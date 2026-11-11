// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using StarCadence.Core.Model;

namespace StarCadence.Core.Run;

/// <summary>One exposure the runner asks the observatory to take.</summary>
public sealed record ExposureRequest(
    string Target, string Filter, string Aavso, double Seconds, int Index, int Count,
    double RaDeg, double DecDeg, TargetKind Kind, string? Window, DateTime BlockEndUtc);

/// <summary>
/// What the night runner needs from an observatory. N.I.N.A. provides it through its sequencer,
/// INDI setups through the built-in INDI client, and tests through a fake.
/// </summary>
public interface IObservatory {
    /// <summary>Point the telescope at J2000 coordinates (and centre on them when the backend can plate-solve).</summary>
    Task SlewAsync(double raDeg, double decDeg, string targetName, CancellationToken ct);
    Task SetFilterAsync(string filterName, CancellationToken ct);
    /// <summary>Take and save one frame.</summary>
    Task ExposeAsync(ExposureRequest request, CancellationToken ct);
    /// <summary>False when a weather or roof monitor says to stop; backends without one answer true.</summary>
    Task<bool> IsSafeAsync(CancellationToken ct) => Task.FromResult(true);
    /// <summary>Called once when the night is over (park the mount, close up); optional.</summary>
    Task FinishAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Time source, so the runner can be tested (and demonstrated) at any speed.</summary>
public interface IClock {
    DateTime UtcNow { get; }
    Task Delay(TimeSpan span, CancellationToken ct);
}

public sealed class SystemClock : IClock {
    public DateTime UtcNow => DateTime.UtcNow;
    public Task Delay(TimeSpan span, CancellationToken ct) => span <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(span, ct);
}

/// <summary>A clock that runs <see cref="Speed"/> times faster than real time from a chosen start (demonstrations, simulator tests).</summary>
public sealed class ScaledClock : IClock {
    readonly DateTime _start;
    readonly DateTime _realStart = DateTime.UtcNow;
    public double Speed { get; }
    public ScaledClock(DateTime startUtc, double speed) { _start = Astro.AstroTime.AsUtc(startUtc); Speed = Math.Max(1e-6, speed); }
    public DateTime UtcNow => _start + (DateTime.UtcNow - _realStart) * Speed;
    public Task Delay(TimeSpan span, CancellationToken ct) => span <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(span / Speed, ct);
}
