using System.Diagnostics;
using System.IO;
using System.Security;

namespace FanControl.Services;

/// <summary>
/// Spouštění po přihlášení přes Plánovač úloh. Klasický zápis do registru (Run) nejde použít,
/// protože aplikace potřebuje práva správce a Windows by při každém startu ukazoval okno UAC.
/// </summary>
public static class StartupService
{
    private const string TaskName = "Fan-control";

    public static bool IsEnabled()
    {
        var (code, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    public static bool Enable(out string? error)
    {
        error = null;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            error = "Nelze zjistit cestu k aplikaci.";
            return false;
        }

        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Spustí Fan Control po přihlášení (s právy správce, bez dotazu UAC).</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>--autostart</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var tmp = Path.Combine(Path.GetTempPath(), $"fan-control-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            var (code, output) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            if (code != 0)
            {
                error = output.Trim();
                Log.Error("Registrace automatického spuštění selhala: " + error);
                return false;
            }
            return true;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* nic */ }
        }
    }

    public static bool Disable(out string? error)
    {
        error = null;
        if (!IsEnabled()) return true;
        var (code, output) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        if (code == 0) return true;
        error = output.Trim();
        return false;
    }

    private static (int Code, string Output) RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
