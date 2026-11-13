namespace Rigbay.Scenes;

/// <summary>The module's settings file. Plain values only. Nothing in it can make a step run: the host runs a scene only when its plan is approved exactly as it is.</summary>
public sealed class ScenesSettings
{
    /// <summary>The scenes, in the order the page shows them.</summary>
    public List<SceneSettings> Scenes { get; set; } = new();
}

/// <summary>One scene: the programs that start it and the steps it runs.</summary>
public sealed class SceneSettings
{
    /// <summary>Stable id (lowercase letters, digits and dashes). It is the plan id the host stores the approval under.</summary>
    public string Id { get; set; } = "";

    /// <summary>What the page, the dialog and the notifications call it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The owner wants this scene to run when its program starts. It still needs an approval that fits the scene as it is now.</summary>
    public bool Enabled { get; set; }

    /// <summary>Executable names (<c>game.exe</c>, matched on the file name, case does not matter) or full paths.</summary>
    public List<string> Games { get; set; } = new();

    /// <summary>The steps, in the order they run. At most 8 (the host's limit).</summary>
    public List<StepSettings> Steps { get; set; } = new();

    /// <summary>
    /// The fingerprint of the programs and steps the owner approved through this page. The host's own approval covers the steps; this covers the programs too
    /// (a scene whose game list was edited asks again, because the writes would then happen at another time).
    /// </summary>
    public string ApprovedFingerprint { get; set; } = "";

    /// <summary>How the last run ended: empty, <c>ran</c>, <c>failed</c>, <c>refused</c> or <c>undo-failed</c>.</summary>
    public string LastKind { get; set; } = "";

    /// <summary>When the last run ended or was refused.</summary>
    public DateTimeOffset? LastAt { get; set; }

    /// <summary>How long the last run lasted, in seconds (for <c>ran</c>).</summary>
    public int LastSeconds { get; set; }

    /// <summary>The host's explanation for <c>failed</c>, <c>refused</c> and <c>undo-failed</c> (English).</summary>
    public string LastDetail { get; set; } = "";

    internal SceneSettings Clone()
    {
        var copy = (SceneSettings)MemberwiseClone();
        copy.Games = new List<string>(Games ?? new());
        copy.Steps = (Steps ?? new()).Select(s => s.Clone()).ToList();
        return copy;
    }
}

/// <summary>One step of a scene: a verb of another module with its exact arguments, and what puts it back.</summary>
public sealed class StepSettings
{
    /// <summary>The module that owns the verb.</summary>
    public string Module { get; set; } = "";

    /// <summary>The verb.</summary>
    public string Verb { get; set; } = "";

    /// <summary>The exact arguments.</summary>
    public List<string> Args { get; set; } = new();

    /// <summary><c>none</c>, <c>recorded</c> (the verb reports what to put back) or <c>fixed</c> (another command, written here).</summary>
    public string Undo { get; set; } = UndoMode.None;

    /// <summary>For a fixed undo: the module.</summary>
    public string UndoModule { get; set; } = "";

    /// <summary>For a fixed undo: the verb.</summary>
    public string UndoVerb { get; set; } = "";

    /// <summary>For a fixed undo: the arguments.</summary>
    public List<string> UndoArgs { get; set; } = new();

    internal StepSettings Clone()
    {
        var copy = (StepSettings)MemberwiseClone();
        copy.Args = new List<string>(Args ?? new());
        copy.UndoArgs = new List<string>(UndoArgs ?? new());
        return copy;
    }
}

/// <summary>The three ways a step is undone.</summary>
public static class UndoMode
{
    /// <summary>Not undone: the owner is told so in the dialog.</summary>
    public const string None = "none";

    /// <summary>The verb reports what to put back and the host runs it at the end.</summary>
    public const string Recorded = "recorded";

    /// <summary>Another command, written in the scene and approved with it.</summary>
    public const string Fixed = "fixed";
}
