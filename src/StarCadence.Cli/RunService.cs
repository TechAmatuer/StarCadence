// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using StarCadence.Core.Indi;
using StarCadence.Core.Model;
using StarCadence.Core.Planning;
using StarCadence.Core.Run;
using StarCadence.Core.Sources;

/// <summary>How a run is driven: a real INDI server, or a simulation that only writes a log (for trying it out).</summary>
sealed class RunRequest {
    public string Mode { get; set; } = "simulate";   // "indi" or "simulate"
    public string? Host { get; set; }
    public int? Port { get; set; }
    /// <summary>Simulation only: start the clock here (UTC) instead of now.</summary>
    public DateTime? FromUtc { get; set; }
    /// <summary>Simulation only: how many times faster than real time.</summary>
    public double Speed { get; set; } = 1;
    public bool Park { get; set; } = true;
}

/// <summary>A simulated observatory for demonstrations: no hardware, just realistic delays on the (scaled) clock.</summary>
sealed class SimulatedObservatory : IObservatory {
    readonly IClock _clock; readonly Action<string> _log; readonly Overheads _oh;
    public int Frames { get; private set; }
    public SimulatedObservatory(IClock clock, Overheads overheads, Action<string> log) { _clock = clock; _oh = overheads; _log = log; }
    public async Task SlewAsync(double ra, double dec, string name, CancellationToken ct) {
        _log($"  (simulation) slewing to {name} at {StarCadence.Core.Astro.Coords.FormatRa(ra)} {StarCadence.Core.Astro.Coords.FormatDec(dec)}");
        await _clock.Delay(TimeSpan.FromSeconds(_oh.SlewAndCenterS), ct);
    }
    public Task SetFilterAsync(string f, CancellationToken ct) => _clock.Delay(TimeSpan.FromSeconds(_oh.FilterChangeS), ct);
    public async Task ExposeAsync(ExposureRequest r, CancellationToken ct) {
        await _clock.Delay(TimeSpan.FromSeconds(r.Seconds + _oh.ReadoutS), ct);
        Frames++;
    }
    public Task FinishAsync(CancellationToken ct) { _log("  (simulation) parked"); return Task.CompletedTask; }
}

/// <summary>Runs one night at a time, for the command line and the app window, and keeps the log and status.</summary>
sealed class RunService {
    readonly object _lock = new();
    readonly List<string> _log = new();
    CancellationTokenSource? _cts;
    Task? _task;
    NightRunner? _runner;
    IAsyncDisposable? _disposable;
    public RunRequest? Request { get; private set; }
    public string? Error { get; private set; }
    public DateTime? StartedUtc { get; private set; }
    public int Frames { get; private set; }
    Func<int>? _frames;
    public string? LogFile { get; private set; }

    public bool Running => _task is { IsCompleted: false };

    public void Log(string line) {
        lock (_lock) { _log.Add(line); if (_log.Count > 2000) _log.RemoveRange(0, 500); }
        if (LogFile is not null) { try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch (IOException) { } }
    }

    public object Status() {
        lock (_lock) {
            var st = _runner?.Status;
            return new {
                running = Running, mode = Request?.Mode, host = Request?.Host, port = Request?.Port, speed = Request?.Speed, startedUtc = StartedUtc, error = Error,
                phase = st?.Phase.ToString() ?? "Idle", message = st?.Message ?? "", current = st?.Current, next = st?.Next,
                exposures = st?.ExposuresTaken ?? 0, blocksDone = st?.BlocksDone ?? 0, blocksFailed = st?.BlocksFailed ?? 0,
                nightEndUtc = st?.NightEndUtc, clockUtc = _clockNow?.Invoke(), frames = _frames?.Invoke() ?? Frames,
                log = _log.TakeLast(60).ToList(), logFile = LogFile,
            };
        }
    }
    Func<DateTime>? _clockNow;

    /// <summary>Start a run. Throws when one is already running or the request cannot be set up.</summary>
    public async Task StartAsync(Config cfg, string dataDir, RunRequest req, bool blocking, Action<string>? echo = null) {
        if (Running) throw new InvalidOperationException("A run is already in progress; stop it first.");
        lock (_lock) { _log.Clear(); Error = null; Request = req; StartedUtc = DateTime.UtcNow; Frames = 0; }
        var runsDir = Path.Combine(dataDir, "runs");
        Directory.CreateDirectory(runsDir);
        LogFile = Path.Combine(runsDir, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        void L(string s) { Log(s); echo?.Invoke(s); }

        IClock clock = req.Mode == "simulate" && (req.FromUtc is not null || req.Speed != 1)
            ? new ScaledClock(req.FromUtc ?? DateTime.UtcNow, req.Speed) : new SystemClock();
        _clockNow = () => clock.UtcNow;
        IObservatory obs;
        if (req.Mode == "indi") {
            var s = cfg.Indi;
            if (!string.IsNullOrWhiteSpace(req.Host)) s.Host = req.Host;
            if (req.Port is int p) s.Port = p;
            s.ParkAtEnd = req.Park;
            var imageRoot = Path.IsPathRooted(s.ImageDir) ? s.ImageDir : Path.Combine(dataDir, s.ImageDir);
            var indi = new IndiObservatory(s, cfg.Filters, imageRoot, L);
            await indi.ConnectAsync(CancellationToken.None);
            L($"Images will be saved under {imageRoot}");
            obs = indi; _disposable = indi; _frames = () => indi.FramesSaved;
        } else {
            var sim = new SimulatedObservatory(clock, cfg.Overheads, L);
            obs = sim; _disposable = null; _frames = () => sim.Frames;
            L($"Simulation: no hardware. Clock {(clock is ScaledClock sc ? $"{sc.Speed:0.#}x from {sc.UtcNow:yyyy-MM-dd HH:mm} UTC" : "real time")}.");
        }
        var loader = new SourceLoader(cfg, dataDir, null);
        var histPath = PlanRunner.Resolve(dataDir, cfg.HistoryFile);
        _runner = new NightRunner(cfg, obs, clock, now => loader.LoadAll(now).SelectMany(r => r.Targets), histPath, L);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _task = Task.Run(async () => {
            try { await _runner.RunAsync(token); }
            catch (Exception e) when (e is not OperationCanceledException) { lock (_lock) Error = e.Message; L("Run stopped with an error: " + e.Message); }
            finally {
                if (_disposable is not null) { try { await _disposable.DisposeAsync(); } catch { } }
                L($"Run ended: {_runner.Status.BlocksDone} targets done, {_runner.Status.ExposuresTaken} exposures.");
            }
        });
        if (blocking) await _task;
    }

    public async Task StopAsync() {
        var t = _task; var c = _cts;
        if (t is null || c is null) return;
        Log("Stop requested.");
        c.Cancel();
        try { await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(30))); } catch { }
    }
}
