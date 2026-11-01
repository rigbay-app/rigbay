using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Rigbay.Core;

namespace Rigbay.App.Services;

/// <summary>
/// "Start with Windows": a scheduled task named <c>Rigbay</c>, at logon of this user, highest privileges (so no prompt),
/// <c>--tray</c>, 15 s delay. Written as XML because <c>schtasks /create</c> options cannot switch off "do not start on
/// battery", which would silently stop a laptop app from starting when unplugged.
/// </summary>
static class StartupTask
{
    /// <summary>"Rigbay". Only the install tests change it (<c>--task-name</c>) so they never touch the real task.</summary>
    public static string TaskName { get; set; } = "Rigbay";

    public static bool Exists() => Run("/query /tn " + TaskName).ExitCode == 0;

    public static (bool Ok, string Message) Create()
    {
        var exe = Environment.ProcessPath ?? "";
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Rigbay in the tray when {Esc(user)} logs on, elevated, without a prompt.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId><Delay>PT15S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{Esc(exe)}</Command><Arguments>--tray</Arguments><WorkingDirectory>{Esc(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory></Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "rigbay-task-" + Environment.ProcessId + ".xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var r = Run($"/create /tn {TaskName} /xml \"{file}\" /f");
            AppLog.Info("start with Windows: create -> " + r.Output.Trim());
            return (r.ExitCode == 0, r.Output.Trim());
        }
        finally { try { File.Delete(file); } catch { } }
    }

    public static (bool Ok, string Message) Delete()
    {
        if (!Exists()) return (true, "not present");
        var r = Run("/delete /tn " + TaskName + " /f");
        AppLog.Info("start with Windows: delete -> " + r.Output.Trim());
        return (r.ExitCode == 0, r.Output.Trim());
    }

    public static (int ExitCode, string Output) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";
}
