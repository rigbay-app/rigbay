using System.Text.RegularExpressions;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>A tray item with its owner and a guarded way to invoke it.</summary>
public sealed record TrayEntry(string ModuleId, TrayItem Item, Action Invoke);

/// <summary>Tray menu items as data. The app draws them (grouped by module) and calls <see cref="TrayEntry.Invoke"/>.</summary>
public sealed class TrayRegistry
{
    private static readonly Regex IdPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly List<TrayEntry> _entries = new();

    public event Action? Changed;

    public IReadOnlyList<TrayEntry> Items
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public void Add(string moduleId, TrayItem item, Action guardedInvoke)
    {
        if (!IdPattern.IsMatch(item.Id ?? "")) throw new ArgumentException($"'{item.Id}' is not a valid tray item id (lowercase letters, digits, dashes).", nameof(item));
        lock (_gate)
        {
            if (_entries.Any(e => e.ModuleId == moduleId && e.Item.Id == item.Id))
                throw new ArgumentException($"Tray item '{item.Id}' is already registered by module '{moduleId}'.", nameof(item));
            _entries.Add(new TrayEntry(moduleId, item, guardedInvoke));
        }
        Changed?.Invoke();
    }

    public void Remove(string moduleId, string id)
    {
        lock (_gate) _entries.RemoveAll(e => e.ModuleId == moduleId && e.Item.Id == id);
        Changed?.Invoke();
    }

    public void RemoveModule(string moduleId)
    {
        lock (_gate) _entries.RemoveAll(e => e.ModuleId == moduleId);
        Changed?.Invoke();
    }
}

/// <summary>A command verb of a module.</summary>
public sealed record CommandEntry(string ModuleId, string Verb, string DescriptionKey, CommandHandler Handler)
{
    /// <summary>What the module said about the verb (host API 0.1.4); null when it registered the old way (then the access is <see cref="VerbAccess.Unknown"/>).</summary>
    public CommandInfo? Info { get; init; }

    /// <summary>What the verb does to the machine, as its module declared it.</summary>
    public VerbAccess Access => Info?.Access ?? VerbAccess.Unknown;
}

/// <summary>What a command run produced. Output goes to standard output for exit code 0, to standard error otherwise.</summary>
public sealed record CommandOutcome(int ExitCode, string Output)
{
    /// <summary>The undo arguments the verb reported (<see cref="CommandResult.UndoArgs"/>), or null.</summary>
    public IReadOnlyList<string>? UndoArgs { get; init; }

    public const int Ok = 0, Failed = 1, UsageOrNotFound = 2;
}

/// <summary>Registry of <c>rigbay &lt;module&gt; &lt;verb&gt;</c> commands (parse and dispatch; the host adds the "is the module running" check).</summary>
public sealed class CommandRegistry
{
    private static readonly Regex VerbPattern = new("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly List<CommandEntry> _entries = new();

    public IReadOnlyList<CommandEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public void Register(string moduleId, string verb, string descriptionKey, CommandHandler handler, CommandInfo? info = null)
    {
        if (info is { Access: VerbAccess.Writes, Effect: null }) throw new ArgumentException($"Verb '{verb}' is declared to write but has no Effect (the owner must be told what it changes).", nameof(info));
        if (info is { UndoVerb: { } undo } && !VerbPattern.IsMatch(undo)) throw new ArgumentException($"'{undo}' is not a valid undo verb.", nameof(info));
        if (verb is null || !VerbPattern.IsMatch(verb)) throw new ArgumentException($"'{verb}' is not a valid verb (lowercase letters, digits, dashes; starts with a letter).", nameof(verb));
        lock (_gate)
        {
            if (_entries.Any(e => e.ModuleId == moduleId && e.Verb == verb))
                throw new ArgumentException($"Verb '{verb}' is already registered by module '{moduleId}'.", nameof(verb));
            _entries.Add(new CommandEntry(moduleId, verb, descriptionKey, handler) { Info = info });
        }
    }

    public void RemoveModule(string moduleId)
    {
        lock (_gate) _entries.RemoveAll(e => e.ModuleId == moduleId);
    }

    public IReadOnlyList<CommandEntry> VerbsOf(string moduleId)
    {
        lock (_gate) return _entries.Where(e => e.ModuleId == moduleId).OrderBy(e => e.Verb, StringComparer.Ordinal).ToArray();
    }

    public CommandEntry? Find(string moduleId, string verb)
    {
        lock (_gate) return _entries.FirstOrDefault(e => e.ModuleId == moduleId && e.Verb == verb);
    }

    /// <summary>Runs a handler and normalises the exit code to 0, 1 or 2. An exception becomes exit code 1 (the module's guard has already faulted it).</summary>
    public static async Task<CommandOutcome> RunAsync(CommandEntry entry, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var result = await entry.Handler(new CommandContext(args, cancellationToken)).ConfigureAwait(false);
            int code = result.ExitCode is 0 or 1 or 2 ? result.ExitCode : 1;
            return new CommandOutcome(code, result.Output ?? "") { UndoArgs = code == 0 ? result.UndoArgs : null };
        }
        catch (Exception ex)
        {
            return new CommandOutcome(CommandOutcome.Failed, $"{entry.ModuleId} {entry.Verb}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
