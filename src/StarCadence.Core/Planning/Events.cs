// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// Event windows for periodic stars and transits. The epoch/period/phase window follows VariableStar.NextEvent in the
// ExoPlanets N.I.N.A. plug-in (github.com/NickHardy/ExoPlanets, MPL-2.0): event = epoch + period x (n + phase),
// window = event +/- (half-width + O-C range). Here every event in the night is returned, not only the next one,
// and the epoch (HJD/BJD) is converted to UTC with the light-time correction.

using StarCadence.Core.Astro;
using StarCadence.Core.Model;

namespace StarCadence.Core.Planning;

public static class Events {
    /// <summary>Fixed windows of a periodic target (transit or event) that overlap [fromUtc, toUtc].</summary>
    public static List<TimeWindow> WindowsBetween(Target t, DateTime fromUtc, DateTime toUtc, PlannerSettings p) {
        var list = new List<TimeWindow>();
        if (t.EpochJd is not double epoch || t.PeriodDays is not double period || period <= 0) return list;
        var halfMin = t.Kind == TargetKind.Transit
            ? (t.DurationHours ?? 2) * 30.0 + p.TransitBaselineMinutes
            : t.EventHalfWidthMin ?? p.EventHalfWidthMinutes;
        halfMin += Math.Max(0, t.OcRangeMin);
        var half = halfMin / 1440.0;
        var shifted = epoch + period * t.EventPhase;
        // Epochs are heliocentric/barycentric; work in that scale, then convert each event to UTC.
        var jdFrom = AstroTime.JulianDate(fromUtc) - half - 0.02;
        var jdTo = AstroTime.JulianDate(toUtc) + half + 0.02;
        var n0 = Math.Ceiling((jdFrom - shifted) / period);
        var n1 = Math.Floor((jdTo - shifted) / period);
        if (n1 - n0 > 2000) return list; // a very short period: treat as a plain target instead
        for (var n = n0; n <= n1; n++) {
            var evt = shifted + n * period;
            var utcJd = epoch > 2400000 ? Sun.BjdTdbToJdUtc(evt, t.RaDeg, t.DecDeg) : evt;
            var start = AstroTime.FromJulianDate(utcJd - half);
            var end = AstroTime.FromJulianDate(utcJd + half);
            if (end <= fromUtc || start >= toUtc) continue;
            var mid = AstroTime.FromJulianDate(utcJd);
            var label = t.Kind == TargetKind.Transit ? $"transit mid {mid:HH:mm} UTC" : $"event (phase {t.EventPhase:0.##}) at {mid:HH:mm} UTC";
            list.Add(new TimeWindow(Trim(start), Trim(end), true, label));
        }
        return list;
    }

    static DateTime Trim(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}
