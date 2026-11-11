// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using StarCadence.Core.Model;

namespace StarCadence.Core.Planning;

public sealed class PlanBlock {
    public string Target { get; set; } = "";
    public TargetKind Kind { get; set; }
    public double RaDeg { get; set; }
    public double DecDeg { get; set; }
    /// <summary>Block start, including the slew and centring.</summary>
    public DateTime StartUtc { get; set; }
    /// <summary>First exposure starts here.</summary>
    public DateTime ExposeFromUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public bool Fixed { get; set; }
    public string? Window { get; set; }
    public List<ExposureStep> Steps { get; set; } = new();
    public double Score { get; set; }
    public List<RuleResult> Why { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public double AltStart { get; set; }
    public double AltEnd { get; set; }
    /// <summary>1 for the first visit tonight, 2 for the second (short cadences), ...</summary>
    public int Visit { get; set; } = 1;
    public string Source { get; set; } = "";

    public double MinutesLong => (EndUtc - StartUtc).TotalMinutes;
}

public sealed record Skipped(string Target, string Reason);

public sealed class Plan {
    public string Site { get; set; } = "";
    public DateOnly NightOf { get; set; }
    public DateTime NightStartUtc { get; set; }
    public DateTime NightEndUtc { get; set; }
    public DateTime PlannedFromUtc { get; set; }
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public string? TimeZone { get; set; }
    public double MoonIllumination { get; set; }
    public List<PlanBlock> Blocks { get; set; } = new();
    public List<Skipped> Skipped { get; set; } = new();
    public int Candidates { get; set; }

    /// <summary>Share of the dark time spent on planned blocks.</summary>
    public double UsedFraction {
        get {
            var dark = (NightEndUtc - PlannedFromUtc).TotalMinutes;
            return dark <= 0 ? 0 : Blocks.Sum(b => b.MinutesLong) / dark;
        }
    }

    public string Local(DateTime utc, string format = "HH:mm") => TimeZones.ToLocal(utc, TimeZone).ToString(format, System.Globalization.CultureInfo.InvariantCulture);
}

public static class TimeZones {
    /// <summary>Converts UTC to the named zone. Accepts IANA ids (America/New_York) on Windows, macOS and Linux;
    /// falls back to UTC when the zone is unknown.</summary>
    public static DateTime ToLocal(DateTime utc, string? zone) {
        var tz = Find(zone);
        return tz is null ? utc : TimeZoneInfo.ConvertTimeFromUtc(Astro.AstroTime.AsUtc(utc), tz);
    }

    public static TimeZoneInfo? Find(string? zone) {
        if (string.IsNullOrWhiteSpace(zone)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(zone); } catch (Exception) { }
        try {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(zone, out var win)) return TimeZoneInfo.FindSystemTimeZoneById(win);
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var iana)) return TimeZoneInfo.FindSystemTimeZoneById(iana);
        } catch (Exception) { }
        return null;
    }
}
