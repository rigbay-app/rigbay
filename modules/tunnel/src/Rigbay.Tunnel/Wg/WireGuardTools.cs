using System.Diagnostics;
using System.Text;

namespace Rigbay.Tunnel.Wg;

/// <summary>Result of running a program through <see cref="IProcessRunner"/>.</summary>
internal sealed record ProcessResult(int ExitCode, string Output, bool TimedOut, bool StartFailed);

/// <summary>Runs a program without a shell and without a window. Arguments are a list, never one string to be parsed.</summary>
internal interface IProcessRunner
{
    /// <param name="keepOutput">True to return standard output; false to discard it (a command that may quote a configuration).</param>
    Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, bool keepOutput, CancellationToken ct);
}

/// <summary>Checks that a file is signed by WireGuard LLC.</summary>
internal interface ISignatureCheck
{
    /// <summary>True when the file has a valid Authenticode signature whose subject is WireGuard LLC.</summary>
    bool IsSignedByWireGuard(string path);
}

/// <summary>
/// The official WireGuard for Windows, driven the way its own app drives it. Nothing is bundled: the programs are the ones the user installed in
/// <c>%ProgramFiles%\WireGuard</c>, and each is checked for the WireGuard LLC signature before it is run as administrator.
/// </summary>
internal sealed class WireGuardTools : IWireGuardTools
{
    internal static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan ShowTimeout = TimeSpan.FromSeconds(10);

    private readonly string _folder;
    private readonly IProcessRunner _runner;
    private readonly ISignatureCheck _signature;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Length, DateTime Stamp)> _verified = new(StringComparer.OrdinalIgnoreCase);

    public WireGuardTools(string folder, IProcessRunner runner, ISignatureCheck signature)
    {
        _folder = folder;
        _runner = runner;
        _signature = signature;
    }

    public string WireGuardExe => Path.Combine(_folder, "wireguard.exe");
    public string WgExe => Path.Combine(_folder, "wg.exe");

    public ToolProbe Probe()
    {
        if (!File.Exists(WireGuardExe) || !File.Exists(WgExe)) return new ToolProbe(false, "probe.no-wireguard");
        return Verify(WireGuardExe) && Verify(WgExe) ? ToolProbe.Yes : new ToolProbe(false, "probe.not-signed");
    }

    /// <summary>The exact argument list of an install: one flag and the path of an existing configuration file.</summary>
    internal static IReadOnlyList<string> InstallArguments(string configPath) => new[] { "/installtunnelservice", configPath };

    /// <summary>The exact argument list of an uninstall: one flag and a validated tunnel name.</summary>
    internal static IReadOnlyList<string> UninstallArguments(string name) => new[] { "/uninstalltunnelservice", name };

    /// <summary>The only three <c>wg</c> invocations this module can make. Never <c>show</c> alone, <c>dump</c> or <c>showconf</c>.</summary>
    internal static IReadOnlyList<string> ShowArguments(string name, WgField field) => new[]
    {
        "show", name,
        field switch
        {
            WgField.LatestHandshakes => "latest-handshakes",
            WgField.Transfer => "transfer",
            WgField.Endpoints => "endpoints",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        },
    };

    public async Task<ToolRun> InstallTunnelServiceAsync(string configPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !Path.IsPathRooted(configPath)) return new ToolRun(-1, StartFailed: true);
        if (!Verify(WireGuardExe)) return new ToolRun(-1, StartFailed: true);
        var r = await _runner.RunAsync(WireGuardExe, InstallArguments(configPath), InstallTimeout, keepOutput: false, ct).ConfigureAwait(false);
        return new ToolRun(r.ExitCode, r.TimedOut, r.StartFailed);
    }

    public async Task<ToolRun> UninstallTunnelServiceAsync(string name, CancellationToken ct)
    {
        if (!TunnelNames.IsValid(name)) return new ToolRun(-1, StartFailed: true);
        if (!Verify(WireGuardExe)) return new ToolRun(-1, StartFailed: true);
        var r = await _runner.RunAsync(WireGuardExe, UninstallArguments(name), InstallTimeout, keepOutput: false, ct).ConfigureAwait(false);
        return new ToolRun(r.ExitCode, r.TimedOut, r.StartFailed);
    }

    public async Task<string?> ShowAsync(string name, WgField field, CancellationToken ct)
    {
        if (!TunnelNames.IsValid(name)) return null;
        if (!Verify(WgExe)) return null;
        var r = await _runner.RunAsync(WgExe, ShowArguments(name, field), ShowTimeout, keepOutput: true, ct).ConfigureAwait(false);
        return r.ExitCode == 0 && !r.TimedOut && !r.StartFailed ? r.Output : null;
    }

    /// <summary>The signature is checked again whenever the file changed (size or time), so a swapped program is not run; an unchanged one is not re-checked on every poll.</summary>
    private bool Verify(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            var stamp = (info.Length, info.LastWriteTimeUtc);
            lock (_gate) if (_verified.TryGetValue(path, out var known) && known == stamp) return true;
            if (!_signature.IsSignedByWireGuard(path)) return false;
            lock (_gate) _verified[path] = stamp;
            return true;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>Real process runner: no shell, no window, arguments as a list, a time limit, output only when asked for.</summary>
internal sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, bool keepOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? process;
        try { process = Process.Start(psi); }
        catch (Exception) { return new ProcessResult(-1, "", false, true); }
        if (process is null) return new ProcessResult(-1, "", false, true);

        using (process)
        {
            try { process.StandardInput.Close(); } catch (Exception) { }
            var stdout = new StringBuilder();
            var readOut = Task.Run(async () =>
            {
                // Read to the end either way so the child never blocks on a full pipe; keep the text only when asked.
                var buffer = new char[4096];
                int n;
                while ((n = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    if (keepOutput && stdout.Length < 64 * 1024) stdout.Append(buffer, 0, n);
            });
            var readErr = Task.Run(async () => { await process.StandardError.ReadToEndAsync().ConfigureAwait(false); });

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                await Task.WhenAll(readOut, readErr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                if (ct.IsCancellationRequested) throw;
                return new ProcessResult(-1, "", true, false);
            }
            catch (TimeoutException) { /* the child is gone; a grandchild kept the pipe open */ }
            return new ProcessResult(process.ExitCode, stdout.ToString(), false, false);
        }
    }
}
