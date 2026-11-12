// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using StarCadence.Core.Model;
using StarCadence.Core.Planning;

namespace StarCadence.Core.Run;

public sealed class RunnerOptions {
    /// <summary>How long to wait when nothing is observable before planning again.</summary>
    public TimeSpan IdleWait { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>How long a target that failed (slew error, camera error) is left alone before it is tried again.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>Targets are reloaded from their sources this often (new alerts appear during the night).</summary>
    public TimeSpan ReloadTargetsEvery { get; set; } = TimeSpan.FromHours(1);
    /// <summary>Stop the night this long before the dark time ends (0 = run to the end).</summary>
    public TimeSpan EndMargin { get; set; } = TimeSpan.Zero;
    /// <summary>When true, wait for the next night to begin instead of returning in daylight.</summary>
    public bool WaitForDark { get; set; } = true;
}

public enum RunnerPhase { Idle, WaitingForDark, Planning, Slewing, Exposing, WaitingForWindow, WaitingSafe, Finished, Stopped, Failed }

public sealed class RunnerStatus {
    public RunnerPhase Phase { get; set; } = RunnerPhase.Idle;
    public string Message { get; set; } = "";
    public PlanBlock? Current { get; set; }
    public PlanBlock? Next { get; set; }
    public int ExposuresTaken { get; set; }
    public int BlocksDone { get; set; }
    public int BlocksFailed { get; set; }
    public DateTime? NightEndUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public RunnerStatus Copy() => (RunnerStatus)MemberwiseClone();
}

/// <summary>
/// Runs a night: plan from now, carry out the first block (slew, filters, exposures), record the visit, plan again.
/// Fixed windows (transits, minima) start on time; everything else fills the gaps. Re-planning after every block
/// means a late start, a cloud gap or a new alert changes the rest of the night, not just the next target.
/// Target Scheduler works the same way inside N.I.N.A.; this one is independent of the observatory software.
/// </summary>
public sealed class NightRunner {
    readonly Config _cfg;
    readonly IObservatory _obs;
    readonly IClock _clock;
    readonly Func<DateTime, IEnumerable<Target>> _loadTargets;
    readonly string _historyPath;
    readonly Action<string> _log;
    readonly RunnerOptions _opt;
    readonly Planner _planner;
    readonly object _lock = new();
    RunnerStatus _status = new();

    public event Action<RunnerStatus>? StatusChanged;
    public event Action<PlanBlock>? BlockStarted;
    public event Action<PlanBlock, bool>? BlockFinished;

    public NightRunner(Config cfg, IObservatory observatory, IClock clock, Func<DateTime, IEnumerable<Target>> loadTargets,
                       string historyPath, Action<string>? log = null, RunnerOptions? options = null) {
        _cfg = cfg; _obs = observatory; _clock = clock; _loadTargets = loadTargets; _historyPath = historyPath;
        _log = log ?? (_ => { }); _opt = options ?? new RunnerOptions();
        _planner = new Planner(cfg);
    }

    readonly History _runVisits = new();   // visits made by this run, kept even if the history file cannot be written

    public RunnerStatus Status { get { lock (_lock) return _status.Copy(); } }

    void Set(RunnerPhase phase, string message, PlanBlock? current = null, PlanBlock? next = null) {
        RunnerStatus copy;
        lock (_lock) {
            _status.Phase = phase; _status.Message = message; _status.Current = current; _status.Next = next; _status.UpdatedUtc = _clock.UtcNow;
            copy = _status.Copy();
        }
        _log($"{_clock.UtcNow:HH:mm:ss} {message}");
        StatusChanged?.Invoke(copy);
    }

    /// <summary>Runs until the night ends or the token is cancelled. Returns the number of blocks completed.</summary>
    public async Task<int> RunAsync(CancellationToken ct) {
        var failedUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        List<Target> targets = new();
        DateTime targetsLoaded = DateTime.MinValue;
        Target? previous = null;
        DateTime? thisNightEnd = null;   // the night being run: when it is over, stop (never roll into the next one)
        try {
            while (!ct.IsCancellationRequested) {
                var now = _clock.UtcNow;
                var night = Night.From(_cfg.Site, now, _cfg.Planner.StepMinutes);
                if (night is null) { Set(RunnerPhase.Finished, "No dark time at this site now; nothing to do."); break; }
                if (thisNightEnd is DateTime ended && night.EndUtc > ended.AddHours(1)) { Set(RunnerPhase.Finished, "The night is over."); break; }
                thisNightEnd = night.EndUtc;
                lock (_lock) _status.NightEndUtc = night.EndUtc;
                if (now < night.StartUtc) {
                    if (!_opt.WaitForDark) { Set(RunnerPhase.Finished, $"It is daylight; the night starts at {night.StartUtc:HH:mm} UTC."); break; }
                    Set(RunnerPhase.WaitingForDark, $"Waiting for dark ({night.StartUtc:HH:mm} UTC).");
                    await _clock.Delay(night.StartUtc - now, ct);
                    continue;
                }
                var stopAt = night.EndUtc - _opt.EndMargin;
                if (now >= stopAt) { Set(RunnerPhase.Finished, "The night is over."); break; }

                if (now - targetsLoaded > _opt.ReloadTargetsEvery) {
                    try { targets = _loadTargets(now).ToList(); targetsLoaded = now; _log($"{now:HH:mm:ss} {targets.Count} targets loaded"); }
                    catch (Exception e) when (e is not OperationCanceledException) { _log($"{now:HH:mm:ss} could not load targets: {e.Message}"); if (targets.Count == 0) throw; }
                }

                if (!await _obs.IsSafeAsync(ct)) {
                    Set(RunnerPhase.WaitingSafe, "Unsafe (weather or roof); waiting.");
                    await _clock.Delay(TimeSpan.FromMinutes(1), ct);
                    continue;
                }

                Set(RunnerPhase.Planning, "Planning the rest of the night...");
                var history = LoadHistory(now);
                var usable = targets.Where(t => !(failedUntil.TryGetValue(t.Key, out var until) && until > now)).ToList();
                var plan = _planner.Build(usable, history, night);
                var block = plan.Blocks.FirstOrDefault(b => b.EndUtc > now);
                if (block is null) {
                    var wait = stopAt - now < _opt.IdleWait ? stopAt - now : _opt.IdleWait;
                    Set(RunnerPhase.Idle, $"Nothing to observe right now; checking again in {wait.TotalMinutes:0} min.");
                    await _clock.Delay(wait, ct);
                    continue;
                }
                var next = plan.Blocks.Skip(1).FirstOrDefault();
                if (block.StartUtc > now.AddSeconds(2)) {
                    Set(RunnerPhase.WaitingForWindow, $"Next: {block.Target} at {block.StartUtc:HH:mm} UTC ({block.Window ?? block.Kind.ToString()}); waiting.", block, next);
                    var wait = block.StartUtc - now;
                    if (wait > _opt.IdleWait) wait = _opt.IdleWait;   // re-plan on the way, in case something changes
                    await _clock.Delay(wait, ct);
                    continue;
                }

                // Run the block.
                BlockStarted?.Invoke(block);
                var ok = false;
                try {
                    ok = await RunBlock(block, next, previous, history, ct);
                } catch (OperationCanceledException) { throw; }
                catch (Exception e) {
                    _log($"{_clock.UtcNow:HH:mm:ss} {block.Target}: {e.GetType().Name}: {e.Message}");
                    failedUntil[Target.NormaliseName(block.Target)] = _clock.UtcNow + _opt.RetryAfter;
                }
                lock (_lock) { if (ok) _status.BlocksDone++; else _status.BlocksFailed++; }
                BlockFinished?.Invoke(block, ok);
                previous = ok ? usable.FirstOrDefault(t => t.Key == Target.NormaliseName(block.Target)) : previous;
            }
            if (ct.IsCancellationRequested) Set(RunnerPhase.Stopped, "Stopped.");
        } catch (OperationCanceledException) {
            Set(RunnerPhase.Stopped, "Stopped.");
        } catch (Exception e) {
            Set(RunnerPhase.Failed, "Stopped with an error: " + e.Message);
            throw;
        }
        try { await _obs.FinishAsync(CancellationToken.None); } catch (Exception e) { _log("finish: " + e.Message); }
        return Status.BlocksDone;
    }

    /// <summary>The history file merged with this run's visits, so a file that cannot be read or saved for a moment
    /// (another program holding it, a sync client) never makes the cadence forget a target observed tonight.</summary>
    History LoadHistory(DateTime now) {
        History h;
        try { h = History.Load(_historyPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            _log($"{now:HH:mm:ss} could not read {Path.GetFileName(_historyPath)} ({e.Message}); using tonight's visits.");
            h = new History();
        }
        foreach (var (name, times) in _runVisits.Visits) foreach (var t in times) h.Record(name, t);
        return h;
    }

    async Task<bool> RunBlock(PlanBlock b, PlanBlock? next, Target? previous, History history, CancellationToken ct) {
        var timed = b.Fixed || b.Kind == TargetKind.Series;
        Set(RunnerPhase.Slewing, $"{b.Target}: slewing ({b.Kind}{(b.Window is null ? "" : ", " + b.Window)})", b, next);
        await _obs.SlewAsync(b.RaDeg, b.DecDeg, b.Target, ct);

        // Record the visit as soon as exposures begin, so a crash mid-block still counts for the cadence.
        var started = _clock.UtcNow;
        history.Record(b.Target, started);
        _runVisits.Record(b.Target, started);
        try { history.Save(_historyPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            _log($"{started:HH:mm:ss} could not save {Path.GetFileName(_historyPath)} ({e.Message}); the visit is kept and saved with the next one.");
        }

        var reps = b.Steps.Count == 0 ? 0 : b.Steps.Max(s => s.Count);
        var taken = 0;
        var total = b.Steps.Sum(s => s.Count);
        string? currentFilter = null;
        for (var rep = 1; rep <= reps; rep++) {
            foreach (var step in b.Steps) {
                if (rep > step.Count) continue;
                ct.ThrowIfCancellationRequested();
                var now = _clock.UtcNow;
                if (timed && now >= b.EndUtc) { Set(RunnerPhase.Exposing, $"{b.Target}: window over after {taken} exposures", b, next); return true; }
                if (taken > 0 && taken % 5 == 0 && !await _obs.IsSafeAsync(ct)) { Set(RunnerPhase.WaitingSafe, $"{b.Target}: unsafe, stopping after {taken} exposures", b, next); return taken > 0; }
                if (!string.Equals(currentFilter, step.Filter, StringComparison.OrdinalIgnoreCase)) {
                    await _obs.SetFilterAsync(step.Filter, ct);
                    currentFilter = step.Filter;
                }
                taken++;
                Set(RunnerPhase.Exposing, $"{b.Target}: {step.Filter} {step.Seconds:0.#} s ({taken}/{total})", b, next);
                await _obs.ExposeAsync(new ExposureRequest(b.Target, step.Filter, step.Aavso, step.Seconds, taken, total, b.RaDeg, b.DecDeg, b.Kind, b.Window, b.EndUtc), ct);
                lock (_lock) _status.ExposuresTaken++;
            }
        }
        Set(RunnerPhase.Exposing, $"{b.Target}: done ({taken} exposures)", b, next);
        return true;
    }
}
