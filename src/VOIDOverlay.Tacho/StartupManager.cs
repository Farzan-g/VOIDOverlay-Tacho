using Microsoft.Win32;

namespace VOIDOverlay.Tacho;

public sealed class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VOIDOverlay.Tacho";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string command &&
            command.Contains("VOIDOverlay.Tacho", StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the current user's startup registry key.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine the application executable path.");
        key.SetValue(ValueName, $"\"{executable}\"");
    }
}
