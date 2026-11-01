using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Rigbay.Core;

namespace Rigbay.App.Services;

/// <summary>
/// The running instance listens on a named pipe (only this user may connect). A second start sends "show"
/// (the first window comes forward); a command-line verb sends "cmd" with its words and gets the result back.
/// </summary>
sealed class InstanceServer : IDisposable
{
    public static string PipeName => "Rigbay.App." + WindowsIdentity.GetCurrent().User?.Value;

    readonly CancellationTokenSource _cts = new();
    Task? _loop;

    public Action? OnShow { get; set; }
    public Func<IReadOnlyList<string>, Task<(int Exit, string Output)>>? OnCommand { get; set; }

    public void Start() => _loop = Task.Run(Loop);

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
                server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await server.WaitForConnectionAsync(_cts.Token);
                _ = Task.Run(() => Serve(server));
            }
            catch (OperationCanceledException) { server?.Dispose(); return; }
            catch (Exception ex)
            {
                server?.Dispose();
                AppLog.Warn("instance pipe error", ex);
                try { await Task.Delay(500, _cts.Token); } catch { return; }
            }
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                var line = await reader.ReadLineAsync();
                var words = JsonSerializer.Deserialize<string[]>(line ?? "[]") ?? Array.Empty<string>();
                (int, string) result;
                if (words.Length > 0 && words[0] == "show") { OnShow?.Invoke(); result = (0, ""); }
                else if (words.Length > 1 && words[0] == "cmd" && OnCommand != null) result = await OnCommand(words.Skip(1).ToList());
                else result = (2, "unknown request");
                var reply = JsonSerializer.Serialize(new { exit = result.Item1, output = result.Item2 });
                var bytes = new UTF8Encoding(false).GetBytes(reply + "\n");
                await pipe.WriteAsync(bytes);
                await pipe.FlushAsync();
            }
            catch (Exception ex) { AppLog.Warn("instance pipe request failed", ex); }
        }
    }

    /// <summary>Sends a request to a running instance. Returns null when none is listening.</summary>
    public static async Task<(int Exit, string Output)?> SendAsync(string[] words, int connectTimeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(connectTimeoutMs);
            var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(words) + "\n");
            await client.WriteAsync(bytes);
            await client.FlushAsync();
            using var reader = new StreamReader(client, new UTF8Encoding(false));
            var reply = await reader.ReadLineAsync();
            if (reply == null) return null;
            using var doc = JsonDocument.Parse(reply);
            return (doc.RootElement.GetProperty("exit").GetInt32(), doc.RootElement.GetProperty("output").GetString() ?? "");
        }
        catch (Exception) { return null; }
    }

    public void Dispose() { _cts.Cancel(); }
}
