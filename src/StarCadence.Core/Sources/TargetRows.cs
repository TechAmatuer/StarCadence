// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// Column names accepted for target lists. The aliases combine the ExoPlanets plug-in's UnifiedVariableStarCsvParser
// (github.com/NickHardy/ExoPlanets, MPL-2.0: Manual, AAVSO and flexible formats, matched by header name, any order)
// and Space Lab's AAVSO Target Tool reader (API field names and exported-CSV headers).

using System.Globalization;
using System.Text.RegularExpressions;
using StarCadence.Core.Astro;
using StarCadence.Core.Model;

namespace StarCadence.Core.Sources;

public static partial class TargetRows {
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static readonly Dictionary<string, string[]> Aliases = new() {
        ["name"] = new[] { "name", "star name", "star_name", "starname", "star", "object", "target" },
        ["ra"] = new[] { "ra", "ra (j2000.0)", "ra_j2000", "ra_2000", "ra(2000)", "ra j2000" },
        ["rahours"] = new[] { "ra_h", "ra (h)", "ra_hours", "ra hours", "ra (hours)" },
        ["dec"] = new[] { "dec", "dec (j2000.0)", "dec_j2000", "dec_2000", "declination", "dec(2000)", "dec j2000" },
        ["mag"] = new[] { "magnitude", "v", "mag", "vmag", "v_mag" },
        ["maxmag"] = new[] { "max mag", "max_mag", "maxmag", "maximum_magnitude" },
        ["minmag"] = new[] { "min mag", "min_mag", "minmag", "minimum_magnitude" },
        ["period"] = new[] { "period", "period (d)", "p", "orbital_period", "period_d" },
        ["epoch"] = new[] { "epoch", "epoch (jd)", "jd", "epoch_jd", "primary_minimum" },
        ["amplitude"] = new[] { "amplitude", "amp", "range", "mag_range" },
        ["type"] = new[] { "type", "var type", "var. type", "vartype", "var_type", "classification", "variability type" },
        ["comments"] = new[] { "comments", "notes", "remarks", "other_info", "other info" },
        ["ocrange"] = new[] { "ocrange", "o-c range", "oc_range", "o-c", "drift" },
        ["phase"] = new[] { "phase", "obsphase", "obs_phase", "start_phase", "observed_phase" },
        ["filter"] = new[] { "filter", "filters", "filter/mode", "observation_filter" },
        ["mode"] = new[] { "obs_mode", "obs mode", "mode", "observing mode" },
        ["cadence"] = new[] { "obs_cadence", "obs cadence", "obs cadence (d)", "cadence", "cadence_days", "observing cadence (d)" },
        ["sections"] = new[] { "obs_section", "obs section", "observing section", "section", "sections" },
        ["last"] = new[] { "last_data_point", "last data point", "last observation", "last_obs" },
        ["priority"] = new[] { "priority" },
        ["series"] = new[] { "series_min", "series minutes", "time series (min)" },
        ["until"] = new[] { "until", "end", "end date", "active until" },
        ["recent"] = new[] { "recent_obs", "observations recent", "obs_recent", "total_observations_recent" },
    };

    /// <summary>Finds which header each field uses (case-insensitive, punctuation-tolerant).</summary>
    public static Dictionary<string, string> MapHeaders(IEnumerable<string> headers) {
        var lookup = new Dictionary<string, string>();
        foreach (var h in headers) {
            var k = Norm(h);
            if (!lookup.ContainsKey(k)) lookup[k] = h;
        }
        var map = new Dictionary<string, string>();
        foreach (var (field, names) in Aliases)
            foreach (var n in names)
                if (lookup.TryGetValue(Norm(n), out var h)) { map[field] = h; break; }
        return map;
    }

    static string Norm(string s) => NonAlnum().Replace(s.Trim().ToLowerInvariant(), "");

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"time[ -]?series", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesRx();

    /// <summary>One row (from a CSV or a JSON record) to a target, or null with a reason.</summary>
    public static Target? FromRow(IReadOnlyDictionary<string, string> row, Dictionary<string, string> map, string source, out string? problem) {
        string? Get(string f) => map.TryGetValue(f, out var h) && row.TryGetValue(h, out var v) && !string.IsNullOrWhiteSpace(v) && v != "None" && v != "null" ? v.Trim() : null;
        problem = null;
        var name = Get("name");
        if (name is null) { problem = "no name"; return null; }
        var raText = Get("ra"); var decText = Get("dec");
        var ra = Coords.ParseRa(raText);
        if (Get("rahours") is string rh) { raText = rh; ra = Coords.ParseRa(rh, plainIsHours: true); }
        var dec = Coords.ParseDec(decText);
        if (ra is null || dec is null) { problem = $"{name}: unreadable RA/Dec ('{raText}', '{decText}')"; return null; }

        var t = new Target { Name = name, RaDeg = ra.Value, DecDeg = dec.Value, Source = source };
        var mag = Num(Get("mag")); var max = Num(Get("maxmag")); var min = Num(Get("minmag"));
        var amp = Num(Get("amplitude"));
        // ExoPlanets convention: "v" is the bright magnitude and amplitude the depth of the event.
        t.Mag = mag; t.MagBright = max ?? mag; t.MagFaint = min ?? (mag is not null && amp is not null ? mag + Math.Abs(amp.Value) : null);
        if (t.Mag is null && t.MagBright is not null && t.MagFaint is not null) t.Mag = (t.MagBright + t.MagFaint) / 2;
        t.VarType = Get("type");
        t.PeriodDays = Num(Get("period"));
        t.EpochJd = Num(Get("epoch"));
        if (t.EpochJd is > 0 and < 100000) t.EpochJd += 2400000; // reduced JD (e.g. 59000.123)
        t.EventPhase = Num(Get("phase")) ?? 0;
        t.OcRangeMin = Num(Get("ocrange")) ?? 0;
        t.CadenceDays = Num(Get("cadence"));
        t.LastObservedUtc = When(Get("last"));
        t.RecentObservations = (int?)Num(Get("recent"));
        t.Notes = Get("comments") ?? "";
        t.SeriesMinutes = Num(Get("series"));
        t.ActiveUntilUtc = When(Get("until"));

        foreach (var f in Split(Get("filter"))) if (IsFilterCode(f)) t.WantFilters.Add(f.ToUpperInvariant());
        t.Sections.AddRange(Split(Get("sections")));
        t.AlertOrCampaign = t.Sections.Any(s => s.Contains("alert", StringComparison.OrdinalIgnoreCase) || s.Contains("campaign", StringComparison.OrdinalIgnoreCase) || s.Equals("ac", StringComparison.OrdinalIgnoreCase));
        var pri = Get("priority");
        t.Priority = pri is null ? 0.5 : pri.ToLowerInvariant() switch {
            "true" or "yes" or "1" or "high" => 1.0,
            "false" or "no" or "0" => 0.5,
            "alert" => 1.0, "medium" => 0.6, "low" => 0.3,
            _ => double.TryParse(pri, NumberStyles.Float, Inv, out var p) ? Math.Clamp(p > 1 ? p / 10 : p, 0, 1) : 0.5
        };
        var mode = Get("mode") ?? "";
        if (SeriesRx().IsMatch(mode + " " + t.Notes) || t.SeriesMinutes is > 0) t.Kind = TargetKind.Series;
        if (t.EpochJd is > 0 && t.PeriodDays is > 0) t.Kind = TargetKind.Event; // the ExoPlanets plug-in's event stars
        return t;
    }

    public static bool IsFilterCode(string s) => s.Length is > 0 and <= 4 && s.All(char.IsLetterOrDigit) && !s.Equals("vis", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> Split(string? s) =>
        (s ?? "").Split(new[] { ',', ';', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The leading number of a field ("12.3", "<14.8", "7.5 V", "0.5073:"), or null.</summary>
    public static double? Num(string? s) {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = LeadingNum().Match(s.Trim().TrimStart('<', '>', '~', '(', '['));
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, Inv, out var v) && double.IsFinite(v) ? v : null;
    }

    [GeneratedRegex(@"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?")]
    private static partial Regex LeadingNum();

    /// <summary>A time from ISO text, "yyyy-MM-dd hh:mm:ss", a Julian date or Unix seconds; UTC.</summary>
    public static DateTime? When(string? s) {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s, NumberStyles.Float, Inv, out var v)) {
            if (v > 2400000 && v < 2600000) return AstroTime.FromJulianDate(v);
            if (v > 1e8 && v < 1e10) return DateTime.UnixEpoch.AddSeconds(v);
            return null;
        }
        return DateTime.TryParse(s, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
    }
}
