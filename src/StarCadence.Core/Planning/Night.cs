// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using StarCadence.Core.Astro;
using StarCadence.Core.Model;

namespace StarCadence.Core.Planning;

/// <summary>
/// One observing night sampled on a time grid: dark limits, the Moon, and per-target altitude and visibility.
/// Same idea as Target Scheduler's visibility sampling, computed once per night with no N.I.N.A. dependency.
/// </summary>
public sealed class Night {
    public Site Site { get; }
    public DateTime StartUtc { get; }
    public DateTime EndUtc { get; }
    public double StepMinutes { get; }
    public DateTime[] Times { get; }
    readonly double[] _lst;
    readonly double[] _moonRa, _moonDec, _moonAlt;
    public double MoonIllumination { get; }
    readonly Dictionary<string, Track> _tracks = new();

    Night(Site site, DateTime start, DateTime end, double stepMinutes) {
        Site = site; StartUtc = start; EndUtc = end; StepMinutes = stepMinutes;
        var n = Math.Max(2, (int)Math.Ceiling((end - start).TotalMinutes / stepMinutes) + 1);
        Times = new DateTime[n];
        _lst = new double[n]; _moonRa = new double[n]; _moonDec = new double[n]; _moonAlt = new double[n];
        for (var i = 0; i < n; i++) {
            var t = i == n - 1 ? end : start.AddMinutes(i * stepMinutes);
            Times[i] = t;
            var jd = AstroTime.JulianDate(t);
            _lst[i] = AstroTime.LmstDegrees(jd, site.Longitude);
            var (mra, mdec, _) = Moon.Position(jd);
            _moonRa[i] = mra; _moonDec[i] = mdec;
            _moonAlt[i] = Moon.Altitude(jd, site.Latitude, site.Longitude);
        }
        MoonIllumination = Moon.Illumination(AstroTime.JulianDate(start + (end - start) / 2));
    }

    /// <summary>
    /// The dark period that starts on the evening of <paramref name="localDate"/> at the site (the date of the evening,
    /// as observers name nights). Returns null when the Sun never gets low enough.
    /// </summary>
    public static Night? ForDate(Site site, DateOnly localDate, double stepMinutes = 2) {
        var (start, end) = DarkLimits(site, localDate);
        if (start is null || end is null || end <= start) return null;
        return new Night(site, start.Value, end.Value, stepMinutes);
    }

    /// <summary>
    /// The rest of the current night from a given time (re-planning mid-night). In daylight this is the whole of the
    /// coming night. Null only when there is no dark time at all (polar summer).
    /// </summary>
    public static Night? From(Site site, DateTime utc, double stepMinutes = 2) {
        utc = AstroTime.AsUtc(utc);
        var date = CurrentOrNext(site, utc);
        var (start, end) = DarkLimits(site, date);
        if (start is null || end is null || utc >= end) return null;
        return new Night(site, utc > start ? utc : start.Value, end.Value, stepMinutes);
    }

    /// <summary>The night still to come at this time: tonight, or the one in progress (not last night after dawn).</summary>
    public static DateOnly CurrentOrNext(Site site, DateTime utc) {
        utc = AstroTime.AsUtc(utc);
        var date = NightDateOf(site, utc);
        var (_, end) = DarkLimits(site, date);
        return end is DateTime e && utc >= e ? date.AddDays(1) : date;
    }

    /// <summary>The evening date a UTC instant belongs to (local mean noon to local mean noon).</summary>
    public static DateOnly NightDateOf(Site site, DateTime utc) {
        var localMean = utc.AddHours(site.Longitude / 15.0);
        return DateOnly.FromDateTime(localMean.AddHours(-12));
    }

    static DateTime LocalMeanNoonUtc(Site site, DateOnly date) =>
        new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Utc).AddHours(-site.Longitude / 15.0);

    public static (DateTime? start, DateTime? end) DarkLimits(Site site, DateOnly date) {
        var noon = LocalMeanNoonUtc(site, date);
        double Alt(DateTime t) => Sun.Altitude(AstroTime.JulianDate(t), site.Latitude, site.Longitude) - site.SunAltitude;
        DateTime? start = null, end = null;
        var prev = noon; var prevAlt = Alt(prev);
        if (prevAlt < 0) start = noon;   // polar night: dark at noon already
        for (var m = 5; m <= 24 * 60; m += 5) {
            var t = noon.AddMinutes(m);
            var a = Alt(t);
            if (start is null && prevAlt >= 0 && a < 0) start = Refine(prev, t, Alt);
            else if (start is not null && end is null && prevAlt < 0 && a >= 0) { end = Refine(prev, t, Alt); break; }
            prev = t; prevAlt = a;
        }
        if (start is not null && end is null) end = noon.AddDays(1);
        return (start, end);
    }

    static DateTime Refine(DateTime a, DateTime b, Func<DateTime, double> f) {
        var fa = f(a);
        for (var i = 0; i < 30 && (b - a).TotalSeconds > 1; i++) {
            var mid = a + (b - a) / 2;
            var fm = f(mid);
            if (Math.Sign(fm) == Math.Sign(fa)) { a = mid; fa = fm; } else b = mid;
        }
        var r = a + (b - a) / 2;
        return new DateTime(r.Ticks - r.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    public int IndexAtOrBefore(DateTime t) {
        if (t <= StartUtc) return 0;
        var i = (int)Math.Floor((t - StartUtc).TotalMinutes / StepMinutes);
        return Math.Clamp(i, 0, Times.Length - 1);
    }

    public int IndexAtOrAfter(DateTime t) {
        if (t <= StartUtc) return 0;
        var i = (int)Math.Ceiling((t - StartUtc).TotalMinutes / StepMinutes - 1e-9);
        return Math.Clamp(i, 0, Times.Length - 1);
    }

    public Track TrackOf(Target target) {
        var key = target.Key + "|" + target.RaDeg.ToString("R") + "|" + target.DecDeg.ToString("R");
        if (_tracks.TryGetValue(key, out var tr)) return tr;
        tr = new Track(this, target);
        _tracks[key] = tr;
        return tr;
    }

    public sealed class Track {
        public double[] Alt { get; }
        public double[] Az { get; }
        public double[] MoonSep { get; }
        public bool[] Ok { get; }
        public string?[] Why { get; }
        readonly Night _n;

        internal Track(Night n, Target target) {
            _n = n;
            var count = n.Times.Length;
            Alt = new double[count]; Az = new double[count]; MoonSep = new double[count]; Ok = new bool[count]; Why = new string?[count];
            var mid = AstroTime.JulianDate(n.StartUtc + (n.EndUtc - n.StartUtc) / 2);
            var (ra, dec) = Coords.PrecessFromJ2000(target.RaDeg, target.DecDeg, mid);
            var minAlt = Math.Max(n.Site.MinAltitude, Coords.AltitudeForAirmass(n.Site.MaxAirmass));
            for (var i = 0; i < count; i++) {
                var (alt, az) = Coords.Horizontal(ra, dec, n.Site.Latitude, n._lst[i]);
                Alt[i] = alt; Az[i] = az;
                MoonSep[i] = AstroMath.Separation(ra, dec, n._moonRa[i], n._moonDec[i]);
                string? why = null;
                if (alt < minAlt) why = "too low";
                else if (alt < n.Site.HorizonAt(az)) why = "behind the local horizon";
                else if (n._moonAlt[i] > 0 && MoonSep[i] < n.Site.MoonSeparation) why = "too close to the Moon";
                Why[i] = why;
                Ok[i] = why is null;
            }
        }

        /// <summary>Observable at every grid point from start to end (inclusive).</summary>
        public bool Throughout(DateTime start, DateTime end) {
            if (start < _n.StartUtc || end > _n.EndUtc) return false;
            for (int i = _n.IndexAtOrBefore(start), j = _n.IndexAtOrAfter(end); i <= j; i++)
                if (!Ok[i]) return false;
            return true;
        }

        /// <summary>Fraction of grid points between start and end where the target is observable.</summary>
        public double Coverage(DateTime start, DateTime end) {
            int i0 = _n.IndexAtOrBefore(start < _n.StartUtc ? _n.StartUtc : start), j = _n.IndexAtOrAfter(end > _n.EndUtc ? _n.EndUtc : end);
            if (end <= _n.StartUtc || start >= _n.EndUtc) return 0;
            var inside = (Math.Min(end.Ticks, _n.EndUtc.Ticks) - Math.Max(start.Ticks, _n.StartUtc.Ticks)) / (double)(end - start).Ticks;
            int ok = 0, all = 0;
            for (var i = i0; i <= j; i++) { all++; if (Ok[i]) ok++; }
            return all == 0 ? 0 : inside * ok / all;
        }

        /// <summary>When the target stops being observable after time t (end of its current visible stretch).</summary>
        public DateTime VisibleUntil(DateTime t) {
            var i = _n.IndexAtOrBefore(t);
            if (!Ok[i]) return t;
            while (i + 1 < Ok.Length && Ok[i + 1]) i++;
            return _n.Times[i];
        }

        /// <summary>First grid time at or after t when the target is observable, or null.</summary>
        public DateTime? NextVisible(DateTime t) {
            for (var i = _n.IndexAtOrAfter(t); i < Ok.Length; i++) if (Ok[i]) return _n.Times[i];
            return null;
        }

        public double AltAt(DateTime t) => Alt[_n.IndexAtOrBefore(t)];
        public bool AnyVisible => Ok.Any(x => x);

        /// <summary>The most common reason the target cannot be observed tonight.</summary>
        public string MainProblem() =>
            Why.Where(w => w is not null).GroupBy(w => w).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
    }
}
