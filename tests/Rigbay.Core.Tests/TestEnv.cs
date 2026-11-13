using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.Core.Tests;

/// <summary>A throw-away data root and modules root; copies the built sample and faulty modules into it.</summary>
public sealed class TestEnv : IDisposable
{
    private static readonly string Templates = Path.Combine(AppContext.BaseDirectory, "module-templates");

    public TestEnv()
    {
        Root = Path.Combine(Path.GetTempPath(), "rigbay-tests-" + Guid.NewGuid().ToString("N")[..10]);
        ModulesRoot = Path.Combine(Root, "modules");
        DataRoot = Path.Combine(Root, "data");
        Directory.CreateDirectory(ModulesRoot);
        Directory.CreateDirectory(DataRoot);
    }

    public string Root { get; }
    public string ModulesRoot { get; }
    public string DataRoot { get; }

    /// <summary>Copies a built module (<c>sample</c> or <c>faulty</c>) to the modules root under its id.</summary>
    public string AddModule(string template, string? behavior = null, Func<string, string>? editManifest = null, string? folderName = null)
    {
        var target = Path.Combine(ModulesRoot, folderName ?? template);
        CopyDirectory(Path.Combine(Templates, template), target);
        if (behavior is not null) File.WriteAllText(Path.Combine(target, "behavior.txt"), behavior);
        if (editManifest is not null)
        {
            var path = Path.Combine(target, "module.json");
            File.WriteAllText(path, editManifest(File.ReadAllText(path)));
        }
        return target;
    }

    /// <param name="timeout">Probe, Initialize and Shutdown timeout. <paramref name="probeTimeout"/> overrides the Probe one (it also guards loading and
    /// constructing the module): a test of a hanging Initialize needs a short Initialize timeout and a generous load time, because a starved
    /// machine can take longer than a short timeout just to create the module.</param>
    public RigbayHost NewHost(TimeSpan? timeout = null, long logMaxBytes = 1024 * 1024, TimeSpan? probeTimeout = null) => new(new RigbayHostOptions
    {
        ModulesRoot = ModulesRoot,
        DataRoot = DataRoot,
        FolderSecurity = new Rigbay.Core.Install.NoFolderSecurity(),
        ProbeTimeout = probeTimeout ?? timeout ?? TimeSpan.FromSeconds(10),
        InitializeTimeout = timeout ?? TimeSpan.FromSeconds(10),
        ShutdownTimeout = timeout ?? TimeSpan.FromSeconds(5),
        LogLevel = LogLevel.Debug,
        LogMaxBytes = logMaxBytes,
    });

    /// <summary>Host with the given modules added, discovered, and every supported module enabled.</summary>
    public RigbayHost StartHost(params string[] templates)
    {
        foreach (var t in templates) if (!Directory.Exists(Path.Combine(ModulesRoot, t))) AddModule(t);
        var host = NewHost();
        host.Discover();
        foreach (var m in host.Modules) m.Enable();
        return host;
    }

    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    public void Dispose()
    {
        // Loaded module DLLs stay locked until the process ends (non-collectible contexts); ignore what cannot be deleted.
        try { Directory.Delete(Root, recursive: true); } catch (Exception) { }
    }
}

/// <summary>Collects log text for tests that only need an <see cref="ILog"/>.</summary>
public sealed class ListLog : ILog
{
    public List<string> Lines { get; } = new();

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        lock (Lines) Lines.Add($"{level}: {message}" + (exception is null ? "" : " | " + exception.Message));
    }
}

public sealed class FakeApproval : IApprovalPrompt
{
    public bool Answer { get; set; } = true;
    public List<ApprovalRequest> Requests { get; } = new();

    public Task<bool> ConfirmAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Answer);
    }
}
