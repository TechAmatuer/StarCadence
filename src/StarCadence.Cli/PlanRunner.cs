// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Globalization;
using StarCadence.Core.Export;
using StarCadence.Core.Model;
using StarCadence.Core.Planning;
using StarCadence.Core.Sources;

/// <summary>The result of planning a night, shared by the command line and the app window.</summary>
sealed class PlanRun {
    public required Plan Plan { get; init; }
    public required DateOnly Date { get; init; }
    public required string OutDir { get; init; }
    public List<string> Files { get; } = new();
    public List<string> Messages { get; } = new();
    public List<SourceResult> Sources { get; init; } = new();
    public string Text { get; set; } = "";
}

static class PlanRunner {
    public static readonly string[] AllFormats = { "txt", "json", "csv", "ekos", "nina" };

    /// <summary>
    /// Plans one night (or the rest of it, from a given time) and writes the chosen files.
    /// Returns null with an error message when there is no dark time.
    /// </summary>
    public static PlanRun? Run(Config cfg, string configDir, DateOnly? date, DateTime? fromUtc, string? outDir, IEnumerable<string> formats, out string? error) {
        error = null;
        Night? night;
        DateOnly d;
        var stem = "plan";
        if (fromUtc is DateTime t) {
            d = Night.CurrentOrNext(cfg.Site, t);
            night = Night.From(cfg.Site, t, cfg.Planner.StepMinutes);
            // Mid-night re-plans get their own files, keeping the full-night plan; before dark it is simply tonight's plan.
            if (night is not null && night.StartUtc <= t) stem = $"replan-{t:HHmm}";
        } else {
            d = date ?? Night.CurrentOrNext(cfg.Site, DateTime.UtcNow);
            night = Night.ForDate(cfg.Site, d, cfg.Planner.StepMinutes);
        }
        if (night is null) {
            error = $"No dark time on the night of {d:yyyy-MM-dd} at this site (the Sun never gets below {cfg.Site.SunAltitude} degrees).";
            return null;
        }

        var sources = new SourceLoader(cfg, configDir, null).LoadAll(night.StartUtc);
        var history = History.Load(Resolve(configDir, cfg.HistoryFile));
        var plan = new Planner(cfg).Build(sources.SelectMany(r => r.Targets), history, night, d);
        if (Night.DarkLimits(cfg.Site, d).start is DateTime darkStart && night.StartUtc > darkStart) {
            plan.NightStartUtc = darkStart;          // the report shows the real dark time
            plan.PlannedFromUtc = night.StartUtc;    // and that this plan starts later
        }

        var dir = Path.GetFullPath(outDir ?? Path.Combine(configDir, "plans", d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(dir);
        var run = new PlanRun { Plan = plan, Date = d, OutDir = dir, Sources = sources };
        var fmt = formats.Select(f => f.Trim().ToLowerInvariant()).ToHashSet();
        void Write(string name, string text) { var p = Path.Combine(dir, name); File.WriteAllText(p, text); run.Files.Add(p); }
        run.Text = PlanExport.Text(plan);
        if (fmt.Contains("txt")) Write(stem + ".txt", run.Text);
        if (fmt.Contains("json")) Write(stem + ".json", PlanExport.Json(plan));
        if (fmt.Contains("csv")) Write(stem + ".csv", PlanExport.Csv(plan));
        if (fmt.Contains("nina")) {
            var (csv, left) = PlanExport.NinaTargetScheduler(plan, cfg.Export);
            Write((stem == "plan" ? "nina-target-scheduler" : "nina-target-scheduler-" + stem) + ".csv", csv);
            if (left.Count > 0) run.Messages.Add($"The N.I.N.A. Target Scheduler file leaves out {left.Count} timed target(s), because Target Scheduler has no time windows: {string.Join(", ", left)}");
        }
        if (fmt.Contains("ekos")) {
            var ekosDir = Path.Combine(dir, stem == "plan" ? "ekos" : "ekos-" + stem);
            Directory.CreateDirectory(ekosDir);
            foreach (var (name, text) in PlanExport.Ekos(plan, cfg, cfg.Export.EkosSequenceDir ?? ekosDir)) {
                var p = Path.Combine(ekosDir, name);
                File.WriteAllText(p, text);
                if (name == "plan.esl") run.Files.Add(p);
            }
        }
        return run;
    }

    public static string Resolve(string dir, string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(dir, p));
}
