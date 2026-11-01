using Rigbay.Core;

// rigbay-langcheck <lang folder> [--module <id>] [--source <dir>] [--strict] [--out <file>]
//   --module id   the files belong to a module: every key must start with module.<id>.
//   --source dir  scan *.cs files under dir; English keys that no string literal uses are reported as unused
//   --strict      warnings (missing, unused) also make the exit code 1
//   --out file    write the report to a file as well
// Exit codes: 0 ok, 1 errors (or warnings with --strict), 2 bad usage.

const string Usage = "usage: rigbay-langcheck <lang folder> [--module <id>] [--source <dir>] [--strict] [--out <file>]";

string? folder = null, module = null, source = null, outFile = null;
bool strict = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--module" when i + 1 < args.Length: module = args[++i]; break;
        case "--source" when i + 1 < args.Length: source = args[++i]; break;
        case "--out" when i + 1 < args.Length: outFile = args[++i]; break;
        case "--strict": strict = true; break;
        case "--help" or "-h": Console.WriteLine(Usage); return 0;
        default:
            if (args[i].StartsWith("--") || folder is not null) { Console.Error.WriteLine(Usage); return 2; }
            folder = args[i];
            break;
    }
}
if (folder is null || !Directory.Exists(folder))
{
    Console.Error.WriteLine(folder is null ? Usage : $"folder not found: {folder}");
    return 2;
}
if (source is not null && !Directory.Exists(source))
{
    Console.Error.WriteLine($"source folder not found: {source}");
    return 2;
}

IReadOnlyList<string>? sources = null;
if (source is not null)
{
    sources = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .Select(File.ReadAllText).ToList();
}

var files = LangCheck.LoadDirectory(folder);
var report = LangCheck.Check(files, new LangCheckOptions(module is null ? null : LanguageCatalog.ModulePrefix(module), sources));
var text = report.ToText();
Console.Write(text);
if (outFile is not null) File.WriteAllText(outFile, text);
return report.HasErrors || (strict && report.HasWarnings) ? 1 : 0;
