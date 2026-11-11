// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using StarCadence.Core.Model;

namespace StarCadence.Core.Planning;

/// <param name="SaturatesBrighterThan">At this exposure the star saturates if it gets brighter than this magnitude
/// (set only when its catalogue bright limit is brighter than that).</param>
public sealed record ExposureStep(string Filter, string Aavso, double Seconds, int Count, double? PlanMag, string? Note = null, double? SaturatesBrighterThan = null) {
    public double TotalSeconds(double readoutS) => Count * (Seconds + readoutS);
}

/// <summary>
/// Exposure time per star brightness and filter. Each filter has a reference exposure for a reference magnitude;
/// the exposure scales by 10^(0.4 dm) so every star reaches about the same signal, capped so the star's brightest
/// catalogue magnitude does not saturate, kept within the camera limits, and stacked when a faint star needs more
/// than the longest exposure. (Replaces test-frame exposure calculation in the ExoPlanets plug-in, so a whole
/// night can be planned before the first frame.)
/// </summary>
public static class ExposureCalculator {
    static readonly double[] Nice = { 0.5, 1, 1.5, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 75, 90, 120, 150, 180, 240, 300, 360, 450, 600, 900, 1200 };

    /// <summary>Filters the target asks for that this observatory has (exact AAVSO code, else nearest); all filters if none asked.</summary>
    public static (List<FilterSpec> filters, string? note) FiltersFor(Config cfg, IReadOnlyCollection<string> want) {
        if (want.Count == 0) return (cfg.Filters.ToList(), null);
        var exact = cfg.Filters.Where(f => want.Contains(f.Aavso, StringComparer.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return (exact, null);
        var nearCodes = want.Select(w => Near.TryGetValue(w.ToUpperInvariant(), out var n) ? n : w).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var near = cfg.Filters.Where(f => nearCodes.Contains(f.Aavso)).ToList();
        if (near.Count > 0) return (near, $"asks for {string.Join(", ", want)}; nearest here: {string.Join(", ", near.Select(f => f.Aavso))}");
        return (cfg.Filters.ToList(), $"asks for {string.Join(", ", want)}, which this observatory does not have; using all filters");
    }

    /// <summary>Close-enough substitutes when the exact band is missing (Sloan/clear/tricolour to Johnson-Cousins).</summary>
    public static readonly Dictionary<string, string> Near = new() {
        ["SR"] = "R", ["SI"] = "I", ["SG"] = "V", ["SZ"] = "I", ["CV"] = "V", ["CR"] = "R", ["TG"] = "V", ["TB"] = "B", ["TR"] = "R", ["RC"] = "R", ["IC"] = "I",
    };

    /// <param name="capForBrightLimit">When true, exposures are shortened so the star's catalogue bright limit cannot
    /// saturate (safe for stars that may flare tonight, at the cost of signal in their faint state). When false (default),
    /// exposures suit the planned magnitude and a note warns if the bright limit would saturate.</param>
    public static ExposureStep For(FilterSpec f, Target t, bool capForBrightLimit = false) {
        var mag = t.PlanMag(f.Aavso, f.ColourOffset);
        if (mag is null) return new ExposureStep(f.Name, f.Aavso, Round(f.RefExposure), f.Count, null, "no magnitude known: reference exposure used");
        var raw = f.RefExposure * Math.Pow(10, 0.4 * (mag.Value - f.RefMag));
        var notes = new List<string>();
        var capped = false;
        double? satWarn = null;
        double? bright = t.MagBright is double mb ? mb + (t.BandMags.ContainsKey(f.Aavso) ? 0 : f.ColourOffset) : null;
        if (bright is double br && br < mag) {
            // Exposure at which a star of magnitude br just reaches saturation (headroom above the reference level).
            var satAt = f.RefExposure * Math.Pow(10, 0.4 * (br + f.SaturationHeadroom - f.RefMag));
            if (capForBrightLimit && satAt < raw) { raw = satAt; capped = true; notes.Add($"shortened so it will not saturate if it brightens to {br:0.0}"); }
            else if (satAt < Math.Clamp(raw, f.MinExposure, f.MaxExposure)) {
                var e = Math.Clamp(raw, f.MinExposure, f.MaxExposure);
                var mSat = f.RefMag - f.SaturationHeadroom + 2.5 * Math.Log10(e / f.RefExposure);   // saturates when brighter than this
                satWarn = Math.Round(mSat, 1);
            }
        }
        var count = f.Count;
        if (raw < f.MinExposure) {
            var satAtMin = f.RefExposure * Math.Pow(10, 0.4 * (mag.Value + f.SaturationHeadroom - f.RefMag));
            if (satAtMin < f.MinExposure) notes.Add("may saturate even at the shortest exposure: defocus, or lower minExposure");
            raw = f.MinExposure;
        } else if (raw > f.MaxExposure) {
            var needed = (int)Math.Ceiling(raw / f.MaxExposure);
            count = Math.Min(f.MaxCount, Math.Max(f.Count, needed * f.Count));
            notes.Add(count < needed * f.Count ? $"faint for this filter: {count} x {f.MaxExposure:0} s gives less signal than wanted" : $"faint: stacked {count} x {f.MaxExposure:0} s");
            raw = f.MaxExposure;
        }
        var secs = Math.Clamp(Round(raw, down: capped), f.MinExposure, f.MaxExposure);
        return new ExposureStep(f.Name, f.Aavso, secs, count, Math.Round(mag.Value, 2), notes.Count == 0 ? null : string.Join("; ", notes), satWarn);
    }

    /// <summary>Round to familiar values (as in Space Lab): the nearest one, or the next one down when rounding up could saturate.</summary>
    public static double Round(double raw, bool down = false) {
        if (raw <= Nice[0]) return Nice[0];
        if (raw > Nice[^1]) return Math.Round(raw);
        if (down) return Nice.Where(n => n <= raw).DefaultIfEmpty(Nice[0]).Max();
        var best = Nice[0];
        foreach (var n in Nice) if (Math.Abs(Math.Log(n / raw)) < Math.Abs(Math.Log(best / raw))) best = n;
        return best;
    }

    /// <summary>One visit: every wanted filter once (with the counts above).</summary>
    public static List<ExposureStep> ForVisit(Config cfg, Target t, out string? note) {
        var (filters, n) = FiltersFor(cfg, t.WantFilters);
        note = n;
        return filters.Select(f => For(f, t, cfg.Planner.CapForBrightLimit)).ToList();
    }

    public static double VisitSeconds(Config cfg, IReadOnlyList<ExposureStep> steps, bool includeSlew = true) =>
        (includeSlew ? cfg.Overheads.SlewAndCenterS : 0) + steps.Count * cfg.Overheads.FilterChangeS + steps.Sum(s => s.TotalSeconds(cfg.Overheads.ReadoutS));

    /// <summary>Repeat the filter cycle to fill a span (time series and transits): counts scaled to the time available.</summary>
    public static List<ExposureStep> Fill(Config cfg, IReadOnlyList<ExposureStep> cycle, double seconds) {
        if (cycle.Count == 0) return new();
        var one = cycle.Count * (cycle.Count > 1 ? cfg.Overheads.FilterChangeS : 0) + cycle.Sum(s => s.Seconds + cfg.Overheads.ReadoutS);
        var reps = Math.Max(1, (int)Math.Floor(seconds / one));
        return cycle.Select(s => s with { Count = reps }).ToList();
    }
}
