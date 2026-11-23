using System.IO;

namespace FanControl.Services;

/// <summary>Jednoduchý log do %AppData%\Fan-control\log.txt (pro diagnostiku problémů).</summary>
public static class Log
{
    private static readonly object Sync = new();
    private static readonly string FilePath = Path.Combine(SettingsService.Folder, "log.txt");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("CHYBA", ex is null ? message : $"{message} {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(SettingsService.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logování nesmí nikdy shodit aplikaci.
        }
    }
}
