using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace VOIDOverlay.Tacho;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmAppShow = 0x8001;
    private readonly AppSettings settings;
    private NetworkSnapshot? networkSnapshot;
    private LatencySnapshot? latencySnapshot;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        this.settings = settings;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => PositionService.Apply(this, this.settings);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        ApplyAppearance();
        new WindowInteropHelper(this).EnsureHandle();
        PositionService.Apply(this, settings);
    }

    public void UpdateNetwork(NetworkSnapshot snapshot)
    {
        networkSnapshot = snapshot;
        RenderMetrics();
    }

    public void UpdateLatency(LatencySnapshot snapshot)
    {
        latencySnapshot = snapshot;
        RenderMetrics();
    }

    public void ApplyAppearance()
    {
        FontSize = settings.FontSize;
        Opacity = settings.Opacity;
        Topmost = settings.KeepOnTop;
        OverlaySurface.Background = CreateBrush(settings.BackgroundColor, settings.BackgroundOpacity, settings.BackgroundEnabled);
        ApplyClickThroughState();
        RenderMetrics();
        PositionService.Apply(this, settings);
    }

    public void RevealFromTray()
    {
        if (!IsVisible) Show();
        Topmost = settings.KeepOnTop;
        PositionService.Apply(this, settings);
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        base.OnClosed(e);
    }

    private void RenderMetrics()
    {
        var separator = CreateBrush(settings.TextColor, 0.5);
        var metrics = new List<(string Text, string Color)>();
        if (settings.ShowDownload)
            metrics.Add(($"↓ {TransferFormatter.Format(networkSnapshot?.DownloadBytesPerSecond, settings.TransferUnit)}", settings.AccentColor));
        if (settings.ShowUpload)
            metrics.Add(($"↑ {TransferFormatter.Format(networkSnapshot?.UploadBytesPerSecond, settings.TransferUnit)}", settings.SecondaryAccentColor));
        if (settings.ShowPing)
        {
            var ping = latencySnapshot?.LatencyMs is double latency
                ? latency < 1 ? "<1 ms" : $"{latency:F0} ms"
                : "—";
            metrics.Add((ping, settings.TextColor));
        }
        if (settings.ShowJitter)
            metrics.Add((latencySnapshot?.JitterMs is double jitter ? $"J {jitter:F1} ms" : "J —", settings.TextColor));
        if (settings.ShowPacketLoss)
            metrics.Add((latencySnapshot?.PacketLossPercent is double loss ? $"Ping loss {loss:F0}%" : "Ping loss —", settings.TextColor));
        if (settings.ShowSessionData)
        {
            var down = networkSnapshot?.SessionDownloadedBytes ?? 0;
            var up = networkSnapshot?.SessionUploadedBytes ?? 0;
            metrics.Add(($"Σ {FormatBytes(down)} / {FormatBytes(up)}", settings.TextColor));
        }
        if (networkSnapshot is { IsConnected: false })
            metrics.Add(("NIC offline", settings.SecondaryAccentColor));

        MetricsPanel.Children.Clear();
        for (var index = 0; index < metrics.Count; index++)
        {
            if (index > 0)
                MetricsPanel.Children.Add(new TextBlock { Text = "|", Foreground = separator, Margin = new Thickness(0, 0, 8, 0) });
            MetricsPanel.Children.Add(new TextBlock
            {
                Text = metrics[index].Text,
                Foreground = CreateBrush(metrics[index].Color),
                Margin = new Thickness(0, 0, index < metrics.Count - 1 ? 8 : 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:F1} {units[index]}";
    }

    private static SolidColorBrush CreateBrush(string color, double opacity = 1, bool enabled = true)
    {
        if (!enabled) return System.Windows.Media.Brushes.Transparent;
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Opacity = opacity;
        brush.Freeze();
        return brush;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var styles = GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExToolWindow | WsExNoActivate;
        styles = settings.ClickThrough ? styles | WsExTransparent : styles & ~WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(styles));
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessageHook);
    }

    private void ApplyClickThroughState()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var styles = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        styles = settings.ClickThrough ? styles | WsExTransparent : styles & ~WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(styles));
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmAppShow)
        {
            RevealFromTray();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => PositionService.Apply(this, settings));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
