namespace Rigbay.Core.Install;

/// <summary>
/// Where the installer works. Everything lives in one folder next to the modules folder (<c>modules.install</c>), never inside it,
/// because the loader treats every folder inside the modules folder as a module.
/// </summary>
public sealed class InstallPaths
{
    public InstallPaths(string modulesRoot)
    {
        ModulesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modulesRoot));
        Root = ModulesRoot + ".install";
    }

    public string ModulesRoot { get; }
    public string Root { get; }

    /// <summary>The zip being downloaded (<c>*.part</c>) lives here; emptied at every start.</summary>
    public string Downloads => Path.Combine(Root, "downloads");

    /// <summary>A zip being unpacked and checked; emptied at every start.</summary>
    public string Staging => Path.Combine(Root, "staging");

    /// <summary>Verified modules waiting for the next start: <c>ready\&lt;id&gt;</c>.</summary>
    public string Ready => Path.Combine(Root, "ready");

    /// <summary>The version that was replaced or removed, kept until the next healthy start: <c>previous\&lt;id&gt;</c>.</summary>
    public string Previous => Path.Combine(Root, "previous");

    /// <summary>A folder that is being thrown away after a rename; emptied at every start.</summary>
    public string Trash => Path.Combine(Root, "trash");

    public string RegistryCache => Path.Combine(Root, "registry");
    public string PendingFile => Path.Combine(Root, "pending.json");
    public string LastRunFile => Path.Combine(Root, "last-run.json");

    public string ModuleFolder(string id) => Path.Combine(ModulesRoot, id);
    public string ReadyFolder(string id) => Path.Combine(Ready, id);
    public string PreviousFolder(string id) => Path.Combine(Previous, id);

    /// <summary>
    /// Gives the modules folder and the install folder the admin-only permissions (and <c>%ProgramData%\Rigbay</c> itself when that is their
    /// parent: a standard user must not be able to replace the folder that holds them), then creates the work folders below.
    /// Idempotent; throws <see cref="InstallException"/> (FolderSecurity) when the permissions cannot be made right.
    /// </summary>
    public void Prepare(IFolderSecurity security)
    {
        var parent = Path.GetDirectoryName(ModulesRoot);
        if (parent is not null && string.Equals(Path.GetFileName(parent), "Rigbay", StringComparison.OrdinalIgnoreCase)) security.EnsureAdminOnly(parent);
        security.EnsureAdminOnly(ModulesRoot);
        security.EnsureAdminOnly(Root);
        foreach (var d in new[] { Downloads, Staging, Ready, Previous, Trash, RegistryCache }) Directory.CreateDirectory(d);
    }
}
