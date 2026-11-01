namespace Rigbay.Core.Install;

/// <summary>Why an install, update, removal or registry fetch did not happen. Each code has a host language key <c>core.install.error.&lt;code&gt;</c>.</summary>
public enum InstallErrorCode
{
    Offline,
    RegistryUnreachable,
    RegistryInvalid,
    NotInRegistry,
    NoVersion,
    IncompatibleHostApi,
    AlreadyInstalled,
    NotInstalled,
    AlreadyPending,
    Busy,
    UrlNotAllowed,
    DownloadFailed,
    TooLarge,
    Incomplete,
    SizeMismatch,
    HashMismatch,
    BadZip,
    UnsafeZip,
    ManifestMismatch,
    DeclaresWider,
    FolderSecurity,
    ApplyFailed,
    NothingToRollBack,
    Cancelled,
}

/// <summary>A refusal or failure with a stable code, so the UI can say it in the user's language. <see cref="Detail"/> is English, for the log and the "reason" line.</summary>
public sealed class InstallException : Exception
{
    public InstallException(InstallErrorCode code, string detail, Exception? inner = null) : base(detail, inner)
    {
        Code = code;
    }

    public InstallErrorCode Code { get; }
    public string Detail => Message;

    /// <summary>The host language key of the code (<c>core.install.error.hash-mismatch</c>).</summary>
    public string Key => KeyOf(Code);

    public static string KeyOf(InstallErrorCode code)
    {
        var name = code.ToString();
        var sb = new System.Text.StringBuilder("core.install.error.");
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
