using System.Globalization;
using Rigbay.Abstractions;
using Rigbay.SteamBypass.Check;

namespace Rigbay.SteamBypass;

public sealed partial class SteamBypassModule
{
    /// <summary>How a run of the four checks ended.</summary>
    internal sealed record CheckRun(int Total, int Failed, IReadOnlyList<CheckResult> Results);

    /// <summary>
    /// Recomputes every text and flag the page shows from the current state. Called after a tick, a switch, a language change.
    /// Cheap, and safe from any thread.
    /// </summary>
    private void RefreshTexts()
    {
        var host = _host;
        var controller = _controller;
        var engine = _engine;
        if (host is null || controller is null || engine is null) return;
        var loc = host.Localisation;
        bool on = controller.IsOn;
        var counters = engine.Counters;

        _listenText.Value = on ? ListenText(loc, engine) : loc.T("listen.off");
        _port80Warning.Value = on && engine.DownloadsError is not null;
        _steamRestartHint.Value = on && _steamState == SteamState.NeedsRestart;
        _domainsText.Value = loc.F("domains.value", _domains.Domains.Count, _domains.Source == "built-in" ? loc.T("domains.built-in") : Path.GetFileName(_domains.Source));

        // The switch's live line: what went wrong when something did, else what Steam is doing.
        _bypassLine.Value = _problem is { } problem ? new LocalisedText(Text.Literal(problem)) : LocalisedText.Of(SteamKey(_steamState));

        _stateLine.Value = !on
            ? (controller.LastError is not null ? LocalisedText.Of("state.failed") : LocalisedText.Of("state.off"))
            : engine.DownloadsError is not null
                ? LocalisedText.Of("state.on-no-downloads", counters.Active)
                : LocalisedText.Of("state.on", counters.Active);

        RefreshTestLine();
        _knownOn = on;
        RefreshPageIfOnChanged(on);
    }

    /// <summary>
    /// The test row's live line: nothing before the first run; one line per check while it runs ("Store: Testing…") and after it ("Store:
    /// Reachable · HTTP 200 · 340 ms"), so the four results are read without four rows that say "Not tested" the rest of the time.
    /// </summary>
    private void RefreshTestLine()
    {
        var loc = _host?.Localisation;
        if (loc is null) return;
        lock (_checkGate)
        {
            if (_checkResults.Count == 0 && !_checksRunning)
            {
                _testLine.Value = default;
                return;
            }
            var lines = CheckSpec.All.Select(spec => loc.F("test.line", loc.T(spec.TitleKey),
                _checkResults.TryGetValue(spec.Id, out var result) ? FormatCheck(loc, result) : loc.T("check.running")));
            _testLine.Value = new LocalisedText(Text.Literal(string.Join("\n", lines)));
        }
    }

    private static string SteamKey(SteamState state) => state switch
    {
        SteamState.RunningBypassOff => "steam.bypass-off",
        SteamState.Starting => "steam.starting",
        SteamState.Using => "steam.using",
        SteamState.NeedsRestart => "steam.needs-restart",
        _ => "steam.not-running",
    };

    private static string SteamText(ILocalisation loc, SteamState state) => loc.T(SteamKey(state));

    /// <summary>One line for a check: "Reachable · HTTP 200 · 340 ms" or the reason it failed.</summary>
    internal static string FormatCheck(ILocalisation loc, CheckResult result)
    {
        if (result.Ok) return loc.F("check.ok", result.HttpStatus ?? 0, result.Milliseconds);
        return result.Failure switch
        {
            CheckFailure.Refused => loc.T("check.refused"),
            CheckFailure.TimedOut => loc.T("check.timeout"),
            CheckFailure.DnsFailed => loc.T("check.dns"),
            CheckFailure.Reset => loc.T("check.reset"),
            _ => loc.F("check.other", result.Detail ?? ""),
        };
    }

    /// <summary>
    /// Runs the four checks in parallel and shows each result in the test row's line as it arrives. With the bypass on, the three web checks go through
    /// the proxy; with it off they go direct, so the result says whether the network blocks Steam. Only one run at a time.
    /// </summary>
    internal async Task<CheckRun> RunChecksAsync(CancellationToken ct)
    {
        var host = _host;
        var probe = _probe;
        var controller = _controller;
        if (host is null || probe is null || controller is null) return new CheckRun(CheckSpec.All.Count, CheckSpec.All.Count, Array.Empty<CheckResult>());
        // Only the run that set the flag may clear it. The try block starts before the flag is set, so a failure right after setting it
        // cannot leave it stuck, and a run that was refused (the flag was not its own) never reaches the clearing code with a claim.
        bool owner = false;
        try
        {
            lock (_checkGate)
            {
                if (_checksRunning) return new CheckRun(CheckSpec.All.Count, CheckSpec.All.Count, Array.Empty<CheckResult>());
                _checksRunning = true;
                owner = true;
                _checkResults.Clear();
                RefreshTestLine();
            }
            int? proxyPort = controller.IsOn ? controller.Engine.ProxyPort : null;
            var tasks = CheckSpec.All.Select(async spec =>
            {
                CheckResult result;
                try { result = await probe.RunAsync(spec, proxyPort, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result = new CheckResult(spec.Id, false, CheckFailure.Other, null, 0, ex.Message); }
                lock (_checkGate)
                {
                    _checkResults[spec.Id] = result;
                    RefreshTestLine();
                }
                return result;
            }).ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            int failed = results.Count(r => !r.Ok);
            host.Log.Info($"Connection check: {results.Length - failed} of {results.Length} passed ({(proxyPort is null ? "direct" : "through the bypass")})");
            return new CheckRun(results.Length, failed, results);
        }
        catch (OperationCanceledException) when (owner)
        {
            lock (_checkGate)
            {
                _checkResults.Clear();
                _checksRunning = false;
                RefreshTestLine();
            }
            throw;
        }
        finally
        {
            if (owner)
                lock (_checkGate)
                {
                    _checksRunning = false;
                    RefreshTestLine();
                }
        }
    }

    /// <summary>The time as "HH:mm" for the status verb.</summary>
    private static string Clock(DateTimeOffset time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
