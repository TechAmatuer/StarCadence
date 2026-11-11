// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarCadence.Core.Model;

public sealed class Config {
    public Site Site { get; set; } = new();
    public List<FilterSpec> Filters { get; set; } = new();
    public Overheads Overheads { get; set; } = new();
    public PlannerSettings Planner { get; set; } = new();
    public SourceSettings Sources { get; set; } = new();
    public List<WatchItem> WatchList { get; set; } = new();
    public ExportSettings Export { get; set; } = new();
    /// <summary>Where this observatory's own observation history is kept (relative to the config file).</summary>
    public string HistoryFile { get; set; } = "history.json";

    public static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static Config Load(string path) {
        var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty configuration file.");
        cfg.Validate();
        return cfg;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public void Validate() {
        var errors = new List<string>();
        if (Math.Abs(Site.Latitude) > 90) errors.Add("site.latitude must be between -90 and 90");
        if (Math.Abs(Site.Longitude) > 180) errors.Add("site.longitude must be between -180 and 180 (east positive)");
        if (Filters.Count == 0) errors.Add("at least one filter is needed in filters");
        foreach (var f in Filters) {
            if (string.IsNullOrWhiteSpace(f.Name)) errors.Add("every filter needs a name (as your filter wheel calls it)");
            if (f.RefExposure <= 0) errors.Add($"filter {f.Name}: refExposure must be above 0");
            if (f.MinExposure <= 0 || f.MaxExposure < f.MinExposure) errors.Add($"filter {f.Name}: need 0 < minExposure <= maxExposure");
            if (f.Count < 1) errors.Add($"filter {f.Name}: count must be at least 1");
        }
        if (Planner.StepMinutes is <= 0 or > 30) errors.Add("planner.stepMinutes must be between 0 and 30");
        if (errors.Count > 0) throw new InvalidDataException("Configuration problems:\n- " + string.Join("\n- ", errors));
    }

    public FilterSpec? FilterByCode(string code) =>
        Filters.FirstOrDefault(f => string.Equals(f.Aavso, code, StringComparison.OrdinalIgnoreCase))
        ?? Filters.FirstOrDefault(f => string.Equals(f.Name, code, StringComparison.OrdinalIgnoreCase));
}

public sealed class Site {
    public string Name { get; set; } = "My observatory";
    public double Latitude { get; set; }
    /// <summary>Degrees, east positive (west is negative).</summary>
    public double Longitude { get; set; }
    public double ElevationM { get; set; }
    /// <summary>IANA time zone for local times in reports (e.g. "America/New_York"); UTC when empty.</summary>
    public string? TimeZone { get; set; }
    public double MinAltitude { get; set; } = 30;
    /// <summary>The Sun must be below this altitude (degrees): -12 nautical, -18 astronomical.</summary>
    public double SunAltitude { get; set; } = -12;
    /// <summary>Minimum distance from the Moon (degrees) while it is up.</summary>
    public double MoonSeparation { get; set; } = 15;
    public double MaxAirmass { get; set; } = 2.5;
    /// <summary>Optional local horizon: azimuth/altitude points (degrees), interpolated in azimuth.</summary>
    public List<HorizonPoint> Horizon { get; set; } = new();

    public double HorizonAt(double az) {
        if (Horizon.Count == 0) return 0;
        var pts = Horizon.Select(p => new HorizonPoint(Astro.AstroMath.Norm360(p.Az), p.Alt)).OrderBy(p => p.Az).ToList();
        if (pts.Count == 1) return pts[0].Alt;
        az = Astro.AstroMath.Norm360(az);
        static double Lerp(HorizonPoint a, HorizonPoint b, double x) => b.Az == a.Az ? Math.Max(a.Alt, b.Alt) : a.Alt + (b.Alt - a.Alt) * (x - a.Az) / (b.Az - a.Az);
        for (var i = 0; i < pts.Count - 1; i++)
            if (az >= pts[i].Az && az <= pts[i + 1].Az) return Lerp(pts[i], pts[i + 1], az);
        var last = pts[^1];
        var first = pts[0] with { Az = pts[0].Az + 360 };
        return Lerp(last, first, az < last.Az ? az + 360 : az);
    }
}

public sealed record HorizonPoint(double Az, double Alt);

/// <summary>
/// One filter. The exposure for a star of magnitude m is refExposure x 10^(0.4 (m - refMag)), kept between
/// minExposure and maxExposure; a star that would need more than maxExposure gets more frames instead.
/// </summary>
public sealed class FilterSpec {
    /// <summary>The name your filter wheel uses.</summary>
    public string Name { get; set; } = "V";
    /// <summary>AAVSO band code (B, V, R, I, SG, SR, CV, ...).</summary>
    public string Aavso { get; set; } = "V";
    public double RefMag { get; set; } = 12;
    /// <summary>Seconds that give a good, unsaturated measurement of a refMag star.</summary>
    public double RefExposure { get; set; } = 30;
    public double MinExposure { get; set; } = 1;
    public double MaxExposure { get; set; } = 300;
    public int Count { get; set; } = 1;
    /// <summary>Most frames per visit when a faint star needs stacking.</summary>
    public int MaxCount { get; set; } = 10;
    /// <summary>Typical colour offset from V (mag in this band = V + offset) when a star has no magnitude in this band.</summary>
    public double ColourOffset { get; set; }
    /// <summary>How many magnitudes brighter than the planned magnitude a star can be before saturating at the planned exposure.</summary>
    public double SaturationHeadroom { get; set; } = 1.0;
}

public sealed class Overheads {
    /// <summary>Slew, plate-solve/centre and settle per new target (seconds).</summary>
    public double SlewAndCenterS { get; set; } = 90;
    public double FilterChangeS { get; set; } = 8;
    public double ReadoutS { get; set; } = 3;
}

public sealed class PlannerSettings {
    public double StepMinutes { get; set; } = 2;
    public int MaxTargets { get; set; } = 300;
    /// <summary>A target is due again once this fraction of its cadence has passed since your last visit.</summary>
    public double DueFraction { get; set; } = 0.8;
    /// <summary>Cadence used when a source gives none (days).</summary>
    public double DefaultCadenceDays { get; set; } = 1;
    public double DefaultSeriesMinutes { get; set; } = 60;
    /// <summary>Minutes before ingress and after egress of a transit (out-of-transit baseline).</summary>
    public double TransitBaselineMinutes { get; set; } = 60;
    /// <summary>Filter (AAVSO code) for transits when the source does not say.</summary>
    public string TransitFilter { get; set; } = "R";
    /// <summary>Minutes either side of a periodic event when the source does not say.</summary>
    public double EventHalfWidthMinutes { get; set; } = 60;
    /// <summary>Shorten exposures so a star's catalogue bright limit cannot saturate (safer for flaring stars, less
    /// signal in their faint state). Off by default: exposures suit the planned magnitude, with a warning.</summary>
    public bool CapForBrightLimit { get; set; }
    /// <summary>Scoring weights, by rule name (see Planning/Scoring.cs). Missing names use the defaults.</summary>
    public Dictionary<string, double> RuleWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SourceSettings {
    /// <summary>Environment variable that holds your AAVSO Target Tool API key (the key itself is never stored here).</summary>
    public string TargetToolKeyVariable { get; set; } = "AAVSO_TARGETTOOL_KEY";
    /// <summary>Or a file containing only the key (relative to the config file).</summary>
    public string? TargetToolKeyFile { get; set; }
    /// <summary>Target Tool sections: ac (alerts/campaigns), ep, cv, eb, spp, lpv, yso, het, misc.</summary>
    public List<string> TargetToolSections { get; set; } = new() { "ac" };
    /// <summary>A CSV exported from the Target Tool (used when there is no key, or when offline).</summary>
    public string? TargetToolCsv { get; set; }
    /// <summary>More target lists: any CSV with Name/RA/Dec columns (ExoPlanets plug-in formats included).</summary>
    public List<string> CsvFiles { get; set; } = new();
    public bool ExoClock { get; set; }
    /// <summary>A saved copy of https://www.exoclock.space/database/planets_json (used offline or instead of fetching).</summary>
    public string? ExoClockFile { get; set; }
    public List<string> ExoClockPriorities { get; set; } = new() { "alert", "high", "medium" };
    public double ExoClockMaxMag { get; set; } = 13.5;
    public double ExoClockMinDepthMmag { get; set; } = 5;
    /// <summary>Folder for downloaded source data (relative to the config file).</summary>
    public string CacheDir { get; set; } = "cache";
}

/// <summary>A star you always want covered.</summary>
public sealed class WatchItem {
    public string Name { get; set; } = "";
    public string? Ra { get; set; }
    public string? Dec { get; set; }
    public double? Mag { get; set; }
    public double? MagBright { get; set; }
    public double? CadenceDays { get; set; }
    public double? Priority { get; set; }
    public List<string>? Filters { get; set; }
    public double? SeriesMinutes { get; set; }
    /// <summary>Optional windows as ISO times, e.g. "2026-11-12T02:00:00Z/2026-11-12T04:30:00Z" (prefix "fixed:" to require all of it).</summary>
    public List<string>? Windows { get; set; }
    public string? Until { get; set; }
    public string? Notes { get; set; }
    /// <summary>For eclipsing binaries and other periodic stars: epoch (HJD) and period (days) of the event to catch.</summary>
    public double? Epoch { get; set; }
    public double? Period { get; set; }
    public double? Phase { get; set; }
    public double? EventMinutes { get; set; }
}

public sealed class ExportSettings {
    /// <summary>Where the Ekos sequence files will sit on the observatory computer (absolute path), for the .esl file.</summary>
    public string? EkosSequenceDir { get; set; }
    public string? EkosImageDir { get; set; }
    public string EkosProfile { get; set; } = "Default";
    /// <summary>Give every Ekos job a fixed start time, not only transits and minima (default: others start ASAP in plan order).</summary>
    public bool EkosStartAllAt { get; set; }
    /// <summary>Ekos steps per job: Track, Focus, Align, Guide.</summary>
    public List<string> EkosSteps { get; set; } = new() { "Track", "Align" };
    /// <summary>N.I.N.A. Target Scheduler import types by longest exposure: seconds -> type name.</summary>
    public List<ExposureClass> NinaClasses { get; set; } = new() {
        new(15, "VS short"), new(60, "VS medium"), new(100000, "VS long")
    };
}

public sealed record ExposureClass(double UpToSeconds, string Type);
