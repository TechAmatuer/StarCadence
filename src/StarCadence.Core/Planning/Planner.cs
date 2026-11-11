// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using StarCadence.Core.Astro;
using StarCadence.Core.Model;

namespace StarCadence.Core.Planning;

/// <summary>
/// Plans one night:
/// 1. fixed windows first (transits, eclipse minima, any window marked fixed): kept when the target is observable
///    for the whole window, best score first, no overlaps;
/// 2. the gaps are filled dispatch-style (as Target Scheduler does at run time): at each moment the highest-scoring
///    target that is due for its cadence, inside its windows and dates, and observable for its whole visit is chosen.
/// Calling it again from any later moment re-plans the rest of the night (after weather, a failure, or new alerts).
/// </summary>
public sealed class Planner {
    readonly Config _cfg;
    readonly ScoringEngine _engine;

    public Planner(Config cfg) {
        _cfg = cfg;
        _engine = new ScoringEngine(cfg.Planner.RuleWeights);
    }

    public ScoringEngine Engine => _engine;

    sealed class Cand {
        public required Target T;
        public required Night.Track Track;
        public List<ExposureStep> Visit = new();
        public double VisitSeconds;
        public string? FilterNote;
        public int Visits;
        public DateTime? LastOwn;
        public double CadenceDays;
        public int MaxVisits = 1;
    }

    sealed record FixedCand(Cand C, TimeWindow W, DateTime BlockStart, double Score, List<RuleResult> Why);

    public Plan Build(IEnumerable<Target> targets, History history, Night night, DateOnly? nightOf = null) {
        var p = _cfg.Planner;
        var slew = TimeSpan.FromSeconds(_cfg.Overheads.SlewAndCenterS);
        var plan = new Plan {
            Site = _cfg.Site.Name, NightOf = nightOf ?? Night.NightDateOf(_cfg.Site, night.StartUtc),
            NightStartUtc = night.StartUtc, NightEndUtc = night.EndUtc, PlannedFromUtc = night.StartUtc,
            TimeZone = _cfg.Site.TimeZone, MoonIllumination = Math.Round(night.MoonIllumination, 2),
        };
        var all = Merge(targets).ToList();
        plan.Candidates = all.Count;
        var nightHours = (night.EndUtc - night.StartUtc).TotalHours;

        var flex = new List<Cand>();
        var fixedCands = new List<FixedCand>();
        foreach (var t in all) {
            var track = night.TrackOf(t);
            var c = new Cand { T = t, Track = track, LastOwn = history.Last(t.Name, night.StartUtc) };
            c.CadenceDays = t.CadenceDays is > 0 ? t.CadenceDays.Value : p.DefaultCadenceDays;
            c.MaxVisits = c.CadenceDays < 1 ? Math.Max(1, (int)Math.Floor(nightHours / (c.CadenceDays * 24 * p.DueFraction)) + 1) : 1;

            if (!track.AnyVisible) { plan.Skipped.Add(new(t.Name, "not observable tonight (" + track.MainProblem() + ")")); continue; }
            if (t.ActiveUntilUtc is DateTime until && until < night.StartUtc) { plan.Skipped.Add(new(t.Name, $"campaign ended {until:yyyy-MM-dd}")); continue; }
            if (t.ActiveFromUtc is DateTime from && from > night.EndUtc) { plan.Skipped.Add(new(t.Name, $"starts {from:yyyy-MM-dd}")); continue; }

            var periodic = (t.Kind is TargetKind.Transit or TargetKind.Event) && t.EpochJd is not null && t.PeriodDays is > 0;
            var fixedWindows = (periodic ? Events.WindowsBetween(t, night.StartUtc, night.EndUtc, p) : new List<TimeWindow>())
                .Concat(t.Windows.Where(w => w.Fixed && w.Overlaps(night.StartUtc, night.EndUtc))).ToList();
            if (periodic || t.Windows.Any(w => w.Fixed)) {
                if (fixedWindows.Count == 0) { plan.Skipped.Add(new(t.Name, periodic ? "no event or transit window tonight" : "its window is not tonight")); continue; }
                var cycle = FixedCycle(t, out var note);
                c.Visit = cycle; c.FilterNote = note;
                foreach (var w in fixedWindows) {
                    var blockStart = w.StartUtc - slew < night.StartUtc ? night.StartUtc : w.StartUtc - slew;
                    if (w.StartUtc < night.StartUtc || w.EndUtc > night.EndUtc) { plan.Skipped.Add(new(t.Name, $"{w.Label}: window runs outside dark time")); continue; }
                    if (!track.Throughout(w.StartUtc, w.EndUtc)) {
                        plan.Skipped.Add(new(t.Name, $"{w.Label}: observable for only {track.Coverage(w.StartUtc, w.EndUtc):P0} of the window"));
                        continue;
                    }
                    var (score, why) = _engine.Score(new ScoreContext(t, w.StartUtc, night, track, null, c.LastOwn));
                    fixedCands.Add(new FixedCand(c, w, blockStart, score, why));
                }
                continue;
            }
            if (t.Kind is TargetKind.Transit or TargetKind.Event) {
                plan.Skipped.Add(new(t.Name, "no epoch/period or window given for this event target"));
                continue;
            }
            c.Visit = ExposureCalculator.ForVisit(_cfg, t, out var fnote);
            c.FilterNote = fnote;
            if (t.Kind == TargetKind.Series) {
                var mins = t.SeriesMinutes ?? p.DefaultSeriesMinutes;
                c.Visit = ExposureCalculator.Fill(_cfg, c.Visit.Select(s => s with { Count = 1 }).ToList(), mins * 60);
                c.VisitSeconds = _cfg.Overheads.SlewAndCenterS + mins * 60;
            } else {
                c.VisitSeconds = ExposureCalculator.VisitSeconds(_cfg, c.Visit);
            }
            flex.Add(c);
        }

        // 1. Fixed windows, best first, no overlaps (the slew before each window counts).
        var accepted = new List<FixedCand>();
        foreach (var f in fixedCands.OrderByDescending(f => f.Score)) {
            var clash = accepted.FirstOrDefault(a => f.BlockStart < a.W.EndUtc && f.W.EndUtc > a.BlockStart);
            if (clash is not null) { plan.Skipped.Add(new(f.C.T.Name, $"{f.W.Label}: overlaps {clash.C.T.Name} ({clash.W.Label}), which scored higher")); continue; }
            accepted.Add(f);
        }
        accepted.Sort((a, b) => a.BlockStart.CompareTo(b.BlockStart));

        // 2. Fill around them.
        var step = TimeSpan.FromMinutes(Math.Max(0.5, night.StepMinutes));
        var t0 = night.StartUtc;
        var fi = 0;
        var placedFixed = new HashSet<FixedCand>();
        Target? prev = null;
        var guard = 0;
        while (t0 < night.EndUtc && plan.Blocks.Count < p.MaxTargets && guard++ < 200000) {
            if (fi < accepted.Count && accepted[fi].BlockStart <= t0 + TimeSpan.FromSeconds(1)) {
                var f = accepted[fi++];
                placedFixed.Add(f);
                var blockStart = f.BlockStart > t0 ? f.BlockStart : t0;
                var steps = ExposureCalculator.Fill(_cfg, f.C.Visit, (f.W.EndUtc - f.W.StartUtc).TotalSeconds);
                var b = NewBlock(f.C, blockStart, f.W.StartUtc, f.W.EndUtc, steps, f.Score, f.Why, night);
                b.Fixed = true; b.Window = f.W.Label;
                plan.Blocks.Add(b);
                f.C.Visits++; f.C.LastOwn = f.W.StartUtc;
                prev = f.C.T;
                t0 = f.W.EndUtc;
                continue;
            }
            var limit = fi < accepted.Count ? accepted[fi].BlockStart : night.EndUtc;
            Cand? best = null; double bestScore = double.MinValue; List<RuleResult>? bestWhy = null; DateTime bestEnd = default;
            foreach (var c in flex) {
                if (c.Visits >= c.MaxVisits) continue;
                if (c.LastOwn is DateTime lo && (t0 - lo).TotalDays < c.CadenceDays * p.DueFraction) continue;
                var t = c.T;
                if (t.ActiveFromUtc is DateTime af && t0 < af) continue;
                if (t.ActiveUntilUtc is DateTime au && t0 > au) continue;
                var end = t0 + TimeSpan.FromSeconds(c.VisitSeconds);
                if (end > limit) continue;
                var windows = t.Windows.Where(w => !w.Fixed).ToList();
                if (windows.Count > 0 && !windows.Any(w => w.Contains(t0, end))) continue;
                if (!c.Track.Throughout(t0 + slew > end ? t0 : t0 + slew, end)) continue;
                var (score, why) = _engine.Score(new ScoreContext(t, t0, night, c.Track, prev, c.LastOwn, c.VisitSeconds / 60));
                if (score > bestScore) { best = c; bestScore = score; bestWhy = why; bestEnd = end; }
            }
            if (best is not null) {
                var expose = t0 + slew > bestEnd ? t0 : t0 + slew;
                var b = NewBlock(best, t0, expose, bestEnd, best.Visit, bestScore, bestWhy!, night);
                b.Visit = best.Visits + 1;
                plan.Blocks.Add(b);
                best.Visits++; best.LastOwn = t0;
                prev = best.T;
                t0 = bestEnd;
                continue;
            }
            if (t0 >= limit) { if (limit >= night.EndUtc) break; t0 = limit; continue; }
            var next = t0 + step;
            t0 = next > limit ? limit : next;
        }

        // What did not make it, and why.
        var planned = plan.Blocks.Select(b => Target.NormaliseName(b.Target)).ToHashSet();
        var skipped = plan.Skipped.Select(s => Target.NormaliseName(s.Target)).ToHashSet();
        foreach (var c in flex.Where(c => !planned.Contains(c.T.Key) && !skipped.Contains(c.T.Key))) {
            string why;
            var due = c.LastOwn is DateTime lo ? lo.AddDays(c.CadenceDays * p.DueFraction) : (DateTime?)null;
            if (due is DateTime d && d > night.StartUtc) {
                var ago = (night.StartUtc - c.LastOwn!.Value).TotalDays;
                why = d >= night.EndUtc
                    ? $"not due: observed here {ago:0.0} d ago, cadence {c.CadenceDays:0.##} d"
                    : $"not due until {d:HH:mm} UTC (observed here {ago:0.0} d ago, cadence {c.CadenceDays:0.##} d)" +
                      (FitsAnywhere(c, night, slew, d) ? "; then other targets scored higher" : "; not observable long enough after that");
            }
            else if (!FitsAnywhere(c, night, slew))
                why = $"needs {c.VisitSeconds / 60:0} min in one go and is never observable that long tonight";
            else if (plan.Blocks.Count >= p.MaxTargets)
                why = $"the plan reached its limit of {p.MaxTargets} blocks";
            else
                why = "no time left: other targets scored higher";
            plan.Skipped.Add(new(c.T.Name, why));
        }
        foreach (var f in accepted.Where(f => !placedFixed.Contains(f)))
            plan.Skipped.Add(new(f.C.T.Name, $"{f.W.Label}: the plan reached its limit of {p.MaxTargets} blocks"));
        return plan;
    }

    bool FitsAnywhere(Cand c, Night night, TimeSpan slew, DateTime? from = null) {
        var dur = TimeSpan.FromSeconds(c.VisitSeconds);
        for (var t = from ?? night.StartUtc; t + dur <= night.EndUtc; t = t.AddMinutes(night.StepMinutes))
            if (c.Track.Throughout(t + slew > t + dur ? t : t + slew, t + dur)) return true;
        return false;
    }

    /// <summary>The filter cycle for a fixed window: one filter for transits (the configured transit band, or the one asked for).</summary>
    List<ExposureStep> FixedCycle(Target t, out string? note) {
        if (t.Kind == TargetKind.Transit) {
            var want = t.WantFilters.Count > 0 ? t.WantFilters : new List<string> { _cfg.Planner.TransitFilter };
            var (filters, n) = ExposureCalculator.FiltersFor(_cfg, want);
            note = n;
            var f = filters[0];
            return new List<ExposureStep> { ExposureCalculator.For(f, t, _cfg.Planner.CapForBrightLimit) with { Count = 1 } };
        }
        var steps = ExposureCalculator.ForVisit(_cfg, t, out note);
        return steps.Select(s => s with { Count = 1 }).ToList();
    }

    PlanBlock NewBlock(Cand c, DateTime start, DateTime exposeFrom, DateTime end, List<ExposureStep> steps, double score, List<RuleResult> why, Night night) {
        var t = c.T;
        var b = new PlanBlock {
            Target = t.Name, Kind = t.Kind, RaDeg = t.RaDeg, DecDeg = t.DecDeg, StartUtc = start, ExposeFromUtc = exposeFrom, EndUtc = end,
            Steps = steps, Score = Math.Round(score, 3), Why = why, Source = t.Source,
            AltStart = Math.Round(c.Track.AltAt(exposeFrom), 1), AltEnd = Math.Round(c.Track.AltAt(end), 1),
        };
        if (c.FilterNote is not null) b.Notes.Add(c.FilterNote);
        b.Notes.AddRange(steps.Where(s => s.Note is not null).Select(s => $"{s.Filter}: {s.Note}").Distinct());
        var sat = steps.Where(s => s.SaturatesBrighterThan is not null).ToList();
        if (sat.Count > 0 && t.MagBright is double mb)
            b.Notes.Add($"saturates if brighter than {string.Join(", ", sat.Select(s => $"{s.Filter} {s.SaturatesBrighterThan:0.0}"))} (catalogue maximum {mb:0.0}): re-plan after an outburst alert");
        if (t.AlertOrCampaign) b.Notes.Add("AAVSO alert or campaign target");
        if (t.ActiveUntilUtc is DateTime u) b.Notes.Add($"wanted until {u:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(t.Notes)) b.Notes.Add(t.Notes);
        return b;
    }

    /// <summary>
    /// One entry per star: sources often list a star twice (once per filter, or in two lists). Keeps the highest
    /// priority, the shortest cadence, every wanted filter, every window and the most recent "last observed".
    /// </summary>
    public static IEnumerable<Target> Merge(IEnumerable<Target> targets) {
        var byKey = new Dictionary<string, Target>();
        var order = new List<string>();
        foreach (var t in targets) {
            if (string.IsNullOrWhiteSpace(t.Name)) continue;
            // Different transits/events of one host stay separate entries (their epochs differ).
            var key = t.Key + (t.Kind is TargetKind.Transit or TargetKind.Event ? "|" + t.Kind + "|" + t.EpochJd + "|" + t.EventPhase : "");
            if (!byKey.TryGetValue(key, out var m)) { byKey[key] = t.Clone(); order.Add(key); continue; }
            m.Priority = Math.Max(m.Priority, t.Priority);
            m.AlertOrCampaign |= t.AlertOrCampaign;
            m.Urgent |= t.Urgent;
            if (t.CadenceDays is > 0 && (m.CadenceDays is null || t.CadenceDays < m.CadenceDays)) m.CadenceDays = t.CadenceDays;
            m.LastObservedUtc = Rules.Latest(m.LastObservedUtc, t.LastObservedUtc);
            if (t.RecentObservations is int r) m.RecentObservations = m.RecentObservations is int mr ? Math.Min(mr, r) : r;
            foreach (var f in t.WantFilters) if (!m.WantFilters.Contains(f, StringComparer.OrdinalIgnoreCase)) m.WantFilters.Add(f);
            m.Windows.AddRange(t.Windows.Where(w => !m.Windows.Contains(w)));
            foreach (var (k, v) in t.BandMags) m.BandMags.TryAdd(k, v);
            m.Mag ??= t.Mag; m.MagBright ??= t.MagBright; m.MagFaint ??= t.MagFaint;
            m.SeriesMinutes = Math.Max(m.SeriesMinutes ?? 0, t.SeriesMinutes ?? 0) is var s && s > 0 ? s : null;
            if (t.Kind == TargetKind.Series) m.Kind = TargetKind.Series;
            if (t.ActiveUntilUtc is DateTime au && (m.ActiveUntilUtc is null || au > m.ActiveUntilUtc)) m.ActiveUntilUtc = au;
            foreach (var s2 in t.Sections) if (!m.Sections.Contains(s2)) m.Sections.Add(s2);
            if (!m.Source.Contains(t.Source)) m.Source = string.IsNullOrEmpty(m.Source) ? t.Source : m.Source + " + " + t.Source;
            if (!string.IsNullOrWhiteSpace(t.Notes) && !m.Notes.Contains(t.Notes)) m.Notes = (m.Notes + " " + t.Notes).Trim();
        }
        return order.Select(k => byKey[k]);
    }
}
