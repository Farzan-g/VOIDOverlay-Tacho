using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace VOIDOverlay.Tacho;

public partial class App : System.Windows.Application
{
    private const int WmAppShow = 0x8001;
    private const string MutexName = "Local\\VOIDOverlay.Tacho";
    private Mutex? instanceMutex;
    private SettingsStore? settingsStore;
    private NetworkMonitorService? networkMonitor;
    private PingMonitorService? pingMonitor;
    private SettingsWindow? settingsWindow;
    private MainWindow? overlay;
    private Forms.NotifyIcon? trayIcon;
    private Forms.ContextMenuStrip? trayMenu;
    private Forms.ToolStripMenuItem? visibilityMenuItem;
    private Forms.ToolStripMenuItem? lockMenuItem;
    private Forms.ToolStripMenuItem? startupMenuItem;
    private Icon? trayImage;
    private StartupManager? startupManager;
    private bool ownsInstanceMutex;
    private NetworkSnapshot? lastNetworkSnapshot;
    private LatencySnapshot? lastLatencySnapshot;

    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var firstInstance);
        if (!firstInstance)
        {
            instanceMutex.Dispose();
            instanceMutex = null;
            var existingWindow = FindWindow(null, "VOID Overlay Tacho");
            if (existingWindow != IntPtr.Zero)
                PostMessage(existingWindow, WmAppShow, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        ownsInstanceMutex = true;
        base.OnStartup(e);
        settingsStore = new SettingsStore();
        var settings = settingsStore.Load();
        startupManager = new StartupManager();
        settings.StartWithWindows = startupManager.IsEnabled();
        overlay = new MainWindow(settings);
        settingsWindow = new SettingsWindow(settings, settingsStore, startupManager);
        settingsWindow.SettingsChanged += OnSettingsChanged;
        networkMonitor = new NetworkMonitorService(settings);
        networkMonitor.SnapshotAvailable += OnNetworkSnapshotAvailable;
        pingMonitor = new PingMonitorService(settings);
        pingMonitor.SnapshotAvailable += OnLatencySnapshotAvailable;
        trayIcon = CreateTrayIcon(settings, startupManager);
        if (settings.StartOverlayVisible) overlay.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        networkMonitor?.Dispose();
        networkMonitor = null;
        pingMonitor?.Dispose();
        pingMonitor = null;
        settingsWindow?.CloseForExit();
        trayIcon?.Dispose();
        trayIcon = null;
        trayMenu?.Dispose();
        trayMenu = null;
        trayImage?.Dispose();
        trayImage = null;
        if (ownsInstanceMutex)
        {
            instanceMutex?.ReleaseMutex();
            ownsInstanceMutex = false;
        }
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private Forms.NotifyIcon CreateTrayIcon(AppSettings settings, StartupManager manager)
    {
        var menu = new Forms.ContextMenuStrip();
        trayMenu = menu;
        menu.Items.Add(new Forms.ToolStripMenuItem("VOID Overlay Tacho") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open Settings...", null, (_, _) => ShowSettings());
        visibilityMenuItem = new Forms.ToolStripMenuItem();
        visibilityMenuItem.Click += (_, _) => ToggleOverlay();
        menu.Items.Add(visibilityMenuItem);
        lockMenuItem = new Forms.ToolStripMenuItem("Lock Position") { Checked = settings.IsLocked, CheckOnClick = true };
        lockMenuItem.CheckedChanged += (_, _) =>
        {
            settings.IsLocked = lockMenuItem.Checked;
            settingsWindow?.RefreshLockState();
            SaveSettings();
        };
        menu.Items.Add(lockMenuItem);
        startupMenuItem = new Forms.ToolStripMenuItem("Start with Windows") { Checked = manager.IsEnabled(), CheckOnClick = true };
        startupMenuItem.CheckedChanged += (_, _) =>
        {
            try
            {
                manager.SetEnabled(startupMenuItem.Checked);
                settings.StartWithWindows = manager.IsEnabled();
                SaveSettings();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
            {
                AppLog.Error($"Unable to update user startup registration. {exception.Message}");
                startupMenuItem.Checked = manager.IsEnabled();
            }
        };
        menu.Items.Add(startupMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "Development";
        menu.Items.Add("About VOID Overlay Tacho", null, (_, _) => new AboutWindow(version).ShowDialog());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        var iconResource = GetResourceStream(new Uri("pack://application:,,,/assets/Tacho.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException("Embedded Tacho.ico application resource could not be loaded.");
        using (iconResource.Stream)
        using (var icon = new Icon(iconResource.Stream))
            trayImage = (Icon)icon.Clone();

        var notifyIcon = new Forms.NotifyIcon
        {
            Icon = trayImage,
            Text = "VOID Overlay Tacho",
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => ToggleOverlay();
        UpdateTrayMenu();
        return notifyIcon;
    }

    private void OnSettingsChanged()
    {
        networkMonitor?.RefreshSettings();
        pingMonitor?.RefreshSettings();
        overlay?.ApplyAppearance();
        if (settingsWindow is not null)
        {
            if (lockMenuItem is not null) lockMenuItem.Checked = settingsWindow.Settings.IsLocked;
            if (startupMenuItem is not null) startupMenuItem.Checked = settingsWindow.Settings.StartWithWindows;
        }
        UpdateTrayMenu();
    }

    private void OnNetworkSnapshotAvailable(NetworkSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() =>
        {
            lastNetworkSnapshot = snapshot;
            overlay?.UpdateNetwork(snapshot);
            UpdateTrayStatus();
        });

    private void OnLatencySnapshotAvailable(LatencySnapshot snapshot) =>
        Dispatcher.BeginInvoke(() =>
        {
            lastLatencySnapshot = snapshot;
            overlay?.UpdateLatency(snapshot);
            UpdateTrayStatus();
        });

    private void ToggleOverlay()
    {
        if (overlay is null) return;
        if (overlay.IsVisible) overlay.Hide();
        else overlay.RevealFromTray();
        UpdateTrayMenu();
    }

    private void ShowSettings()
    {
        if (settingsWindow is null) return;
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    private void SaveSettings()
    {
        try
        {
            if (settingsStore is not null && settingsWindow is not null)
            {
                settingsStore.Save(settingsWindow.Settings);
                settingsWindow.RefreshLockState();
                if (lockMenuItem is not null) lockMenuItem.Checked = settingsWindow.Settings.IsLocked;
                if (startupMenuItem is not null) startupMenuItem.Checked = settingsWindow.Settings.StartWithWindows;
                overlay?.ApplyAppearance();
            }
        }
        catch (IOException exception) { AppLog.Error($"Unable to save settings. {exception.Message}"); }
        catch (UnauthorizedAccessException exception) { AppLog.Error($"Unable to save settings. {exception.Message}"); }
    }

    private void UpdateTrayMenu()
    {
        if (visibilityMenuItem is not null && overlay is not null)
            visibilityMenuItem.Text = overlay.IsVisible ? "Hide Overlay" : "Show Overlay";
        if (lockMenuItem is not null && settingsWindow is not null)
            lockMenuItem.Checked = settingsWindow.Settings.IsLocked;
        if (startupMenuItem is not null && startupManager is not null)
            startupMenuItem.Checked = startupManager.IsEnabled();
        UpdateTrayStatus();
    }

    private void UpdateTrayStatus()
    {
        if (trayIcon is null) return;
        var link = lastNetworkSnapshot is null
            ? "Checking link"
            : lastNetworkSnapshot.IsConnected ? "Link up" : "Link down";
        var target = settingsWindow?.Settings.PingEnabled == true
            ? lastLatencySnapshot?.LatencyMs.HasValue == true ? "Ping target reachable" : "Ping target unconfirmed"
            : "Internet unknown";
        trayIcon.Text = $"VOID Overlay Tacho | {link} | {target}";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
}
