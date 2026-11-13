using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>What running a verb for another module returned.</summary>
internal sealed record InvokeResult(VerbOutcome Outcome, int ExitCode, string Output, IReadOnlyList<string>? UndoArgs = null);

/// <summary>
/// Runs a verb of one module on behalf of another (or of the host). The verb already runs in its own module's fault boundary (the command wrapper
/// in <see cref="ModuleHostServices"/>); this adds the time limit, the cancellation that follows both modules, and the <see cref="CallScope"/>.
/// </summary>
internal static class VerbInvoker
{
    public static async Task<InvokeResult> RunAsync(RigbayHost host, ModuleEntry? caller, ModuleEntry target, CommandEntry command, IReadOnlyList<string> args,
        CallScope scope, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, host.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, target.StopToken, caller?.StopToken ?? CancellationToken.None);

        Task<CommandOutcome> task;
        var before = CallScope.Current;
        CallScope.Current = scope;   // the verb's own work inherits it (AsyncLocal); it is closed when the call ends
        try { task = CommandRegistry.RunAsync(command, args, linked.Token); }
        finally { CallScope.Current = before; }

        try
        {
            var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            var first = await Task.WhenAny(task, cancelled).ConfigureAwait(false);
            if (first != task)
            {
                scope.Close();
                _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);   // nobody awaits it any more
                if (timeoutCts.IsCancellationRequested) return new InvokeResult(VerbOutcome.TimedOut, -1, $"{target.Id} {command.Verb} did not answer within {timeout.TotalSeconds:0.#} s");
                return new InvokeResult(VerbOutcome.Cancelled, -1, "cancelled");
            }
            var outcome = await task.ConfigureAwait(false);
            scope.Close();
            // a verb that ends by the cancellation can finish a moment before the wait notices it: that is still a time-out or a cancel, not an ordinary failure
            if (linked.IsCancellationRequested && outcome.ExitCode != 0 && target.Lifecycle != ModuleLifecycle.Faulted)
                return timeoutCts.IsCancellationRequested
                    ? new InvokeResult(VerbOutcome.TimedOut, -1, $"{target.Id} {command.Verb} did not answer within {timeout.TotalSeconds:0.#} s")
                    : new InvokeResult(VerbOutcome.Cancelled, -1, "cancelled");
            if (target.Lifecycle == ModuleLifecycle.Faulted) return new InvokeResult(VerbOutcome.CalleeFaulted, -1, outcome.Output);
            return new InvokeResult(outcome.ExitCode switch { 0 => VerbOutcome.Done, 2 => VerbOutcome.Usage, _ => VerbOutcome.Failed }, outcome.ExitCode, outcome.Output, outcome.UndoArgs);
        }
        finally { scope.Close(); }
    }
}
