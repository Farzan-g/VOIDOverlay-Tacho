using System.Diagnostics;
using System.IO;

namespace VOIDOverlay.Tacho;

public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VOIDOverlay", "Tacho", "logs");

    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}";
        Trace.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(Path.Combine(DirectoryPath, $"tacho-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch (IOException exception)
        {
            Trace.WriteLine($"Unable to write Tacho log: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Trace.WriteLine($"Unable to write Tacho log: {exception.Message}");
        }
    }
}
