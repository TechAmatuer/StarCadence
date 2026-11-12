// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using StarCadence.Core.Astro;
using StarCadence.Core.Export;
using StarCadence.Core.Model;
using StarCadence.Core.Planning;
using StarCadence.Core.Sources;

namespace StarCadence.Tests;

[AttributeUsage(AttributeTargets.Method)] sealed class TestAttribute : Attribute { }

sealed class Fail(string m) : Exception(m);

static class A {
    public static void True(bool c, string what) { if (!c) throw new Fail(what); }
    public static void Near(double actual, double expected, double tol, string what) {
        if (double.IsNaN(actual) || Math.Abs(actual - expected) > tol) throw new Fail($"{what}: got {actual.ToString("R", CultureInfo.InvariantCulture)}, expected {expected.ToString("R", CultureInfo.InvariantCulture)} +/- {tol}");
    }
    public static void Eq<T>(T actual, T expected, string what) { if (!Equals(actual, expected)) throw new Fail($"{what}: got {actual}, expected {expected}"); }
}

static class Program {
    static int Main(string[] args) {
        // Run everything under another locale (decimal comma, Thai calendar, Turkish i...) the way N.I.N.A. would on that PC.
        if (Environment.GetEnvironmentVariable("STARCADENCE_TEST_CULTURE") is { Length: > 0 } culture) {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = new CultureInfo(culture);
            Console.WriteLine($"Locale: {CultureInfo.CurrentCulture.Name} ({CultureInfo.CurrentCulture.Calendar.GetType().Name}, decimal '{CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}')");
        }
        var filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null && (filter is null || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || m.DeclaringType!.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.MetadataToken).ToList();
        int pass = 0, fail = 0;
        var sw = Stopwatch.StartNew();
        foreach (var m in tests) {
            var t0 = sw.Elapsed;
            try { m.Invoke(null, null); pass++; Console.WriteLine($"ok    {m.DeclaringType!.Name}.{m.Name} ({(sw.Elapsed - t0).TotalMilliseconds:0} ms)"); }
            catch (TargetInvocationException e) { fail++; Console.WriteLine($"FAIL  {m.DeclaringType!.Name}.{m.Name}: {e.InnerException?.Message}"); if (e.InnerException is not Fail) Console.WriteLine(e.InnerException); }
        }
        Console.WriteLine($"\n{pass}/{pass + fail} passed in {sw.Elapsed.TotalSeconds:0.0} s on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}), .NET {Environment.Version}");
        return fail == 0 ? 0 : 1;
    }
}

static class Fx {
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>A mid-latitude test site (not anyone's real observatory).</summary>
    public static Config Cfg(double lat = 40, double lon = -75) => new() {
        Site = new Site { Name = "Test site", Latitude = lat, Longitude = lon, TimeZone = "America/New_York", MinAltitude = 30, SunAltitude = -12, MoonSeparation = 15, MaxAirmass = 2.5 },
        Filters = new() {
            new FilterSpec { Name = "B", Aavso = "B", RefExposure = 40, MinExposure = 2, MaxExposure = 300, MaxCount = 6, ColourOffset = 0.6 },
            new FilterSpec { Name = "V", Aavso = "V", RefExposure = 20, MinExposure = 1, MaxExposure = 300, MaxCount = 6 },
            new FilterSpec { Name = "R", Aavso = "R", RefExposure = 15, MinExposure = 1, MaxExposure = 300, MaxCount = 6, ColourOffset = -0.4 },
            new FilterSpec { Name = "I", Aavso = "I", RefExposure = 20, MinExposure = 1, MaxExposure = 300, MaxCount = 6, ColourOffset = -0.8 },
        },
    };

    public static Night Night(Config c, int y = 2026, int m = 12, int d = 3) => StarCadence.Core.Planning.Night.ForDate(c.Site, new DateOnly(y, m, d), c.Planner.StepMinutes)!;

    /// <summary>A star that culminates near local midnight in early December at the test site.</summary>
    public static Target Star(string name, double ra = 75, double dec = 40, double mag = 11) =>
        new() { Name = name, RaDeg = ra, DecDeg = dec, Mag = mag, Source = "test" };

    /// <summary>Checks every block: inside the night, ordered, not overlapping, and observable for its exposures.</summary>
    public static void Valid(Plan plan, Night night) {
        var prevEnd = night.StartUtc.AddDays(-1);
        foreach (var b in plan.Blocks) {
            A.True(b.StartUtc >= night.StartUtc.AddSeconds(-1) && b.EndUtc <= night.EndUtc.AddSeconds(1), $"{b.Target} block outside the night");
            A.True(b.StartUtc >= prevEnd.AddSeconds(-1), $"{b.Target} overlaps the previous block ({b.StartUtc:HH:mm:ss} < {prevEnd:HH:mm:ss})");
            A.True(b.EndUtc > b.StartUtc && b.ExposeFromUtc >= b.StartUtc && b.ExposeFromUtc <= b.EndUtc, $"{b.Target} times out of order");
            var track = night.TrackOf(new Target { Name = b.Target, RaDeg = b.RaDeg, DecDeg = b.DecDeg });
            A.True(track.Throughout(b.ExposeFromUtc, b.EndUtc), $"{b.Target} not observable for its whole block");
            prevEnd = b.EndUtc;
        }
    }
}

static class AstroTests {
    [Test] static void JulianDates() {
        A.Near(AstroTime.JulianDate(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)), 2451545.0, 1e-9, "J2000");
        A.Near(AstroTime.JulianDate(new DateTime(1987, 4, 10, 0, 0, 0, DateTimeKind.Utc)), 2446895.5, 1e-9, "Meeus 12.a date");
        var t = new DateTime(2026, 11, 12, 3, 4, 5, DateTimeKind.Utc);
        A.True(Math.Abs((AstroTime.FromJulianDate(AstroTime.JulianDate(t)) - t).TotalMilliseconds) < 1, "round trip");
    }

    [Test] static void SiderealTime_Meeus12a() =>
        A.Near(AstroTime.GmstDegrees(2446895.5), 197.693195, 0.00001, "GMST 1987-04-10 0h");

    [Test] static void Sun_Meeus25a() {
        var (ra, dec, r) = Sun.Position(2448908.5 - AstroTime.TtMinusUtcSeconds / 86400.0);
        A.Near(ra, 198.38083, 0.01, "Sun RA"); A.Near(dec, -7.78507, 0.01, "Sun Dec"); A.Near(r, 0.99766, 0.0001, "Sun distance");
    }

    [Test] static void Moon_Meeus47a() {
        var jd = 2448724.5 - AstroTime.TtMinusUtcSeconds / 86400.0;
        var (lam, beta, dist) = Moon.Ecliptic(jd);
        A.Near(lam, 133.162655, 0.02, "Moon longitude"); A.Near(beta, -3.229126, 0.02, "Moon latitude"); A.Near(dist, 368409.7, 60, "Moon distance");
        var (ra, dec, _) = Moon.Position(jd);
        A.Near(ra, 134.688470, 0.03, "Moon RA"); A.Near(dec, 13.768368, 0.03, "Moon Dec");
    }

    [Test] static void Coordinates() {
        A.Near(Coords.ParseRa("05 34 31.9")!.Value, 83.632917, 1e-5, "RA spaced");
        A.Near(Coords.ParseRa("5h34m31.9s")!.Value, 83.632917, 1e-5, "RA hms");
        A.Near(Coords.ParseRa("83.632917")!.Value, 83.632917, 1e-9, "RA degrees");
        A.Near(Coords.ParseDec("-05:23:28")!.Value, -5.391111, 1e-5, "Dec colons");
        A.Near(Coords.ParseDec("-00 30 00")!.Value, -0.5, 1e-9, "Dec negative zero degrees");
        A.Near(Coords.ParseDec("+38 22 32.4")!.Value, 38.375667, 1e-5, "Dec spaced");
        A.True(Coords.ParseDec("95") is null && Coords.ParseRa("abc") is null, "rejects nonsense");
        A.Eq(Coords.FormatRa(83.632917), "05:34:31.9", "format RA");
        A.Eq(Coords.FormatDec(-5.391111), "-05:23:28", "format Dec");
        A.Near(AstroMath.Separation(10, 20, 10, 21), 1, 1e-9, "separation 1 deg");
        A.Near(AstroMath.Separation(0, 0, 90, 0), 90, 1e-9, "separation 90 deg");
    }

    [Test] static void Precession_Meeus21b() {
        // Theta Persei without its proper motion, J2000 -> 2028 Nov 13.19 TD (Meeus 21.b, proper motion removed).
        var ra0 = (2 + 44 / 60.0 + 11.986 / 3600) * 15; var dec0 = 49 + 13 / 60.0 + 42.48 / 3600;
        var (ra, dec) = Coords.PrecessFromJ2000(ra0, dec0, 2462088.69);
        A.Near(ra, 41.547214 - 28.86705 * 0.03425 * 15 / 3600, 0.0005, "RA of date");
        A.Near(dec, 49.348483 + 28.86705 * 0.0895 / 3600, 0.0005, "Dec of date");
    }

    [Test] static void Airmass() {
        A.Near(Coords.Airmass(90), 1, 0.001, "zenith");
        A.Near(Coords.Airmass(30), 1.995, 0.01, "30 degrees");
        A.Near(Coords.AltitudeForAirmass(2.0), 29.9, 0.2, "inverse");
    }

    [Test] static void Horizon() {
        var s = new Site { Horizon = { new(0, 10), new(90, 30), new(180, 10), new(270, 40) } };
        A.Near(s.HorizonAt(45), 20, 1e-9, "between points");
        A.Near(s.HorizonAt(315), 25, 1e-9, "wraps through north");
        A.Near(s.HorizonAt(-45), 25, 1e-9, "negative azimuth");
        A.Near(new Site().HorizonAt(123), 0, 0, "none");
    }

    [Test] static void LightTimeConversion() {
        // Star in the Sun's direction: light reaches the Sun about R/c (~8.2 min) before the Earth, so UTC is later.
        var jd = AstroTime.JulianDate(new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc));
        var (sra, sdec, _) = Sun.Position(jd);
        var lightMin = Sun.Position(jd).distanceAu * 499.004784 / 60;   // about 8.45 min in June (Earth near aphelion)
        var utc = Sun.BjdTdbToJdUtc(jd, sra, sdec);
        A.Near((utc - jd) * 1440, lightMin - 69.184 / 60, 0.02, "minutes (toward the Sun)");
        var (ara, adec) = (AstroMath.Norm360(sra + 180), -sdec);
        A.Near((Sun.BjdTdbToJdUtc(jd, ara, adec) - jd) * 1440, -lightMin - 69.184 / 60, 0.02, "minutes (away from the Sun)");
    }
}

static class PlanningTests {
    [Test] static void NightLimits() {
        var c = Fx.Cfg();
        var n = Fx.Night(c, 2026, 12, 21);
        // At latitude 40 N in late December, nautical darkness lasts about 12.6 h, from about 22:50 UTC.
        A.Near((n.EndUtc - n.StartUtc).TotalHours, 12.6, 0.3, "dark hours");
        A.True(n.StartUtc.Hour is 22 or 23, $"dark starts {n.StartUtc:HH:mm} UTC");
        A.Near(Sun.Altitude(AstroTime.JulianDate(n.StartUtc), 40, -75), -12, 0.05, "Sun at the limit");
        A.True(Night.ForDate(new Site { Latitude = 70, Longitude = 20, SunAltitude = -12 }, new DateOnly(2026, 6, 21)) is null, "no night in Arctic June");
        A.Eq(Night.NightDateOf(c.Site, new DateTime(2026, 12, 22, 9, 0, 0, DateTimeKind.Utc)), new DateOnly(2026, 12, 21), "pre-dawn belongs to the evening before");
    }

    [Test] static void ExposurePerBrightnessAndFilter() {
        var c = Fx.Cfg();
        var v = c.Filters[1];
        A.Eq(ExposureCalculator.For(v, new Target { Mag = 12 }).Seconds, 20.0, "reference star");
        A.Eq(ExposureCalculator.For(v, new Target { Mag = 14 }).Seconds, 120.0, "2 mag fainter: x6.3, rounded");
        var faint = ExposureCalculator.For(v, new Target { Mag = 17 });
        A.True(faint.Seconds == 300 && faint.Count == 6 && faint.Note!.Contains("faint"), $"faint star stacked: {faint}");
        var bright = ExposureCalculator.For(v, new Target { Mag = 6 });
        A.True(bright.Seconds == 1 && bright.Note!.Contains("saturate"), $"very bright star warned: {bright}");
        var warned = ExposureCalculator.For(v, new Target { Mag = 13, MagBright = 10 });
        A.True(warned.Seconds == 50 && warned.SaturatesBrighterThan == 12.0 && warned.Note is null, $"default: planned for mag 13, saturates brighter than 12.0: {warned}");
        var capped = ExposureCalculator.For(v, new Target { Mag = 13, MagBright = 10 }, capForBrightLimit: true);
        A.True(capped.Seconds == 6 && capped.Note!.Contains("not saturate"), $"capped for its bright limit (7.96 s, rounded down): {capped}");
        var b = ExposureCalculator.For(c.Filters[0], new Target { Mag = 12 });
        A.Eq(b.Seconds, 75.0, "B uses the colour offset (12.6 mag in B): 40 x 10^0.24 = 69.5 -> 75");
        var withBand = ExposureCalculator.For(c.Filters[0], new Target { Mag = 12, BandMags = { ["B"] = 12 } });
        A.Eq(withBand.Seconds, 40.0, "a measured B magnitude wins");
        var (fl, note) = ExposureCalculator.FiltersFor(c, new[] { "SR" });
        A.True(fl.Count == 1 && fl[0].Aavso == "R" && note!.Contains("nearest"), "Sloan r -> R");
    }

    [Test] static void FillsTheNightWithoutOverlaps() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var rnd = new Random(7);
        var targets = Enumerable.Range(0, 120).Select(i => Fx.Star($"Star {i}", rnd.NextDouble() * 360, rnd.NextDouble() * 100 - 20, 9 + rnd.NextDouble() * 6)).ToList();
        var plan = new Planner(c).Build(targets, new History(), n);
        Fx.Valid(plan, n);
        A.True(plan.Blocks.Count > 40, $"plenty planned ({plan.Blocks.Count})");
        A.True(plan.UsedFraction > 0.9, $"night used ({plan.UsedFraction:P0})");
        A.True(plan.Blocks.Select(b => b.Target).Distinct().Count() == plan.Blocks.Count, "daily cadence: one visit each");
        A.True(plan.Skipped.Any(s => s.Reason.Contains("not observable")), "far-south stars skipped with a reason");
    }

    [Test] static void RepeatCadenceWithinANight() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var fast = Fx.Star("Fast cadence", 60, 45); fast.CadenceDays = 0.1; fast.Priority = 1;
        var filler = Enumerable.Range(0, 60).Select(i => Fx.Star($"F{i}", 20 + i * 2.5, 30 + i % 20)).ToList();
        var plan = new Planner(c).Build(filler.Append(fast), new History(), n);
        Fx.Valid(plan, n);
        var visits = plan.Blocks.Where(b => b.Target == "Fast cadence").ToList();
        A.True(visits.Count >= 3, $"several visits ({visits.Count})");
        for (var i = 1; i < visits.Count; i++)
            A.True((visits[i].StartUtc - visits[i - 1].StartUtc).TotalHours >= 0.1 * 24 * c.Planner.DueFraction - 0.01, "spacing respects the cadence");
        A.Eq(visits.Last().Visit, visits.Count, "visits numbered");
    }

    [Test] static void HistoryHoldsBackStarsThatAreNotDue() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var h = new History();
        h.Record("Recent", n.StartUtc.AddDays(-0.3));
        h.Record("Older", n.StartUtc.AddDays(-2));
        var plan = new Planner(c).Build(new[] { Fx.Star("Recent"), Fx.Star("Older") }, h, n);
        A.True(plan.Blocks.All(b => b.Target != "Recent"), "observed 0.3 d ago, cadence 1 d: not due");
        A.True(plan.Blocks.Any(b => b.Target == "Older"), "observed 2 d ago: due");
        A.True(plan.Skipped.Any(s => s.Target == "Recent" && s.Reason.StartsWith("not due")), "reason given");
    }

    [Test] static void FixedTransitWindowIsKept() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var midUtc = n.StartUtc.AddHours(6);
        var midJd = AstroTime.JulianDate(midUtc);
        // Epoch chosen so that one transit is centred on midUtc (BJD = UTC + light-time + TT-UTC).
        var tr = new Target { Name = "Test-1b", RaDeg = 75, DecDeg = 40, Kind = TargetKind.Transit, Mag = 11, PeriodDays = 3.1, DurationHours = 2.5, Priority = 1 };
        tr.EpochJd = midJd + AstroTime.TtMinusUtcSeconds / 86400 + Sun.HeliocentricCorrectionDays(midJd, 75, 40) - 10 * 3.1;
        var filler = Enumerable.Range(0, 80).Select(i => Fx.Star($"F{i}", 30 + i * 1.5, 25 + i % 30)).ToList();
        var plan = new Planner(c).Build(filler.Append(tr), new History(), n);
        Fx.Valid(plan, n);
        var b = plan.Blocks.Single(x => x.Target == "Test-1b");
        var half = 2.5 * 30 + c.Planner.TransitBaselineMinutes;
        A.True(b.Fixed, "fixed");
        A.Near((b.ExposeFromUtc - midUtc).TotalMinutes, -half, 0.6, "exposures start at ingress minus baseline");
        A.Near((b.EndUtc - midUtc).TotalMinutes, half, 0.6, "and end at egress plus baseline");
        A.True(b.Steps.Count == 1 && b.Steps[0].Filter == "R" && b.Steps[0].Count > 50, $"one filter, filling the window ({b.Steps[0].Count} x {b.Steps[0].Seconds} s)");
        A.True(plan.Blocks.Count(x => !x.Fixed) > 20, "flexible targets fill around it");
    }

    [Test] static void OverlappingFixedWindowsKeepTheBetterOne() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var mid = AstroTime.JulianDate(n.StartUtc.AddHours(6));
        Target Tr(string name, double pri, double shiftH) {
            var jd = mid + shiftH / 24;
            return new Target { Name = name, RaDeg = 75, DecDeg = 40, Kind = TargetKind.Transit, Mag = 11, PeriodDays = 5, DurationHours = 2, Priority = pri,
                EpochJd = jd + AstroTime.TtMinusUtcSeconds / 86400 + Sun.HeliocentricCorrectionDays(jd, 75, 40) };
        }
        var plan = new Planner(c).Build(new[] { Tr("Low", 0.2, 0), Tr("High", 1.0, 1.0) }, new History(), n);
        A.True(plan.Blocks.Any(b => b.Target == "High") && plan.Blocks.All(b => b.Target != "Low"), "higher score wins the clash");
        A.True(plan.Skipped.Any(s => s.Target == "Low" && s.Reason.Contains("overlaps High")), "the loser is explained");
    }

    [Test] static void EclipseMinimaFromEpochAndPeriod() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var p = 0.4;
        var first = AstroTime.JulianDate(n.StartUtc.AddHours(3));
        var eb = new Target { Name = "EB Test", RaDeg = 75, DecDeg = 40, Kind = TargetKind.Event, Mag = 11, PeriodDays = p, EventHalfWidthMin = 45,
            EpochJd = first + AstroTime.TtMinusUtcSeconds / 86400 + Sun.HeliocentricCorrectionDays(first, 75, 40) - 100 * p };
        var wins = Events.WindowsBetween(eb, n.StartUtc, n.EndUtc, c.Planner);
        A.True(wins.Count >= 2, $"minima tonight ({wins.Count})");
        var mid0 = wins.Select(w => w.StartUtc.AddMinutes(45)).OrderBy(x => Math.Abs((x - n.StartUtc.AddHours(3)).TotalMinutes)).First();
        A.Near((mid0 - n.StartUtc.AddHours(3)).TotalMinutes, 0, 0.5, "minimum where predicted");
        var plan = new Planner(c).Build(new[] { eb }, new History(), n);
        Fx.Valid(plan, n);
        A.True(plan.Blocks.Count(b => b.Target == "EB Test" && b.Fixed) >= 1, "at least one minimum planned");
        // Phase 0.5 (secondary minimum) shifts by half a period.
        eb.EventPhase = 0.5;
        var wins2 = Events.WindowsBetween(eb, n.StartUtc, n.EndUtc, c.Planner);
        A.True(wins2.All(w2 => wins.All(w1 => Math.Abs((w2.StartUtc - w1.StartUtc).TotalHours) > 4.7)), "secondary minima between the primaries");
    }

    [Test] static void UserWindowsAndCampaignDates() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var win = Fx.Star("Windowed"); win.Windows.Add(new TimeWindow(n.StartUtc.AddHours(7), n.StartUtc.AddHours(8)));
        var ended = Fx.Star("Ended"); ended.ActiveUntilUtc = n.StartUtc.AddDays(-1);
        var later = Fx.Star("Later"); later.ActiveFromUtc = n.EndUtc.AddDays(2);
        var filler = Enumerable.Range(0, 60).Select(i => Fx.Star($"F{i}", 20 + i * 2.5, 30 + i % 20)).ToList();
        var plan = new Planner(c).Build(filler.Concat(new[] { win, ended, later }), new History(), n);
        Fx.Valid(plan, n);
        var b = plan.Blocks.Single(x => x.Target == "Windowed");
        A.True(b.StartUtc >= n.StartUtc.AddHours(7) && b.EndUtc <= n.StartUtc.AddHours(8), "inside its window");
        A.True(plan.Skipped.Any(s => s.Target == "Ended" && s.Reason.Contains("ended")), "ended campaign skipped");
        A.True(plan.Skipped.Any(s => s.Target == "Later" && s.Reason.Contains("starts")), "future campaign skipped");
    }

    [Test] static void SeriesBlocks() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var s = Fx.Star("Series star"); s.Kind = TargetKind.Series; s.SeriesMinutes = 90; s.WantFilters.Add("V");
        var plan = new Planner(c).Build(new[] { s }, new History(), n);
        var b = plan.Blocks.Single();
        A.Near(b.MinutesLong, 90 + c.Overheads.SlewAndCenterS / 60, 0.01, "block length");
        A.True(b.Steps.Single().Count >= 200, $"frames fill the series ({b.Steps.Single().Count})");
    }

    [Test] static void ReplanFromMidNight() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var mid = n.StartUtc.AddHours(5.5);
        var rest = Night.From(c.Site, mid, c.Planner.StepMinutes)!;
        A.Eq(rest.StartUtc, mid, "starts at the given time");
        A.Eq(rest.EndUtc, n.EndUtc, "same dawn");
        var targets = Enumerable.Range(0, 50).Select(i => Fx.Star($"S{i}", i * 7.2, 35)).ToList();
        var plan = new Planner(c).Build(targets, new History(), rest);
        Fx.Valid(plan, rest);
        A.True(plan.Blocks.All(b => b.StartUtc >= mid), "nothing before the re-plan time");
        A.True(Night.From(c.Site, n.EndUtc.AddHours(1))!.StartUtc > n.EndUtc.AddHours(6), "after dawn: the next night");
    }

    [Test] static void FixedWindowsBeyondTheBlockLimitAreReported() {
        var c = Fx.Cfg();
        c.Planner.MaxTargets = 1;
        var n = Fx.Night(c);
        Target Tr(string name, double hours) {
            var jd = AstroTime.JulianDate(n.StartUtc.AddHours(hours));
            return new Target { Name = name, RaDeg = 75, DecDeg = 40, Kind = TargetKind.Transit, Mag = 11, PeriodDays = 9, DurationHours = 1, Priority = 1,
                EpochJd = jd + AstroTime.TtMinusUtcSeconds / 86400 + Sun.HeliocentricCorrectionDays(jd, 75, 40) };
        }
        var plan = new Planner(c).Build(new[] { Tr("First", 4), Tr("Second", 8) }, new History(), n);
        A.Eq(plan.Blocks.Count, 1, "limit of one block");
        A.True(plan.Skipped.Any(s => s.Target == "Second" && s.Reason.Contains("limit")), "the second transit is reported, not silently dropped");
    }

    [Test] static void DaytimeMeansTheComingNight() {
        var c = Fx.Cfg();
        var tonight = Fx.Night(c, 2026, 12, 3);
        var morning = tonight.EndUtc.AddHours(3);           // after dawn on the 4th
        A.Eq(Night.CurrentOrNext(c.Site, morning), new DateOnly(2026, 12, 4), "after dawn: the coming night");
        A.Eq(Night.CurrentOrNext(c.Site, tonight.StartUtc.AddHours(2)), new DateOnly(2026, 12, 3), "during the night: this night");
        A.Eq(Night.CurrentOrNext(c.Site, tonight.StartUtc.AddHours(-3)), new DateOnly(2026, 12, 3), "afternoon: tonight");
        var rest = Night.From(c.Site, morning, 2)!;
        A.True(rest.StartUtc > morning && Night.NightDateOf(c.Site, rest.StartUtc) == new DateOnly(2026, 12, 4), "re-plan from the morning plans the coming night in full");
    }

    [Test] static void MergesDuplicateRows() {
        var a = Fx.Star("SS Cyg"); a.WantFilters.Add("V"); a.CadenceDays = 1;
        var b = Fx.Star("ss  cyg"); b.WantFilters.Add("B"); b.CadenceDays = 0.5; b.AlertOrCampaign = true; b.Priority = 0.9;
        var m = Planner.Merge(new[] { a, b }).ToList();
        A.Eq(m.Count, 1, "one star");
        A.True(m[0].WantFilters.SequenceEqual(new[] { "V", "B" }) && m[0].CadenceDays == 0.5 && m[0].AlertOrCampaign && m[0].Priority == 0.9, "merged fields");
    }

    [Test] static void ManyTargetsQuickly() {
        var c = Fx.Cfg();
        var sw = Stopwatch.StartNew();
        var n = Fx.Night(c);
        var rnd = new Random(1);
        var targets = Enumerable.Range(0, 1000).Select(i => Fx.Star($"S{i}", rnd.NextDouble() * 360, rnd.NextDouble() * 110 - 30, 8 + rnd.NextDouble() * 8)).ToList();
        foreach (var t in targets.Where((_, i) => i % 4 == 0)) t.CadenceDays = 0.25;
        var plan = new Planner(c).Build(targets, new History(), n);
        Fx.Valid(plan, n);
        A.True(sw.Elapsed.TotalSeconds < 20, $"1000 targets planned in {sw.Elapsed.TotalSeconds:0.0} s");
        A.True(plan.Blocks.Count >= 100, $"about a hundred visits in a winter night ({plan.Blocks.Count})");
    }

    [Test] static void ScoresExplainThemselves() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var overdue = Fx.Star("Overdue"); overdue.CadenceDays = 1; overdue.LastObservedUtc = n.StartUtc.AddDays(-10); overdue.AlertOrCampaign = true;
        var fresh = Fx.Star("Fresh", 76, 40); fresh.CadenceDays = 1; fresh.LastObservedUtc = n.StartUtc.AddHours(-12);
        var plan = new Planner(c).Build(new[] { fresh, overdue }, new History(), n);
        A.Eq(plan.Blocks[0].Target, "Overdue", "the overdue alert star goes first");
        A.True(plan.Blocks[0].Why.Any(w => w.Rule == "Cadence" && w.Score == 1), "cadence rule maxed");
    }

    [Test] static void LocalTimesWork() {
        A.True(TimeZones.Find("America/New_York") is not null, "IANA zone found");
        var t = TimeZones.ToLocal(new DateTime(2026, 12, 3, 3, 0, 0, DateTimeKind.Utc), "America/New_York");
        A.Eq(t.Hour, 22, "EST is UTC-5");
        A.Eq(TimeZones.ToLocal(new DateTime(2026, 12, 3, 3, 0, 0, DateTimeKind.Utc), "Not/AZone").Hour, 3, "unknown zone: UTC");
    }
}

static class SourceTests {
    [Test] static void ExoPlanetsCsvFormats() {
        var aavso = CsvTargets.Load(Fx.Path("test_aavso.csv"));
        var ab = aavso.Targets.First(t => t.Name == "AB And");
        A.True(ab.MagBright == 7.5 && ab.MagFaint == 8.9 && ab.PeriodDays == 0.5073 && ab.WantFilters.SequenceEqual(new[] { "V" }), "AAVSO format");
        A.Near(ab.RaDeg, (0 + 38 / 60.0 + 18.86 / 3600) * 15, 1e-6, "RA"); A.Near(ab.DecDeg, 38 + 22 / 60.0 + 32.4 / 3600, 1e-6, "Dec");
        var flex = CsvTargets.Load(Fx.Path("test_flexible.csv"));
        A.True(flex.Targets.Count >= 2 && flex.Targets.Any(t => t.Name == "XY Cen" && t.DecDeg < -59), "flexible column order");
        var manual = CsvTargets.Load(Fx.Path("test_manual.csv"));
        A.True(manual.Targets.Any(t => t.Name == "BD Dra" && t.Mag == 12.02 && t.PeriodDays == 0.58898), "manual format");
    }

    [Test] static void RaInHoursColumn() {
        var dir = Path.Combine(Path.GetTempPath(), "sc-ra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var f = Path.Combine(dir, "list.csv");
            File.WriteAllText(f, "Name,RA_h,Dec,Mag\nStar A,5.5,+20.5,11\n\"Star, B\",12.25,-5,12\n");
            var r = CsvTargets.Load(f);
            A.Eq(r.Targets.Count, 2, "both rows (one with a quoted comma)");
            A.Near(r.Targets[0].RaDeg, 82.5, 1e-9, "decimal hours");
            A.Eq(r.Targets[1].Name, "Star, B", "quoted name");
        } finally { Directory.Delete(dir, true); }
    }

    [Test] static void SpreadsheetCsvFromDecimalCommaLanguages() {
        // Excel in Germany, Portugal, Brazil... saves "CSV" with semicolons and decimal commas; some tools add a "sep=" line or use tabs.
        var dir = Path.Combine(Path.GetTempPath(), "sc-semi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var expectRa = (21 + 42 / 60.0 + 42.8 / 3600) * 15;
            foreach (var (file, text) in new[] {
                ("semicolon.csv", "Name;RA;Dec;Mag;Filters\r\nSS Cyg;21 42 42,8;+43 35 09,9;12,1;B,V\r\n\"Nova; test\";1,5;-2,25;<14,8;V\r\n"),
                ("sep-line.csv", "sep=;\nName;RA;Dec;Mag;Filters\nSS Cyg;21:42:42,8;+43:35:09,9;12,1;B,V\n\"Nova; test\";1,5;-2,25;<14,8;V\n"),
                ("tabs.txt", "Name\tRA\tDec\tMag\tFilters\nSS Cyg\t21 42 42,8\t+43 35 09,9\t12,1\tB,V\n\"Nova; test\"\t1,5\t-2,25\t<14,8\tV\n"),
            }) {
                var f = Path.Combine(dir, file);
                File.WriteAllText(f, text);
                var r = CsvTargets.Load(f);
                A.Eq(r.Targets.Count, 2, file + ": both rows");
                var ss = r.Targets[0];
                A.Near(ss.RaDeg, expectRa, 1e-6, file + ": RA with a decimal comma");
                A.Near(ss.DecDeg, 43 + 35 / 60.0 + 9.9 / 3600, 1e-6, file + ": Dec with a decimal comma");
                A.Eq(ss.Mag, 12.1, file + ": magnitude 12,1");
                A.True(ss.WantFilters.SequenceEqual(new[] { "B", "V" }), file + ": filter list B,V is not a number");
                A.Eq(r.Targets[1].Name, "Nova; test", file + ": quoted separator in a name");
                A.Near(r.Targets[1].DecDeg, -2.25, 1e-9, file + ": decimal degrees -2,25");
            }
            // An ordinary comma file is untouched: "12,5" there is two columns, as before.
            var plain = Path.Combine(dir, "plain.csv");
            File.WriteAllText(plain, "Name,RA,Dec,Mag\nStar,10.5,20.25,11.5\n");
            var pr = CsvTargets.Load(plain);
            A.True(pr.Targets.Count == 1 && pr.Targets[0].Mag == 11.5 && Math.Abs(pr.Targets[0].DecDeg - 20.25) < 1e-9, "comma CSV unchanged");
        } finally { Directory.Delete(dir, true); }
    }

    [Test] static void TargetToolJson() {
        var r = TargetTool.ParseJson(File.ReadAllText(Fx.Path("targettool.json")));
        A.Eq(r.Targets.Count, 3, "two SS Cyg rows + R CrB (spectroscopy, conjunction and no-coordinate rows dropped)");
        var merged = Planner.Merge(r.Targets).ToList();
        var ss = merged.Single(t => t.Name == "SS Cyg");
        A.True(ss.AlertOrCampaign && ss.Priority == 1 && ss.CadenceDays == 0.5, "alert, priority, shortest cadence");
        A.True(ss.WantFilters.OrderBy(x => x).SequenceEqual(new[] { "B", "I", "V" }), "filters from both rows");
        A.True(ss.Kind == TargetKind.Series, "time series asked for in other_info");
        A.True(ss.LastObservedUtc == new DateTime(2026, 11, 9, 3, 12, 0, DateTimeKind.Utc), "latest last-data-point kept (text form)");
        A.True(ss.MagBright == 7.7 && ss.MagFaint == 12.4 && ss.BandMags.Count == 0, "range (limits are not current band magnitudes)");
        var rcrb = merged.Single(t => t.Name == "R CrB");
        A.Near(rcrb.RaDeg, (15 + 48 / 60.0 + 34.4 / 3600) * 15, 1e-5, "sexagesimal RA from JSON");
        A.True(r.Messages.Any(m => m.Contains("spectroscopy")) && r.Messages.Any(m => m.Contains("conjunction")), "reasons logged");
    }

    [Test] static void ExoClockTransits() {
        var s = new SourceSettings();
        var r = ExoClock.ParseJson(File.ReadAllText(Fx.Path("exoclock.json")), s, new DateTime(2026, 12, 3, 0, 0, 0, DateTimeKind.Utc));
        A.Eq(r.Targets.Count, 1, "faint, shallow and low-priority planets left out");
        var t = r.Targets[0];
        A.True(t.Kind == TargetKind.Transit && t.PeriodDays == 2.1500082 && t.DurationHours == 3.1 && t.BandMags["R"] == 11.1, "fields");
        A.True(t.OcRangeMin > 0.5 && t.OcRangeMin < 5, $"ephemeris uncertainty in minutes ({t.OcRangeMin:0.00})");
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var wins = Events.WindowsBetween(t, n.StartUtc.AddDays(-3), n.EndUtc.AddDays(3), c.Planner);
        A.True(wins.Count >= 2, "transits found over a week");
        foreach (var w in wins)
            A.Near(w.Length.TotalMinutes, 2 * (3.1 * 30 + 60 + t.OcRangeMin), 0.05, "window = duration + 2 baselines + uncertainty");
    }

    [Test] static void BrokenOrOddSourceFilesDoNotStopThePlan() {
        var r = ExoClock.ParseJson("{\"_comment\": \"hello\", \"X-1b\": {\"name\": \"X-1b\", \"priority\": \"high\", \"ephem_mid_time\": 2461000.5, \"ephem_period\": 2, \"ra_j2000\": \"01:00:00\", \"dec_j2000\": \"+10:00:00\", \"v_mag\": 11}}", new SourceSettings());
        A.Eq(r.Targets.Count, 1, "comment entry ignored");
        A.Eq(TargetTool.ParseJson("[1, \"x\", {\"star_name\": \"A\", \"ra\": 10, \"dec\": 10}]").Targets.Count, 1, "non-object rows ignored");
        var dir = Path.Combine(Path.GetTempPath(), "sc-broken-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not json");
            var cfg = Fx.Cfg();
            cfg.Sources.ExoClock = true; cfg.Sources.ExoClockFile = "bad.json";
            cfg.WatchList.Add(new WatchItem { Name = "W", Ra = "05 00 00", Dec = "+40 00 00" });
            var all = new SourceLoader(cfg, dir, null).LoadAll(DateTime.UtcNow);
            A.True(all.Single(x => x.Source == "ExoClock").Messages.Single().Contains("could not read"), "broken file reported");
            A.Eq(all.SelectMany(x => x.Targets).Count(), 1, "the watch list still loads");
        } finally { Directory.Delete(dir, true); }
    }

    [Test] static void WatchListWindows() {
        var r = WatchList.Load(new[] {
            new WatchItem { Name = "A", Ra = "10 00 00", Dec = "+20 00 00", Windows = new() { "fixed:2026-12-04T03:00:00Z/2026-12-04T05:00:00Z", "bad" } },
            new WatchItem { Name = "EB", Ra = "10 00 00", Dec = "+20 00 00", Epoch = 60000.5, Period = 1.2 },
            new WatchItem { Name = "NoCoords" } });
        A.Eq(r.Targets.Count, 2, "two usable");
        var a = r.Targets[0];
        A.True(a.Windows.Count == 1 && a.Windows[0].Fixed && a.Windows[0].Length == TimeSpan.FromHours(2), "fixed window read");
        A.True(r.Messages.Any(m => m.Contains("'bad'")) && r.Messages.Any(m => m.Contains("NoCoords")), "problems reported");
        A.True(r.Targets[1].Kind == TargetKind.Event && r.Targets[1].EpochJd == 2460000.5, "reduced epoch expanded");
    }
}

static class ExportTests {
    static Plan SamplePlan(out Config c) {
        c = Fx.Cfg();
        var n = Fx.Night(c);
        var mid = AstroTime.JulianDate(n.StartUtc.AddHours(5));
        var tr = new Target { Name = "Tr & <1b>", RaDeg = 75, DecDeg = 40, Kind = TargetKind.Transit, Mag = 11, PeriodDays = 4, DurationHours = 2, Priority = 1,
            EpochJd = mid + AstroTime.TtMinusUtcSeconds / 86400 + Sun.HeliocentricCorrectionDays(mid, 75, 40) };
        var stars = Enumerable.Range(0, 30).Select(i => Fx.Star($"Star {i}", 30 + i * 3, 30)).Append(tr);
        return new Planner(c).Build(stars, new History(), n);
    }

    [Test] static void EkosFilesAreValidXml() {
        var plan = SamplePlan(out var c);
        var files = PlanExport.Ekos(plan, c, "/home/observer/ekos");
        var esl = XDocument.Parse(files["plan.esl"]);
        var jobs = esl.Root!.Elements("Job").ToList();
        A.Eq(jobs.Count, plan.Blocks.Count, "one job per block");
        var trJob = jobs.Single(j => j.Element("Name")!.Value == "Tr & <1b>");
        A.Eq(trJob.Element("StartupCondition")!.Element("Condition")!.Value, "At", "the transit starts at its time");
        A.True(jobs.Where(j => j != trJob).All(j => j.Element("StartupCondition")!.Element("Condition")!.Value == "ASAP"), "other jobs start ASAP, in plan order");
        var names = jobs.Select(j => j.Element("Name")!.Value).ToList();
        A.True(names.SequenceEqual(plan.Blocks.Select(b => b.Target)), "jobs listed in plan order (Ekos priority)");
        c.Export.EkosStartAllAt = true;
        var pinned = XDocument.Parse(PlanExport.Ekos(plan, c, "/x")["plan.esl"]);
        A.True(pinned.Root!.Elements("Job").All(j => j.Element("StartupCondition")!.Element("Condition")!.Value == "At"), "ekosStartAllAt pins every job");
        A.Eq(trJob.Element("CompletionCondition")!.Element("Condition")!.Value, "At", "fixed window ends on time");
        A.True(trJob.Element("Sequence")!.Value.StartsWith("/home/observer/ekos/") && trJob.Element("Sequence")!.Value.EndsWith(".esq"), "sequence path");
        A.Near(double.Parse(trJob.Element("Coordinates")!.Element("J2000RA")!.Value, CultureInfo.InvariantCulture), 5.0, 1e-6, "RA in hours");
        foreach (var (name, text) in files.Where(f => f.Key.EndsWith(".esq"))) {
            var q = XDocument.Parse(text);
            A.True(q.Root!.Elements("Job").All(j => double.Parse(j.Element("Exposure")!.Value, CultureInfo.InvariantCulture) > 0 && int.Parse(j.Element("Count")!.Value) > 0), name);
        }
    }

    [Test] static void CsvTextJsonAndNina() {
        var plan = SamplePlan(out var c);
        var old = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // decimal comma locale must not leak into files
            var csv = PlanExport.Csv(plan);
            var rows = StarCadence.Core.Util.Csv.Read(csv);
            A.Eq(rows.Count, plan.Blocks.Sum(b => b.Steps.Count), "one row per step");
            A.True(rows.All(r => double.TryParse(r["exposure_s"], NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !r["alt_start"].Contains(',')), "invariant numbers");
            var text = PlanExport.Text(plan);
            A.True(text.Contains("Night of") && text.Contains("[fixed]"), "readable text");
            var json = PlanExport.Json(plan);
            var back = System.Text.Json.JsonSerializer.Deserialize<Plan>(json, Config.Json)!;
            A.Eq(back.Blocks.Count, plan.Blocks.Count, "JSON round trip");
            var (nina, left) = PlanExport.NinaTargetScheduler(plan, c.Export);
            A.True(nina.StartsWith("Type,Name,Ra,Dec") && left.Count == 1 && !nina.Contains("Tr & <1b>"), "Target Scheduler CSV leaves out the transit");
            A.True(nina.Contains("VS "), "typed by exposure class");
            // Read it back the way Target Scheduler's CsvTargetLoader does: lower-case headers type,name,ra,dec,rotation,roi; RA as h:m:s.
            var ts = StarCadence.Core.Util.Csv.Read(nina);
            A.True(ts.All(r => r.ContainsKey("type") && r.ContainsKey("name") && r["ra"].Count(ch => ch == ':') == 2 && r["dec"].Count(ch => ch == ':') == 2 && r["roi"] == "100"), "Target Scheduler columns and formats");
            A.True(ts.All(r => Math.Abs(Coords.ParseRa(r["ra"])!.Value - plan.Blocks.First(b => b.Target == r["name"]).RaDeg) < 0.0001), "RA survives the round trip");
        } finally { CultureInfo.CurrentCulture = old; }
    }

    [Test] static void FileNamesAreSafeOnWindows() {
        A.Eq(PlanExport.Safe("CON"), "_CON", "device name");
        A.Eq(PlanExport.Safe("nul.csv"), "_nul.csv", "device name with an extension");
        A.Eq(PlanExport.Safe("COM1"), "_COM1", "serial port name");
        A.Eq(PlanExport.Safe("Comet 12P"), "Comet_12P", "ordinary name");
        A.Eq(PlanExport.Safe("R Leo."), "R_Leo", "trailing dot (Windows drops it)");
        A.Eq(PlanExport.Safe("TIC 1234:01 / b?"), "TIC_1234_01___b", "characters Windows forbids");
        A.Eq(PlanExport.Safe(" ..  "), "target", "nothing left");
    }

    [Test] static void SavingCopesWithAFileHeldOpen() {
        // OneDrive, antivirus or the app window can hold history.json open for a moment; on Windows a plain
        // replace then fails. The save must wait for it instead of losing the visit.
        var dir = Path.Combine(Path.GetTempPath(), "sc-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var path = Path.Combine(dir, "history.json");
            var h = new History(); h.Record("AB And", new DateTime(2026, 12, 3, 23, 0, 0, DateTimeKind.Utc)); h.Save(path);
            var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);   // no delete or write sharing, like a reader that locks
            var release = Task.Run(async () => { await Task.Delay(400); holder.Dispose(); });
            h.Record("SS Cyg", new DateTime(2026, 12, 4, 1, 0, 0, DateTimeKind.Utc));
            h.Save(path);
            release.GetAwaiter().GetResult();
            var back = History.Load(path);
            A.True(back.Last("SS Cyg") is not null && back.Last("AB And") is not null, "both visits saved once the file was free");
            A.True(!Directory.GetFiles(dir, "*.tmp").Any(), "no temporary files left behind");
            // Reading while another program has it open for writing works too.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) A.True(History.Load(path).Visits.Count == 2, "read while open elsewhere");
        } finally { Directory.Delete(dir, true); }
    }

    [Test] static void HistoryRoundTrip() {
        var dir = Path.Combine(Path.GetTempPath(), "starcadence-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var p = Path.Combine(dir, "history.json");
            var h = new History();
            var when = new DateTime(2026, 12, 4, 3, 0, 0, DateTimeKind.Utc);
            h.Record("SS Cyg", when); h.Record("SS Cyg", when.AddHours(-24)); h.Save(p);
            var back = History.Load(p);
            A.Eq(back.Last("ss cyg"), when, "last visit, any letter case");
            A.Eq(back.Last("SS Cyg", when.AddHours(-1)), when.AddHours(-24), "last visit before a time");
        } finally { Directory.Delete(dir, true); }
    }
}

/* ---------- the night runner ---------- */

sealed class TestClock : StarCadence.Core.Run.IClock {
    public DateTime UtcNow { get; set; }
    public TestClock(DateTime start) { UtcNow = start; }
    public Task Delay(TimeSpan span, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (span > TimeSpan.Zero) UtcNow += span; return Task.CompletedTask; }
    public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
}

/// <summary>An observatory that only moves the clock: slews take 60 s, exposures their length plus 3 s readout.</summary>
sealed class FakeObservatory : StarCadence.Core.Run.IObservatory {
    readonly TestClock _clock;
    public List<string> Actions { get; } = new();
    public List<StarCadence.Core.Run.ExposureRequest> Exposures { get; } = new();
    public HashSet<string> FailOnSlew { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<bool> Safe { get; set; } = () => true;
    public bool Finished { get; private set; }
    public FakeObservatory(TestClock clock) { _clock = clock; }
    public Task SlewAsync(double ra, double dec, string name, CancellationToken ct) {
        Actions.Add($"slew {name}");
        if (FailOnSlew.Contains(name)) throw new IOException("mount did not answer");
        _clock.Advance(60); return Task.CompletedTask;
    }
    public Task SetFilterAsync(string f, CancellationToken ct) { Actions.Add("filter " + f); _clock.Advance(5); return Task.CompletedTask; }
    public Task ExposeAsync(StarCadence.Core.Run.ExposureRequest r, CancellationToken ct) {
        Actions.Add($"expose {r.Target} {r.Filter} {r.Seconds}"); Exposures.Add(r); _clock.Advance(r.Seconds + 3); return Task.CompletedTask;
    }
    public Task<bool> IsSafeAsync(CancellationToken ct) => Task.FromResult(Safe());
    public Task FinishAsync(CancellationToken ct) { Finished = true; return Task.CompletedTask; }
}

static class RunnerTests {
    static (StarCadence.Core.Run.NightRunner runner, FakeObservatory obs, TestClock clock, string hist, List<string> log) Make(Config c, IEnumerable<Target> targets, DateTime start, StarCadence.Core.Run.RunnerOptions? opt = null) {
        var clock = new TestClock(start);
        var obs = new FakeObservatory(clock);
        var hist = Path.Combine(Path.GetTempPath(), "sc-run-" + Guid.NewGuid().ToString("N") + ".json");
        var log = new List<string>();
        var list = targets.ToList();
        var runner = new StarCadence.Core.Run.NightRunner(c, obs, clock, _ => list, hist, log.Add, opt);
        return (runner, obs, clock, hist, log);
    }

    [Test] static void RunsAWholeNightAndRecordsVisits() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var targets = Enumerable.Range(0, 40).Select(i => Fx.Star($"S{i}", 20 + i * 6, 25 + i % 30, 10 + i % 5)).ToList();
        var (runner, obs, clock, hist, log) = Make(c, targets, n.StartUtc.AddHours(-1));
        var done = runner.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        A.True(done >= 20, $"many blocks done ({done})");
        A.True(clock.UtcNow >= n.EndUtc.AddMinutes(-6) && clock.UtcNow <= n.EndUtc.AddMinutes(1), $"ran until dawn ({clock.UtcNow:HH:mm} vs {n.EndUtc:HH:mm})");
        A.True(log[0].Contains("Waiting for dark"), "waited for dark first");
        A.True(obs.Finished, "FinishAsync called at the end");
        var h = History.Load(hist);
        A.Eq(h.Visits.Count, obs.Actions.Count(a => a.StartsWith("slew")), "one history entry per slewed target");
        A.True(obs.Actions.Count(a => a.StartsWith("slew S")) == obs.Actions.Where(a => a.StartsWith("slew")).Distinct().Count(), "daily cadence: no target twice");
        // every exposure is preceded by a slew to that target and a filter change
        var lastSlew = ""; var lastFilter = "";
        foreach (var a in obs.Actions) {
            if (a.StartsWith("slew ")) lastSlew = a[5..];
            else if (a.StartsWith("filter ")) lastFilter = a[7..];
            else { var parts = a.Split(' '); A.True(parts[1] == lastSlew && parts[2] == lastFilter, "exposure after slew and filter: " + a); }
        }
        A.Eq(runner.Status.Phase, StarCadence.Core.Run.RunnerPhase.Finished, "finished phase");
        File.Delete(hist);
    }

    [Test] static void FixedWindowStartsOnTimeAndStopsOnTime() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var midUtc = n.StartUtc.AddHours(3);
        var midJd = AstroTime.JulianDate(midUtc);
        var tr = new Target { Name = "Tr-1b", RaDeg = 75, DecDeg = 40, Kind = TargetKind.Transit, Mag = 11, PeriodDays = 3.1, DurationHours = 1.0, Priority = 1,
            EpochJd = midJd + AstroTime.TtMinusUtcSeconds / 86400 + Sun.HeliocentricCorrectionDays(midJd, 75, 40) };
        var filler = Enumerable.Range(0, 6).Select(i => Fx.Star($"F{i}", 60 + i * 5, 40)).ToList();
        var opt = new StarCadence.Core.Run.RunnerOptions { EndMargin = TimeSpan.FromHours(7) };   // stop early: keeps the test short
        var (runner, obs, clock, hist, log) = Make(c, filler.Append(tr), n.StartUtc, opt);
        runner.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        var tx = obs.Exposures.Where(e => e.Target == "Tr-1b").ToList();
        A.True(tx.Count > 30, $"transit got many frames ({tx.Count})");
        var half = TimeSpan.FromMinutes(1.0 * 30 + c.Planner.TransitBaselineMinutes);
        var slewIdx = obs.Actions.IndexOf("slew Tr-1b");
        A.True(slewIdx > 0, "slewed to the transit after other work");
        // The first transit frame starts within a minute of ingress minus baseline; the last ends before egress plus baseline.
        var firstAt = log.First(l => l.Contains("Tr-1b: R")).Substring(0, 8);
        var windowStart = (midUtc - half).ToString("HH:mm:ss");
        A.True(Math.Abs((TimeSpan.Parse(firstAt) - TimeSpan.Parse(windowStart)).TotalSeconds) < 90, $"first transit frame at {firstAt}, window starts {windowStart}");
        A.True(log.Any(l => l.Contains("Tr-1b: window over") || l.Contains("Tr-1b: done")), "finished the window");
        var lastTx = log.Last(l => l.Contains("Tr-1b: R")).Substring(0, 8);
        A.True(TimeSpan.Parse(lastTx) < (midUtc + half).TimeOfDay, $"last transit frame ({lastTx}) before the window ends ({(midUtc + half):HH:mm:ss})");
        A.True(tx.All(e => e.Filter == "R"), "transit in one filter");
        File.Delete(hist);
    }

    [Test] static void FailedTargetIsSkippedAndRetriedLater() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var bad = Fx.Star("Bad", 75, 40); bad.Priority = 1; bad.CadenceDays = 0.2;
        var good = Fx.Star("Good", 76, 41); good.CadenceDays = 0.2;
        var opt = new StarCadence.Core.Run.RunnerOptions { EndMargin = TimeSpan.FromHours(6), RetryAfter = TimeSpan.FromHours(2) };
        var (runner, obs, clock, hist, log) = Make(c, new[] { bad, good }, n.StartUtc, opt);
        obs.FailOnSlew.Add("Bad");
        runner.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        A.True(obs.Actions.Count(a => a == "slew Bad") is >= 2 and <= 4, $"Bad retried every ~2 h, not every loop ({obs.Actions.Count(a => a == "slew Bad")})");
        A.True(obs.Exposures.Any(e => e.Target == "Good"), "the good star was still observed");
        A.True(runner.Status.BlocksFailed >= 1 && runner.Status.BlocksDone >= 1, "counts kept");
        File.Delete(hist);
    }

    [Test] static void UnsafeWeatherPausesTheNight() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var targets = Enumerable.Range(0, 10).Select(i => Fx.Star($"S{i}", 60 + i * 4, 40)).ToList();
        var opt = new StarCadence.Core.Run.RunnerOptions { EndMargin = TimeSpan.FromHours(8) };
        var (runner, obs, clock, hist, log) = Make(c, targets, n.StartUtc, opt);
        var unsafeFrom = n.StartUtc.AddMinutes(20); var unsafeTo = n.StartUtc.AddMinutes(80);
        obs.Safe = () => clock.UtcNow < unsafeFrom || clock.UtcNow > unsafeTo;
        runner.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        A.True(log.Any(l => l.Contains("Unsafe")), "reported the unsafe spell");
        var during = obs.Exposures.Count(e => false);
        var times = log.Where(l => l.Contains(": slewing")).Select(l => TimeSpan.Parse(l[..8])).ToList();
        A.True(!times.Any(t => t > unsafeFrom.AddMinutes(1).TimeOfDay && t < unsafeTo.TimeOfDay), "no new target started while unsafe");
        A.True(obs.Exposures.Count > 0, "work resumed afterwards");
        File.Delete(hist);
    }

    [Test] static void CancellationStopsCleanly() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var targets = Enumerable.Range(0, 10).Select(i => Fx.Star($"S{i}", 60 + i * 4, 40)).ToList();
        var (runner, obs, clock, hist, log) = Make(c, targets, n.StartUtc);
        var cts = new CancellationTokenSource();
        runner.BlockFinished += (_, _) => { if (runner.Status.BlocksDone >= 3) cts.Cancel(); };
        var threw = false;
        try { runner.RunAsync(cts.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { threw = true; }
        A.True(!threw, "cancellation is not an error");
        A.Eq(runner.Status.Phase, StarCadence.Core.Run.RunnerPhase.Stopped, "stopped phase");
        A.True(obs.Finished, "observatory finished (parked) on stop");
        A.Eq(runner.Status.BlocksDone, 3, "stopped after three blocks");
        File.Delete(hist);
    }

    [Test] static void HistoryFileProblemsDoNotStopTheNight() {
        var c = Fx.Cfg();
        var n = Fx.Night(c);
        var targets = Enumerable.Range(0, 12).Select(i => Fx.Star($"S{i}", 20 + i * 20, 25 + i, 11)).ToList();
        var dir = Path.Combine(Path.GetTempPath(), "sc-hist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);   // the "history file" is a folder: every read and write of it fails
        var old = StarCadence.Core.Util.Files.RetryStep;
        StarCadence.Core.Util.Files.RetryStep = TimeSpan.FromMilliseconds(1);
        try {
            var clock = new TestClock(n.StartUtc.AddMinutes(5));
            var obs = new FakeObservatory(clock);
            var log = new List<string>();
            var runner = new StarCadence.Core.Run.NightRunner(c, obs, clock, _ => targets, dir, log.Add,
                new StarCadence.Core.Run.RunnerOptions { EndMargin = n.EndUtc - n.StartUtc - TimeSpan.FromHours(3) });
            var done = runner.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            // the same night with a normal history file, for comparison
            var clock2 = new TestClock(n.StartUtc.AddMinutes(5)); var obs2 = new FakeObservatory(clock2);
            var normal = Path.Combine(dir + "-ok.json");
            var done2 = new StarCadence.Core.Run.NightRunner(c, obs2, clock2, _ => targets, normal, _ => { },
                new StarCadence.Core.Run.RunnerOptions { EndMargin = n.EndUtc - n.StartUtc - TimeSpan.FromHours(3) }).RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            File.Delete(normal);
            A.True(done >= 3 && done == done2, $"kept observing as if nothing were wrong ({done} targets, {done2} with a working file)");
            A.True(log.Any(l => l.Contains("could not save")), "said the history could not be saved");
            var slews = obs.Actions.Where(a => a.StartsWith("slew")).ToList();
            A.Eq(slews.Count, slews.Distinct().Count(), "no target observed twice although nothing reached the file");
        } finally { StarCadence.Core.Util.Files.RetryStep = old; Directory.Delete(dir, true); }
    }

    [Test] static void KeepAwakeHoldsAndReleases() {
        using (var awake = StarCadence.Core.Run.KeepAwake.Start("StarCadence test")) {
            Console.WriteLine("      " + awake.How);
            if (OperatingSystem.IsWindows()) {
                A.True(awake.Active, "Windows accepted the request");
                var requests = Run("powercfg", "/requests");
                var me = Process.GetCurrentProcess().ProcessName;
                if (requests is null || requests.Contains("administrator", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("      (powercfg needs administrator rights here; not checked)");
                else A.True(requests.Contains(me, StringComparison.OrdinalIgnoreCase), $"powercfg /requests lists {me}:\n{requests}");
            } else if (OperatingSystem.IsMacOS()) {
                A.True(awake.Active && awake.Helper is { HasExited: false }, "caffeinate is running");
                var assertions = Run("pmset", "-g assertions") ?? "";
                A.True(assertions.Contains("caffeinate", StringComparison.OrdinalIgnoreCase), "pmset shows the caffeinate assertion");
            } else if (awake.Active) {
                A.True(awake.Helper is { HasExited: false }, "systemd-inhibit is holding the lock");
            }
            if (awake.Helper is { } helper) { awake.Dispose(); A.True(helper.HasExited, "helper ends when released"); }
        }
    }

    static string? Run(string exe, string args) {
        try {
            var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return output;
        } catch (Exception) { return null; }
    }

    [Test] static void ScaledClockRunsFast() {
        var start = new DateTime(2026, 12, 3, 22, 0, 0, DateTimeKind.Utc);
        var clk = new StarCadence.Core.Run.ScaledClock(start, 600);
        var t0 = clk.UtcNow;
        clk.Delay(TimeSpan.FromMinutes(5), CancellationToken.None).GetAwaiter().GetResult();   // 0.5 s real
        var dt = (clk.UtcNow - t0).TotalMinutes;
        A.True(dt >= 5 && dt < 6.5, $"5 simulated minutes passed in half a second ({dt:0.0})");
    }
}

/* ---------- INDI, against a real INDI server when one is available ---------- */

static class IndiTests {
    [Test] static void FitsHeaderPatching() {
        // A minimal FITS: one header block, 4 pixels of 16-bit data.
        var cards = new[] { "SIMPLE  =                    T", "BITPIX  =                   16", "NAXIS   =                    2", "NAXIS1  =                    2", "NAXIS2  =                    2", "FILTER  = 'Luminance'", "END" };
        var header = string.Concat(cards.Select(c => c.PadRight(80))).PadRight(2880);
        var data = System.Text.Encoding.ASCII.GetBytes(header).Concat(new byte[2880]).ToArray();
        data[2880] = 1; data[2881] = 2;
        var patched = StarCadence.Core.Indi.Fits.WithCards(data, new (string, object?, string?)[] { ("OBJECT", "SS Cyg", "target"), ("FILTER", "V", null), ("RA", 325.678, "deg") });
        var h = StarCadence.Core.Indi.Fits.ReadHeader(patched);
        A.Eq(h["OBJECT"], "SS Cyg", "card added"); A.Eq(h["FILTER"], "V", "card replaced"); A.Eq(h["RA"], "325.678", "number card");
        A.True(patched.Length == data.Length && patched[2880] == 1 && patched[2881] == 2, "data untouched, header still one block");
        // Enough cards to need a second header block: the data must move, not be overwritten.
        var many = Enumerable.Range(0, 40).Select(i => ($"KEY{i:00}", (object?)i, (string?)null)).ToArray();
        var grown = StarCadence.Core.Indi.Fits.WithCards(data, many);
        A.True(grown.Length == data.Length + 2880 && grown[5760] == 1 && grown[5761] == 2, "header grew by one block and the data followed");
        A.Eq(StarCadence.Core.Indi.IndiClient.Sexagesimal("12:30:00"), 12.5, "sexagesimal");
        A.Eq(StarCadence.Core.Indi.IndiClient.Sexagesimal("-05 30 00"), -5.5, "negative sexagesimal");
    }

    /// <summary>Runs only when an INDI server with the simulator drivers is reachable (STARCADENCE_INDI=host:port, default localhost:7624).</summary>
    [Test] static void RealIndiServer_SlewFilterExposeAndSave() {
        var spec = Environment.GetEnvironmentVariable("STARCADENCE_INDI") ?? "localhost:7624";
        var host = spec.Split(':')[0]; var port = spec.Contains(':') ? int.Parse(spec.Split(':')[1]) : 7624;
        try { using var probe = new System.Net.Sockets.TcpClient(); probe.Connect(host, port); }
        catch (Exception) { Console.WriteLine($"      (no INDI server at {spec}; skipped)"); return; }

        var c = Fx.Cfg();
        c.Indi.Host = host; c.Indi.Port = port; c.Indi.SettleSeconds = 0.2; c.Indi.ParkAtEnd = true;
        var dir = Path.Combine(Path.GetTempPath(), "sc-indi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        var obs = new StarCadence.Core.Indi.IndiObservatory(c.Indi, c.Filters, Path.Combine(dir, "images"), log.Add);
        try {
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            obs.ConnectAsync(cts.Token).GetAwaiter().GetResult();
            A.True(obs.Mount.Length > 0 && obs.Camera.Length > 0, $"devices found: mount '{obs.Mount}', camera '{obs.Camera}', wheel '{obs.FilterWheel}'");
            // Name the simulator's filters the way the configuration does, as an observer would in Ekos.
            if (obs.FilterWheel.Length > 0) obs.NameFiltersAsync(new[] { "B", "V", "R", "I" }, cts.Token).GetAwaiter().GetResult();
            A.True(obs.FilterSlots.ContainsKey("V"), "wheel knows V");
            // A target that is up for the simulator's default site at the current time: use the local sidereal meridian.
            var lstDeg = AstroTime.LmstDegrees(AstroTime.JulianDate(DateTime.UtcNow), -75);
            var ra = lstDeg; var dec = 40.0;
            obs.SlewAsync(ra, dec, "Sim Target", cts.Token).GetAwaiter().GetResult();
            obs.SetFilterAsync("V", cts.Token).GetAwaiter().GetResult();
            obs.ExposeAsync(new StarCadence.Core.Run.ExposureRequest("Sim Target", "V", "V", 1.0, 1, 2, ra, dec, TargetKind.Variable, null, DateTime.UtcNow.AddMinutes(5)), cts.Token).GetAwaiter().GetResult();
            obs.SetFilterAsync("B", cts.Token).GetAwaiter().GetResult();
            obs.ExposeAsync(new StarCadence.Core.Run.ExposureRequest("Sim Target", "B", "B", 0.5, 2, 2, ra, dec, TargetKind.Variable, null, DateTime.UtcNow.AddMinutes(5)), cts.Token).GetAwaiter().GetResult();
            A.Eq(obs.FramesSaved, 2, "two frames saved");
            var bytes = File.ReadAllBytes(obs.SavedFiles[0]);
            var h = StarCadence.Core.Indi.Fits.ReadHeader(bytes);
            A.True(StarCadence.Core.Indi.Fits.LooksLikeFits(bytes) && bytes.Length > 2880 * 2, $"a real FITS file ({bytes.Length} bytes)");
            A.Eq(h["OBJECT"], "Sim Target", "OBJECT card"); A.Eq(h["FILTER"], "V", "FILTER card");
            A.True(h.ContainsKey("EXPTIME") && Math.Abs(double.Parse(h["EXPTIME"], CultureInfo.InvariantCulture) - 1.0) < 0.01, $"EXPTIME from the driver ({h.GetValueOrDefault("EXPTIME")})");
            A.True(h.ContainsKey("DATE-OBS"), "DATE-OBS from the driver");
            A.True(Path.GetFileName(obs.SavedFiles[1]).StartsWith("Sim_Target_B_"), "file named after target and filter");
            obs.FinishAsync(cts.Token).GetAwaiter().GetResult();
            A.True(log.Any(l => l.Contains("parking")), "parked at the end");

            // Now a short planned run through the night runner, at 600x speed, with sub-second exposures.
            foreach (var f in c.Filters) { f.RefExposure = 0.5; f.MinExposure = 0.3; f.MaxExposure = 1; }
            c.Overheads.SlewAndCenterS = 5; c.Overheads.FilterChangeS = 1; c.Overheads.ReadoutS = 1;
            var night = Fx.Night(c);
            // 120x: a real second is two simulated minutes, so slews and exposures still take their real time.
            var clock = new StarCadence.Core.Run.ScaledClock(night.StartUtc.AddMinutes(30), 120);
            var hist = Path.Combine(dir, "history.json");
            var targets = Enumerable.Range(0, 3).Select(i => Fx.Star($"Run {i}", 70 + i * 3, 40, 12)).ToList();
            var opt = new StarCadence.Core.Run.RunnerOptions { EndMargin = (night.EndUtc - night.StartUtc) - TimeSpan.FromHours(3) };   // 2.5 simulated hours
            var runner = new StarCadence.Core.Run.NightRunner(c, obs, clock, _ => targets, hist, log.Add, opt);
            var done = runner.RunAsync(new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token).GetAwaiter().GetResult();
            A.True(done >= 2, $"the runner observed targets through INDI ({done} blocks, {obs.FramesSaved} frames)");
            A.True(History.Load(hist).Visits.Count >= 2, "visits recorded");
            Console.WriteLine($"      INDI run: {done} targets, {obs.FramesSaved} frames; " + string.Join(" | ", log.Where(l => l.Contains("slewing") || l.Contains("done (")).Take(6)));
        } finally {
            obs.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(dir, true);
        }
    }
}
