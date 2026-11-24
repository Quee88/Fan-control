using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FanControl.Models;

namespace FanControl.Services;

/// <summary>Ukládá nastavení do %AppData%\Fan-control\settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Čeština v souboru čitelně („Tichý“ místo „Tichý“).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _saveLock = new();
    private Timer? _debounce;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fan-control");

    public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            // Poškozený soubor – zazálohujeme ho a začneme s výchozím nastavením.
            Log.Error("Nastavení nelze načíst, používám výchozí.", ex);
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { /* nic */ }
            Current = new AppSettings();
        }

        Current.Normalize();
    }

    /// <summary>Uloží se zpožděním, aby tažení posuvníku nezapisovalo na disk stokrát za sekundu.</summary>
    public void SaveSoon()
    {
        lock (_saveLock)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => SaveNow(), null, 600, Timeout.Infinite);
        }
    }

    public void SaveNow()
    {
        lock (_saveLock)
        {
            _debounce?.Dispose();
            _debounce = null;
            try
            {
                Directory.CreateDirectory(Folder);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Nastavení nelze uložit.", ex);
            }
        }
    }
}
