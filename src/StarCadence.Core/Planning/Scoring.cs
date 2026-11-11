// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// The rule-and-weight design follows Target Scheduler's scoring engine (Tom Palmer, MPL-2.0,
// github.com/tcpalmer/nina.plugin.targetscheduler, Planning/Scoring): every rule returns 0..1, the total is the
// weighted sum, and each rule's part is kept so the plan can say why a target won. "Setting soonest" and the
// target-switch penalty (here "Slew") are adapted from its rules; the cadence and coverage rules are new.

using StarCadence.Core.Astro;
using StarCadence.Core.Model;

namespace StarCadence.Core.Planning;

public sealed record RuleResult(string Rule, double Weight, double Score) {
    public double Part => Weight * Score;
}

/// <summary>What a rule can look at when scoring one candidate at one moment.</summary>
public sealed record ScoreContext(Target Target, DateTime AtUtc, Night Night, Night.Track Track, Target? Previous, DateTime? LastOwnUtc, double VisitMinutes = 0);

public interface IScoringRule {
    string Name { get; }
    double DefaultWeight { get; }
    double Score(ScoreContext c);
}

public static class Rules {
    public sealed class Priority : IScoringRule {
        public string Name => "Priority";
        public double DefaultWeight => 0.5;
        public double Score(ScoreContext c) => Math.Clamp(c.Target.Priority, 0, 1);
    }

    /// <summary>How overdue the star is for its cadence, from the last observation by anyone (up to 6 cadences).</summary>
    public sealed class Cadence : IScoringRule {
        public string Name => "Cadence";
        public double DefaultWeight => 1.0;
        public double Score(ScoreContext c) {
            var cad = c.Target.CadenceDays;
            if (cad is null or <= 0) return 0.2;
            var last = Latest(c.Target.LastObservedUtc, c.LastOwnUtc);
            if (last is null) return 0.4;
            var overdue = (c.AtUtc - last.Value).TotalDays / cad.Value;
            return Math.Clamp(overdue, 0, 6) / 6;
        }
    }

    /// <summary>Few recent observations by anyone = more useful data (the "low cadence / low coverage" targets).</summary>
    public sealed class LowCoverage : IScoringRule {
        public string Name => "LowCoverage";
        public double DefaultWeight => 0.5;
        public double Score(ScoreContext c) => c.Target.RecentObservations is int n ? 1.0 / (1.0 + n / 3.0) : 0.3;
    }

    public sealed class Alert : IScoringRule {
        public string Name => "Alert";
        public double DefaultWeight => 0.8;
        public double Score(ScoreContext c) => c.Target.Urgent ? 1 : c.Target.AlertOrCampaign ? 0.7 : 0;
    }

    /// <summary>From Target Scheduler: prefer targets that will be lost soonest tonight.</summary>
    public sealed class SettingSoonest : IScoringRule {
        public string Name => "SettingSoonest";
        public double DefaultWeight => 0.5;
        public double Score(ScoreContext c) {
            var until = c.Track.VisibleUntil(c.AtUtc);
            var night = (c.Night.EndUtc - c.Night.StartUtc).TotalMinutes;
            return night <= 0 ? 0 : 1 - Math.Clamp((until - c.AtUtc).TotalMinutes / night, 0, 1);
        }
    }

    /// <summary>A campaign or window that is about to close.</summary>
    public sealed class Deadline : IScoringRule {
        public string Name => "Deadline";
        public double DefaultWeight => 0.4;
        public double Score(ScoreContext c) {
            if (c.Target.ActiveUntilUtc is not DateTime until) return 0;
            var days = (until - c.AtUtc).TotalDays;
            return days <= 0 ? 1 : Math.Clamp(1 - days / 7.0, 0, 1);
        }
    }

    /// <summary>From Target Scheduler's target-switch penalty: short slews keep more time for photons.</summary>
    public sealed class Slew : IScoringRule {
        public string Name => "Slew";
        public double DefaultWeight => 0.3;
        public double Score(ScoreContext c) =>
            c.Previous is null ? 1 : 1 - AstroMath.Separation(c.Previous.RaDeg, c.Previous.DecDeg, c.Target.RaDeg, c.Target.DecDeg) / 180.0;
    }

    /// <summary>Higher is better: less extinction and scintillation.</summary>
    public sealed class Altitude : IScoringRule {
        public string Name => "Altitude";
        public double DefaultWeight => 0.2;
        public double Score(ScoreContext c) {
            var min = c.Night.Site.MinAltitude;
            return Math.Clamp((c.Track.AltAt(c.AtUtc) - min) / Math.Max(1, 90 - min), 0, 1);
        }
    }

    /// <summary>Shorter visits leave room for more stars: 1 for a visit of a few minutes, 0 for an hour or more.
    /// Not applied to fixed windows (transits, minima), whose length is set by the event.</summary>
    public sealed class Efficiency : IScoringRule {
        public string Name => "Efficiency";
        public double DefaultWeight => 0.3;
        public double Score(ScoreContext c) => c.VisitMinutes <= 0 ? 0.5 : 1 - Math.Clamp((c.VisitMinutes - 3) / 57.0, 0, 1);
    }

    public static DateTime? Latest(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : (a > b ? a : b);

    public static IReadOnlyList<IScoringRule> All { get; } = new IScoringRule[] {
        new Priority(), new Cadence(), new LowCoverage(), new Alert(), new SettingSoonest(), new Deadline(), new Slew(), new Altitude(), new Efficiency()
    };
}

public sealed class ScoringEngine {
    readonly Dictionary<string, double> _weights;
    public IReadOnlyList<IScoringRule> RuleSet { get; }

    public ScoringEngine(IDictionary<string, double>? weights = null, IReadOnlyList<IScoringRule>? rules = null) {
        RuleSet = rules ?? Rules.All;
        _weights = RuleSet.ToDictionary(r => r.Name, r => r.DefaultWeight, StringComparer.OrdinalIgnoreCase);
        if (weights is not null) foreach (var (k, v) in weights) if (_weights.ContainsKey(k)) _weights[k] = Math.Max(0, v);
    }

    public double Weight(string rule) => _weights.TryGetValue(rule, out var w) ? w : 0;

    public (double total, List<RuleResult> parts) Score(ScoreContext c) {
        var parts = new List<RuleResult>(RuleSet.Count);
        double total = 0;
        foreach (var r in RuleSet) {
            var w = _weights[r.Name];
            if (w == 0) continue;
            var s = Math.Clamp(r.Score(c), 0, 1);
            parts.Add(new RuleResult(r.Name, w, s));
            total += w * s;
        }
        return (total, parts);
    }
}
