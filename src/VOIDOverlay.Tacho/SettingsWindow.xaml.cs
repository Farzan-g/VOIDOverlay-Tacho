using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace VOIDOverlay.Tacho;

public partial class SettingsWindow : Window
{
    private readonly AppSettings settings;
    private readonly SettingsStore store;
    private readonly StartupManager startupManager;
    private readonly DispatcherTimer saveTimer;
    private bool initialized;
    private bool allowClose;

    public event Action? SettingsChanged;
    public AppSettings Settings => settings;

    public SettingsWindow(AppSettings settings, SettingsStore store, StartupManager startupManager)
    {
        InitializeComponent();
        this.settings = settings;
        this.store = store;
        this.startupManager = startupManager;
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); PersistSettings(); };
        PositionCombo.ItemsSource = Enum.GetValues<OverlayPosition>();
        MonitorCombo.ItemsSource = Forms.Screen.AllScreens
            .Select(screen => new MonitorOption(screen.DeviceName, $"{screen.DeviceName} {(screen.Primary ? "(Primary)" : string.Empty)}"))
            .ToArray();
        LoadSettings();
        WireChanges();
    }

    public void CloseForExit()
    {
        allowClose = true;
        saveTimer.Stop();
        PersistSettings();
        Close();
    }

    public void RefreshLockState()
    {
        LockedCheck.IsChecked = settings.IsLocked;
        PositionControls.IsEnabled = !settings.IsLocked;
    }

    private void LoadSettings()
    {
        initialized = false;
        StartupCheck.IsChecked = startupManager.IsEnabled();
        TopmostCheck.IsChecked = settings.KeepOnTop;
        LockedCheck.IsChecked = settings.IsLocked;
        ClickThroughCheck.IsChecked = settings.ClickThrough;
        StartVisibleCheck.IsChecked = settings.StartOverlayVisible;
        BackgroundEnabledCheck.IsChecked = settings.BackgroundEnabled;
        BackgroundColorBox.Text = settings.BackgroundColor;
        BackgroundOpacitySlider.Value = settings.BackgroundOpacity;
        OverlayOpacitySlider.Value = settings.Opacity;
        TextColorBox.Text = settings.TextColor;
        AccentColorBox.Text = settings.AccentColor;
        SecondaryAccentColorBox.Text = settings.SecondaryAccentColor;
        FontSizeSlider.Value = settings.FontSize;
        PositionCombo.SelectedItem = settings.Position;
        MonitorCombo.SelectedItem = MonitorCombo.Items.Cast<MonitorOption>()
            .FirstOrDefault(item => item.DeviceName.Equals(settings.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? MonitorCombo.Items.Cast<MonitorOption>().FirstOrDefault(item => item.DeviceName == (Forms.Screen.PrimaryScreen?.DeviceName ?? string.Empty));
        EdgeReferenceCombo.SelectedIndex = settings.EdgeReference == OverlayEdgeReference.WorkArea ? 0 : 1;
        OffsetXBox.Text = settings.OffsetX.ToString(CultureInfo.InvariantCulture);
        OffsetYBox.Text = settings.OffsetY.ToString(CultureInfo.InvariantCulture);
        InterfaceChoice[] adapters;
        try
        {
            adapters = new WindowsNetworkDataProvider().GetInterfaces()
                .Select(adapter => new InterfaceChoice(adapter.Id,
                    $"{adapter.Name} ({adapter.Type}, {adapter.Status}{(adapter.IsLikelyVirtual ? ", virtual" : string.Empty)})"))
                .Prepend(new InterfaceChoice(string.Empty, "Automatic (highest-speed active adapter)"))
                .ToArray();
            InterfaceCombo.ToolTip = null;
        }
        catch (NetworkInformationException exception)
        {
            AppLog.Error($"Network adapter details could not be read in Settings. {exception.Message}");
            adapters = [new InterfaceChoice(string.Empty, "Automatic (adapter details unavailable)")];
            InterfaceCombo.ToolTip = "Windows could not enumerate network adapters. Automatic selection will retry.";
        }
        InterfaceCombo.ItemsSource = adapters;
        InterfaceCombo.SelectedValuePath = nameof(InterfaceChoice.Id);
        InterfaceCombo.SelectedValue = settings.InterfaceId;
        DownloadCheck.IsChecked = settings.ShowDownload;
        UploadCheck.IsChecked = settings.ShowUpload;
        PingVisibleCheck.IsChecked = settings.ShowPing;
        JitterCheck.IsChecked = settings.ShowJitter;
        PacketLossCheck.IsChecked = settings.ShowPacketLoss;
        SessionDataCheck.IsChecked = settings.ShowSessionData;
        PingEnabledCheck.IsChecked = settings.PingEnabled;
        PingTargetBox.Text = settings.PingTarget;
        UnitCombo.SelectedValue = settings.TransferUnit.ToString();
        IntervalCombo.SelectedValue = settings.RefreshIntervalMs.ToString(CultureInfo.InvariantCulture);
        PingIntervalCombo.SelectedValue = settings.PingIntervalMs.ToString(CultureInfo.InvariantCulture);
        PingTimeoutCombo.SelectedValue = settings.PingTimeoutMs.ToString(CultureInfo.InvariantCulture);
        PositionControls.IsEnabled = !settings.IsLocked;
        initialized = true;
    }

    private void WireChanges()
    {
        StartupCheck.Checked += StartupChanged;
        StartupCheck.Unchecked += StartupChanged;
        foreach (var check in new[] { TopmostCheck, LockedCheck, ClickThroughCheck, StartVisibleCheck, BackgroundEnabledCheck,
                     DownloadCheck, UploadCheck, PingVisibleCheck, JitterCheck, PacketLossCheck, SessionDataCheck, PingEnabledCheck })
        {
            check.Checked += SettingsChangedHandler;
            check.Unchecked += SettingsChangedHandler;
        }
        foreach (var slider in new[] { BackgroundOpacitySlider, OverlayOpacitySlider, FontSizeSlider })
            slider.ValueChanged += SettingsChangedHandler;
        foreach (var box in new[] { BackgroundColorBox, TextColorBox, AccentColorBox, SecondaryAccentColorBox, PingTargetBox })
            box.LostFocus += SettingsChangedHandler;
        foreach (var combo in new[] { PositionCombo, MonitorCombo, EdgeReferenceCombo, InterfaceCombo, UnitCombo, IntervalCombo,
                     PingIntervalCombo, PingTimeoutCombo })
            combo.SelectionChanged += SettingsChangedHandler;
        OffsetXBox.LostFocus += SettingsChangedHandler;
        OffsetYBox.LostFocus += SettingsChangedHandler;
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        try
        {
            startupManager.SetEnabled(StartupCheck.IsChecked == true);
            ApplySettings();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            AppLog.Error($"Unable to update current-user startup registration. {exception.Message}");
            StartupCheck.IsChecked = startupManager.IsEnabled();
            System.Windows.MessageBox.Show(this, "Windows startup registration could not be updated. See the Tacho log for details.",
                "VOID Overlay Tacho", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SettingsChangedHandler(object sender, RoutedEventArgs e) => ApplySettings();

    private void ApplySettings()
    {
        if (!initialized) return;
        if (!TryReadColor(BackgroundColorBox, out var background) ||
            !TryReadColor(TextColorBox, out var text) ||
            !TryReadColor(AccentColorBox, out var accent) ||
            !TryReadColor(SecondaryAccentColorBox, out var secondary))
        {
            return;
        }

        if (!TryParseOffset(OffsetXBox.Text, out var offsetX))
        {
            OffsetXBox.ToolTip = "Enter a whole number from -200 to 1000 DIPs.";
            return;
        }
        if (!TryParseOffset(OffsetYBox.Text, out var offsetY))
        {
            OffsetYBox.ToolTip = "Enter a whole number from -200 to 1000 DIPs.";
            return;
        }
        if (!PingTargetValidation.IsValid(PingTargetBox.Text))
        {
            PingTargetBox.ToolTip = "Enter a host name or IP address (up to 253 characters).";
            return;
        }

        OffsetXBox.ToolTip = null;
        OffsetYBox.ToolTip = null;
        PingTargetBox.ToolTip = null;
        settings.StartWithWindows = startupManager.IsEnabled();
        settings.KeepOnTop = TopmostCheck.IsChecked == true;
        settings.IsLocked = LockedCheck.IsChecked == true;
        settings.ClickThrough = ClickThroughCheck.IsChecked == true;
        settings.StartOverlayVisible = StartVisibleCheck.IsChecked == true;
        settings.BackgroundEnabled = BackgroundEnabledCheck.IsChecked == true;
        settings.BackgroundColor = background;
        settings.BackgroundOpacity = BackgroundOpacitySlider.Value;
        settings.Opacity = OverlayOpacitySlider.Value;
        settings.TextColor = text;
        settings.AccentColor = accent;
        settings.SecondaryAccentColor = secondary;
        settings.FontSize = FontSizeSlider.Value;
        settings.ShowDownload = DownloadCheck.IsChecked == true;
        settings.ShowUpload = UploadCheck.IsChecked == true;
        settings.ShowPing = PingVisibleCheck.IsChecked == true;
        settings.ShowJitter = JitterCheck.IsChecked == true;
        settings.ShowPacketLoss = PacketLossCheck.IsChecked == true;
        settings.ShowSessionData = SessionDataCheck.IsChecked == true;
        settings.PingEnabled = PingEnabledCheck.IsChecked == true;
        settings.PingTarget = PingTargetBox.Text.Trim();
        settings.OffsetX = offsetX;
        settings.OffsetY = offsetY;
        if (PositionCombo.SelectedItem is OverlayPosition position) settings.Position = position;
        if (MonitorCombo.SelectedItem is MonitorOption monitor) settings.MonitorDeviceName = monitor.DeviceName;
        if (InterfaceCombo.SelectedValue is string interfaceId) settings.InterfaceId = interfaceId;
        settings.EdgeReference = EdgeReferenceCombo.SelectedIndex == 0 ? OverlayEdgeReference.WorkArea : OverlayEdgeReference.FullScreen;
        if (UnitCombo.SelectedValue is string unit && Enum.TryParse<TransferUnit>(unit, out var transferUnit))
            settings.TransferUnit = transferUnit;
        settings.RefreshIntervalMs = ReadSelectedInt(IntervalCombo, settings.RefreshIntervalMs);
        settings.PingIntervalMs = ReadSelectedInt(PingIntervalCombo, settings.PingIntervalMs);
        settings.PingTimeoutMs = ReadSelectedInt(PingTimeoutCombo, settings.PingTimeoutMs);
        settings.Validate();
        PositionControls.IsEnabled = !settings.IsLocked;
        SettingsChanged?.Invoke();
        saveTimer.Stop();
        saveTimer.Start();
    }

    private static int ReadSelectedInt(System.Windows.Controls.ComboBox combo, int fallback) =>
        combo.SelectedValue is string value && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : fallback;

    private static bool TryReadColor(System.Windows.Controls.TextBox box, out string value)
    {
        value = box.Text.Trim();
        var valid = value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
        if (valid)
        {
            value = value.ToUpperInvariant();
            box.ToolTip = null;
            return true;
        }
        box.ToolTip = "Enter a six-digit hex color such as #4DE3D2.";
        return false;
    }

    private static bool TryParseOffset(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
        value is >= -200 and <= 1000;

    private void PersistSettings()
    {
        try { store.Save(settings); }
        catch (IOException exception) { AppLog.Error($"Unable to save settings. {exception.Message}"); }
        catch (UnauthorizedAccessException exception) { AppLog.Error($"Unable to save settings. {exception.Message}"); }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var defaults = AppSettings.Defaults();
        defaults.StartWithWindows = startupManager.IsEnabled();
        defaults.MonitorDeviceName = Forms.Screen.PrimaryScreen?.DeviceName ?? string.Empty;
        CopySettings(defaults, settings);
        LoadSettings();
        ApplySettings();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        saveTimer.Stop();
        PersistSettings();
        Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        saveTimer.Stop();
        PersistSettings();
        if (!allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private static void CopySettings(AppSettings source, AppSettings destination)
    {
        var properties = typeof(AppSettings).GetProperties().Where(property => property.CanWrite);
        foreach (var property in properties) property.SetValue(destination, property.GetValue(source));
    }

    private sealed record MonitorOption(string DeviceName, string Name);
    private sealed record InterfaceChoice(string Id, string Name);
}
