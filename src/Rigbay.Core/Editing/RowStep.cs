using System.Text;

namespace Rigbay.Core;

/// <summary>
/// The argument of a script step that names a row of the open page and maybe a value: <c>edit:&lt;row header key&gt;=&lt;value&gt;</c>,
/// <c>revert:&lt;row header key&gt;</c>, <c>capture:&lt;row header key&gt;=&lt;chord&gt;</c>. The first single '=' separates the key from the
/// value; a '=' that belongs to the key is written twice. A literal header (a module-made name such as the tunnel "example-tunnel", whose key is
/// "=example-tunnel") is therefore written <c>edit:==example-tunnel=on</c>. The value is everything after the separator, taken as it is (it may
/// hold '=', but it cannot start with one: "k==x" is the key "k=x").
/// </summary>
public static class RowStep
{
    /// <summary>Splits <paramref name="arg"/> into the row's header key and the value (null when there is no separator).</summary>
    public static (string Key, string? Value) Parse(string? arg)
    {
        arg ??= "";
        var key = new StringBuilder(arg.Length);
        for (int i = 0; i < arg.Length; i++)
        {
            if (arg[i] != '=') { key.Append(arg[i]); continue; }
            if (i + 1 < arg.Length && arg[i + 1] == '=') { key.Append('='); i++; continue; }
            return (key.ToString(), arg[(i + 1)..]);
        }
        return (key.ToString(), null);
    }

    /// <summary>Writes a header key for a step (each '=' doubled), so that <see cref="Parse"/> gives it back.</summary>
    public static string Escape(string key) => (key ?? "").Replace("=", "==", StringComparison.Ordinal);
}
