// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using StarCadence.Core.Astro;
using StarCadence.Core.Model;
using StarCadence.Core.Planning;
using StarCadence.Core.Util;

namespace StarCadence.Core.Export;

public static class PlanExport {
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Json(Plan plan) => JsonSerializer.Serialize(plan, Config.Json);

    /// <summary>One row per filter step: easy to open in a spreadsheet.</summary>
    public static string Csv(Plan plan) {
        var sb = new StringBuilder();
        sb.Append(Util.Csv.Line(new object?[] { "start_utc", "expose_utc", "end_utc", "start_local", "target", "kind", "ra", "dec", "filter", "aavso", "exposure_s", "count", "plan_mag", "fixed", "window", "visit", "alt_start", "alt_end", "score", "notes" })).Append("\r\n");
        foreach (var b in plan.Blocks)
            foreach (var s in b.Steps)
                sb.Append(Util.Csv.Line(new object?[] {
                    Iso(b.StartUtc), Iso(b.ExposeFromUtc), Iso(b.EndUtc), plan.Local(b.StartUtc, "yyyy-MM-dd HH:mm"), b.Target, b.Kind.ToString(),
                    Coords.FormatRa(b.RaDeg), Coords.FormatDec(b.DecDeg), s.Filter, s.Aavso, s.Seconds, s.Count, s.PlanMag,
                    b.Fixed ? "yes" : "", b.Window, b.Visit, b.AltStart, b.AltEnd, b.Score, string.Join("; ", b.Notes) })).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>A readable night plan.</summary>
    public static string Text(Plan plan, bool showSkipped = true) {
        var sb = new StringBuilder();
        var tzName = string.IsNullOrWhiteSpace(plan.TimeZone) || TimeZones.Find(plan.TimeZone) is null ? "UTC" : plan.TimeZone;
        sb.AppendLine($"Night of {plan.NightOf:yyyy-MM-dd} at {plan.Site}  (times {tzName})");
        sb.AppendLine($"Dark from {plan.Local(plan.NightStartUtc)} to {plan.Local(plan.NightEndUtc)}" +
                      (plan.PlannedFromUtc > plan.NightStartUtc ? $", planned from {plan.Local(plan.PlannedFromUtc)}" : "") +
                      $"; Moon {plan.MoonIllumination * 100:0}% lit");
        sb.AppendLine($"{plan.Blocks.Count} blocks, {plan.Blocks.Select(b => b.Target).Distinct().Count()} targets from {plan.Candidates} candidates; {plan.UsedFraction * 100:0}% of the dark time used");
        sb.AppendLine();
        foreach (var b in plan.Blocks) {
            var steps = string.Join("  ", b.Steps.Select(s => $"{s.Filter} {s.Count}x{s.Seconds.ToString("0.#", Inv)}s"));
            var tag = b.Fixed ? " [fixed]" : b.Visit > 1 ? $" [visit {b.Visit}]" : "";
            sb.AppendLine($"{plan.Local(b.StartUtc)}-{plan.Local(b.EndUtc)}  {b.Target,-22} {b.Kind,-8}{tag}  alt {b.AltStart:0}->{b.AltEnd:0}  {steps}");
            var top = b.Why.OrderByDescending(w => w.Part).Take(3).Where(w => w.Part > 0).Select(w => $"{w.Rule} {w.Score:0.00}");
            var line = $"      score {b.Score:0.00} ({string.Join(", ", top)})";
            if (b.Window is not null) line += $"; {b.Window}";
            sb.AppendLine(line);
            foreach (var n in b.Notes) sb.AppendLine("      - " + n);
        }
        if (showSkipped && plan.Skipped.Count > 0) {
            sb.AppendLine();
            sb.AppendLine($"Not planned ({plan.Skipped.Count}):");
            foreach (var g in plan.Skipped.GroupBy(s => s.Target)) sb.AppendLine($"  {g.Key}: {string.Join("; ", g.Select(x => x.Reason).Distinct())}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// N.I.N.A. Target Scheduler bulk-import CSV (Type, Name, Ra, Dec, Rotation, ROI): one row per target, typed by its
    /// longest exposure so each type maps to an exposure template. Target Scheduler has no time windows or repeat
    /// cadence, so fixed-window blocks are left out and listed in the returned notes.
    /// </summary>
    public static (string csv, List<string> leftOut) NinaTargetScheduler(Plan plan, ExportSettings ex) {
        var sb = new StringBuilder("Type,Name,Ra,Dec,Rotation,ROI\r\n");
        var left = new List<string>();
        foreach (var g in plan.Blocks.GroupBy(b => b.Target)) {
            var b = g.First();
            if (g.Any(x => x.Fixed)) { left.Add($"{b.Target} ({g.First(x => x.Fixed).Window})"); continue; }
            var type = b.Kind == TargetKind.Series ? "VS series" : b.Kind == TargetKind.Standard ? "VS standard"
                : ex.NinaClasses.OrderBy(c => c.UpToSeconds).FirstOrDefault(c => b.Steps.Max(s => s.Seconds) <= c.UpToSeconds)?.Type ?? "VS long";
            sb.Append(Util.Csv.Line(new object?[] { type, b.Target, Coords.FormatRa(b.RaDeg, 2), Coords.FormatDec(b.DecDeg, 1), 0, 100 })).Append("\r\n");
        }
        return (sb.ToString(), left);
    }

    /// <summary>
    /// KStars/Ekos scheduler list (.esl) plus one capture sequence (.esq) per block, for macOS/Linux/Windows observatories
    /// running Ekos. Jobs are listed in plan order; fixed windows start and finish at their times ("At"). Format as
    /// written by KStars (ekos/scheduler/schedulerprocess.cpp, ekos/capture/sequencejob.cpp).
    /// </summary>
    public static Dictionary<string, string> Ekos(Plan plan, Config cfg, string sequenceDir) {
        var files = new Dictionary<string, string>();
        var esl = new StringBuilder();
        esl.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<SchedulerList version='2.2'>\n");
        esl.Append($"<Profile>{X(cfg.Export.EkosProfile)}</Profile>\n");
        var i = 0;
        foreach (var b in plan.Blocks) {
            i++;
            var seqName = $"{i:000}_{Safe(b.Target)}.esq";
            files[seqName] = Esq(b, cfg);
            var seqPath = Path.Combine(sequenceDir, seqName).Replace('\\', '/');
            esl.Append("<Job>\n<JobType lead='true'/>\n");
            esl.Append($"<Name>{X(b.Target)}</Name>\n<Group></Group>\n");
            esl.Append($"<Coordinates>\n<J2000RA>{(b.RaDeg / 15).ToString("0.######", Inv)}</J2000RA>\n<J2000DE>{b.DecDeg.ToString("0.######", Inv)}</J2000DE>\n</Coordinates>\n");
            esl.Append("<PositionAngle>0</PositionAngle>\n");
            esl.Append($"<Sequence>{X(seqPath)}</Sequence>\n");
            // Fixed windows start at their time. Other jobs start "as soon as possible" in plan order (Ekos treats list
            // order as priority), so one late job does not knock the rest of the night out; set ekosStartAllAt to pin them all.
            esl.Append(b.Fixed || cfg.Export.EkosStartAllAt
                ? $"<StartupCondition>\n<Condition value='{Iso(b.StartUtc)}'>At</Condition>\n</StartupCondition>\n"
                : "<StartupCondition>\n<Condition>ASAP</Condition>\n</StartupCondition>\n");
            esl.Append("<Constraints>\n");
            esl.Append($"<Constraint value='{cfg.Site.MinAltitude.ToString(Inv)}'>MinimumAltitude</Constraint>\n");
            if (cfg.Site.MoonSeparation > 0) esl.Append($"<Constraint value='{cfg.Site.MoonSeparation.ToString(Inv)}'>MoonSeparation</Constraint>\n");
            esl.Append("<Constraint>EnforceTwilight</Constraint>\n");
            if (cfg.Site.Horizon.Count > 0) esl.Append("<Constraint>EnforceArtificialHorizon</Constraint>\n");
            esl.Append("</Constraints>\n<CompletionCondition>\n");
            esl.Append(b.Fixed || b.Kind == TargetKind.Series ? $"<Condition value='{Iso(b.EndUtc)}'>At</Condition>\n" : "<Condition>Sequence</Condition>\n");
            esl.Append("</CompletionCondition>\n<Steps>\n");
            foreach (var st in cfg.Export.EkosSteps) esl.Append($"<Step>{X(st)}</Step>\n");
            esl.Append("</Steps>\n</Job>\n");
        }
        esl.Append("<SchedulerAlgorithm value='1'/>\n</SchedulerList>\n");
        files["plan.esl"] = esl.ToString();
        return files;
    }

    static string Esq(PlanBlock b, Config cfg) {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<SequenceQueue version='2.6'>\n");
        sb.Append("<GuideDeviation enabled='false'>2</GuideDeviation>\n<GuideStartDeviation enabled='false'>2</GuideStartDeviation>\n");
        sb.Append("<HFRCheck enabled='false'>\n<HFRDeviation>0.5</HFRDeviation>\n<HFRCheckAlgorithm>0</HFRCheckAlgorithm>\n<HFRCheckThreshold>10</HFRCheckThreshold>\n<HFRCheckFrames>1</HFRCheckFrames>\n</HFRCheck>\n");
        sb.Append("<RefocusOnTemperatureDelta enabled='false'>1</RefocusOnTemperatureDelta>\n<RefocusEveryN enabled='false'>60</RefocusEveryN>\n<RefocusOnMeridianFlip enabled='false'/>\n");
        foreach (var s in b.Steps) {
            sb.Append("<Job>\n");
            sb.Append($"<Exposure>{s.Seconds.ToString("0.###", Inv)}</Exposure>\n<Format>FITS</Format>\n<Encoding>FITS</Encoding>\n");
            sb.Append("<Binning>\n<X>1</X>\n<Y>1</Y>\n</Binning>\n<Frame>\n<X>0</X>\n<Y>0</Y>\n<W>0</W>\n<H>0</H>\n</Frame>\n");
            sb.Append($"<Filter>{X(s.Filter)}</Filter>\n<Type>Light</Type>\n<Count>{s.Count}</Count>\n<Delay>0</Delay>\n");
            sb.Append($"<TargetName>{X(b.Target)}</TargetName>\n");
            sb.Append("<GuideDitherPerJob>-1</GuideDitherPerJob>\n");
            if (!string.IsNullOrWhiteSpace(cfg.Export.EkosImageDir)) sb.Append($"<FITSDirectory>{X(cfg.Export.EkosImageDir)}</FITSDirectory>\n");
            sb.Append("<UploadMode>0</UploadMode>\n</Job>\n");
        }
        sb.Append("</SequenceQueue>\n");
        return sb.ToString();
    }

    static string X(string? s) => SecurityElement.Escape(s ?? "") ?? "";
    static string Iso(DateTime t) => AstroTime.AsUtc(t).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv);

    public static string Safe(string name) {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim('_');
        return s.Length == 0 ? "target" : s;
    }
}
