using System.IO;
using System.Net;
using System.Text.Json;

namespace VOIDOverlay.Tacho;

public enum OverlayPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

public enum OverlayEdgeReference
{
    WorkArea,
    FullScreen
}

public enum TransferUnit
{
    BytesPerSecond,
    BitsPerSecond
}

public sealed class AppSettings
{
    public OverlayPosition Position { get; set; } = OverlayPosition.TopCenter;
    public OverlayEdgeReference EdgeReference { get; set; } = OverlayEdgeReference.WorkArea;
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public string MonitorDeviceName { get; set; } = string.Empty;
    public int RefreshIntervalMs { get; set; } = 500;
    public double FontSize { get; set; } = 13;
    public double Opacity { get; set; } = 1;
    public bool BackgroundEnabled { get; set; }
    public string BackgroundColor { get; set; } = "#111720";
    public double BackgroundOpacity { get; set; } = 0.5;
    public string TextColor { get; set; } = "#F2F4F7";
    public string AccentColor { get; set; } = "#4DE3D2";
    public string SecondaryAccentColor { get; set; } = "#FFAE5C";
    public bool ClickThrough { get; set; } = true;
    public bool ShowDownload { get; set; } = true;
    public bool ShowUpload { get; set; } = true;
    public bool ShowPing { get; set; } = true;
    public bool ShowJitter { get; set; }
    public bool ShowPacketLoss { get; set; }
    public bool ShowSessionData { get; set; }
    public bool PingEnabled { get; set; }
    public string PingTarget { get; set; } = "1.1.1.1";
    public int PingIntervalMs { get; set; } = 10000;
    public int PingTimeoutMs { get; set; } = 2000;
    public string InterfaceId { get; set; } = string.Empty;
    public TransferUnit TransferUnit { get; set; } = TransferUnit.BytesPerSecond;
    public bool StartWithWindows { get; set; }
    public bool StartOverlayVisible { get; set; } = true;
    public bool KeepOnTop { get; set; } = true;
    public bool IsLocked { get; set; } = true;

    public static AppSettings Defaults() => new();

    public void Validate()
    {
        if (!Enum.IsDefined(Position)) Position = OverlayPosition.TopCenter;
        if (!Enum.IsDefined(EdgeReference)) EdgeReference = OverlayEdgeReference.WorkArea;
        if (!Enum.IsDefined(TransferUnit)) TransferUnit = TransferUnit.BytesPerSecond;
        if (RefreshIntervalMs is not (250 or 500 or 750 or 1000 or 2000 or 5000)) RefreshIntervalMs = 500;
        if (PingIntervalMs is not (5000 or 10000 or 30000 or 60000)) PingIntervalMs = 10000;
        if (PingTimeoutMs is not (1000 or 2000 or 5000)) PingTimeoutMs = 2000;
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 9, 24) : 13;
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.3, 1) : 1;
        BackgroundOpacity = double.IsFinite(BackgroundOpacity) ? Math.Clamp(BackgroundOpacity, 0, 1) : 0.5;
        OffsetX = Math.Clamp(OffsetX, -200, 1000);
        OffsetY = Math.Clamp(OffsetY, -200, 1000);
        MonitorDeviceName ??= string.Empty;
        InterfaceId ??= string.Empty;
        PingTarget = PingTargetValidation.IsValid(PingTarget) ? PingTarget!.Trim() : "1.1.1.1";
        BackgroundColor = ValidColorOr(BackgroundColor, "#111720");
        TextColor = ValidColorOr(TextColor, "#F2F4F7");
        AccentColor = ValidColorOr(AccentColor, "#4DE3D2");
        SecondaryAccentColor = ValidColorOr(SecondaryAccentColor, "#FFAE5C");
    }

    private static string ValidColorOr(string? value, string fallback) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit)
            ? value.ToUpperInvariant()
            : fallback;
}

public static class PingTargetValidation
{
    public static bool IsValid(string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        target.Trim().Length <= 253 &&
        (IPAddress.TryParse(target.Trim(), out _) || Uri.CheckHostName(target.Trim()) == UriHostNameType.Dns);
}

public sealed class SettingsStore(string? settingsPath = null)
{
    private readonly string path = settingsPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VOIDOverlay", "Tacho", "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(path))
        {
            return AppSettings.Defaults();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                ?? throw new JsonException("The settings document must contain an object.");
            settings.Validate();
            return settings;
        }
        catch (JsonException exception)
        {
            AppLog.Warning($"Settings could not be parsed; defaults were loaded. {exception.Message}");
        }
        catch (IOException exception)
        {
            AppLog.Error($"Settings could not be read; defaults were loaded. {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            AppLog.Error($"Settings could not be read; defaults were loaded. {exception.Message}");
        }

        return AppSettings.Defaults();
    }

    public void Save(AppSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
