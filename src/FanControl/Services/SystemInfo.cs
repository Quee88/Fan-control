using System.Diagnostics;
using System.Security.Principal;

namespace FanControl.Services;

public static class SystemInfo
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// PawnIO je ovladač, přes který knihovna čte teploty CPU a ovládá větráky na základní desce.
    /// Grafické karty NVIDIA/AMD ho nepotřebují.
    /// </summary>
    public static bool IsPawnIoInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ovladač PawnIO se instaluje se spouštěním „na vyžádání“ – pokud neběží, spustíme ho
    /// (vyžaduje práva správce). Bez něj knihovna nevidí teploty CPU ani čip na desce.
    /// </summary>
    public static void EnsurePawnIoRunning()
    {
        if (!IsAdministrator() || !IsPawnIoInstalled()) return;
        var (_, state) = RunSc("query PawnIO");
        Log.Info("PawnIO: " + (state.Contains("RUNNING") ? "běží" : "neběží"));
        if (state.Contains("RUNNING")) return;
        var (code, output) = RunSc("start PawnIO");
        Log.Info(code == 0 ? "PawnIO spuštěn." : $"PawnIO nelze spustit (kód {code}): {output.Trim()}");
    }

    private static (int Code, string Output) RunSc(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Nainstaluje PawnIO přes winget. Vrací true při úspěchu.</summary>
    public static async Task<bool> InstallPawnIoAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("winget",
                "install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;
            await p.WaitForExitAsync();
            return IsPawnIoInstalled();
        }
        catch (Exception ex)
        {
            Log.Error("Instalace PawnIO selhala.", ex);
            return false;
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Nelze otevřít " + url, ex); }
    }
}
