using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FanControl.Services;

public enum PawnIoState
{
    /// <summary>Ovladač v systému není.</summary>
    Missing,
    /// <summary>Ovladač je v systému, ale jeho zařízení nejde otevřít (typicky chybí uzel Root\PawnIO).</summary>
    Broken,
    /// <summary>Ovladač je připravený k použití.</summary>
    Ready,
}

public static class SystemInfo
{
    private const string PawnIoDevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    private const int ErrorAccessDenied = 5;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
                                                     uint creation, uint flags, IntPtr template);

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
    /// Zjistí, jestli PawnIO opravdu funguje. Samotná služba v registru nestačí: když se do systému
    /// dostane jen ovladač bez oficiálního instalátoru, chybí zařízení Root\PawnIO a knihovna pak
    /// tiše čte samé nuly (žádné teploty, frekvence ani příkon CPU).
    /// </summary>
    public static PawnIoState GetPawnIoState()
    {
        if (!IsPawnIoInstalled()) return PawnIoState.Missing;
        try
        {
            using var handle = CreateFileW(PawnIoDevicePath, 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */,
                                           3 /* FILE_SHARE_READ | FILE_SHARE_WRITE */, IntPtr.Zero,
                                           3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (!handle.IsInvalid) return PawnIoState.Ready;
            // Bez práv správce zařízení existuje, jen nás nepustí dovnitř – to je v pořádku.
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorAccessDenied ? PawnIoState.Ready : PawnIoState.Broken;
        }
        catch (Exception ex)
        {
            Log.Error("Kontrola PawnIO selhala.", ex);
            return PawnIoState.Broken;
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
        if (!state.Contains("RUNNING"))
        {
            var (code, output) = RunSc("start PawnIO");
            Log.Info(code == 0 ? "PawnIO spuštěn." : $"PawnIO nelze spustit (kód {code}): {output.Trim()}");
        }
        Log.Info("PawnIO stav: " + GetPawnIoState());
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

    /// <summary>
    /// Nainstaluje (nebo přeinstaluje) PawnIO oficiálním instalátorem přes winget.
    /// <c>--force</c> je tu kvůli opravě: winget jinak odmítne balíček, který už vede jako nainstalovaný.
    /// Vrací stav ovladače po instalaci.
    /// </summary>
    public static async Task<PawnIoState> InstallPawnIoAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("winget",
                "install --id namazso.PawnIO -e --silent --force --accept-package-agreements --accept-source-agreements")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;
            await p.WaitForExitAsync();
            var state = GetPawnIoState();
            Log.Info($"Instalace PawnIO skončila (kód {p.ExitCode}), stav: {state}.");
            return state;
        }
        catch (Exception ex)
        {
            Log.Error("Instalace PawnIO selhala.", ex);
            return GetPawnIoState();
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Nelze otevřít " + url, ex); }
    }
}
