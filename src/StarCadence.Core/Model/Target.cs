// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Text.Json.Serialization;

namespace StarCadence.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetKind {
    /// <summary>One set of exposures through the filters, repeated on a cadence.</summary>
    Variable,
    /// <summary>A time series: the filter set repeated for a set number of minutes.</summary>
    Series,
    /// <summary>An exoplanet transit: a fixed window around ingress and egress.</summary>
    Transit,
    /// <summary>A predicted event of a periodic star (eclipse minimum, maximum, ...): a fixed window.</summary>
    Event,
    /// <summary>A standard field for transformation coefficients.</summary>
    Standard
}

/// <summary>A span of time. A fixed window must be covered from start to end (transits, eclipse minima);
/// a non-fixed window only limits when the target may be observed.</summary>
public sealed record TimeWindow(DateTime StartUtc, DateTime EndUtc, bool Fixed = false, string? Label = null) {
    public TimeSpan Length => EndUtc - StartUtc;
    public bool Contains(DateTime start, DateTime end) => start >= StartUtc && end <= EndUtc;
    public bool Overlaps(DateTime start, DateTime end) => start < EndUtc && end > StartUtc;
}

public sealed class Target {
    public string Name { get; set; } = "";
    public double RaDeg { get; set; }
    public double DecDeg { get; set; }
    public TargetKind Kind { get; set; } = TargetKind.Variable;

    /// <summary>Magnitude to plan exposures for (latest or typical), V unless a band magnitude is given.</summary>
    public double? Mag { get; set; }
    /// <summary>Brightest magnitude the star reaches (catalogue "max"); exposures are capped so it does not saturate.</summary>
    public double? MagBright { get; set; }
    /// <summary>Faintest magnitude (catalogue "min").</summary>
    public double? MagFaint { get; set; }
    /// <summary>Magnitudes by band (AAVSO codes: B, V, R, I, ...), when known.</summary>
    public Dictionary<string, double> BandMags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? VarType { get; set; }
    public double? PeriodDays { get; set; }

    /// <summary>0 (low) to 1 (high).</summary>
    public double Priority { get; set; } = 0.5;
    public bool AlertOrCampaign { get; set; }
    public bool Urgent { get; set; }

    /// <summary>Wanted days between observations; below 1 means several visits a night.</summary>
    public double? CadenceDays { get; set; }
    /// <summary>Last observation by anyone (e.g. AAVSO's last data point).</summary>
    public DateTime? LastObservedUtc { get; set; }
    /// <summary>How many observations it got recently (anyone), when the source says; few means it needs coverage.</summary>
    public int? RecentObservations { get; set; }

    /// <summary>Filters asked for (AAVSO codes); empty = every filter in the configuration.</summary>
    public List<string> WantFilters { get; set; } = new();
    public double? SeriesMinutes { get; set; }

    public List<TimeWindow> Windows { get; set; } = new();
    public DateTime? ActiveFromUtc { get; set; }
    public DateTime? ActiveUntilUtc { get; set; }

    /// <summary>For periodic events: epoch of the reference event (HJD/BJD), phase of the event to observe,
    /// minutes either side of it, and the O-C uncertainty in minutes (from the ExoPlanets plug-in's event model).</summary>
    public double? EpochJd { get; set; }
    public double EventPhase { get; set; }
    public double? EventHalfWidthMin { get; set; }
    public double OcRangeMin { get; set; }

    public double? DepthMmag { get; set; }
    public double? DurationHours { get; set; }

    public string Source { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Sections { get; set; } = new();

    public string Key => NormaliseName(Name);

    public static string NormaliseName(string name) =>
        string.Join(' ', name.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The magnitude to plan with in one band: a measured band magnitude, else Mag plus the filter's colour offset.</summary>
    public double? PlanMag(string band, double colourOffset) {
        if (BandMags.TryGetValue(band, out var m)) return m;
        var basis = Mag ?? MidRange();
        return basis is null ? null : basis + colourOffset;
    }

    public double? MidRange() =>
        MagBright is not null && MagFaint is not null ? (MagBright + MagFaint) / 2 : MagBright ?? MagFaint;

    public Target Clone() {
        var t = (Target)MemberwiseClone();
        t.BandMags = new Dictionary<string, double>(BandMags, StringComparer.OrdinalIgnoreCase);
        t.WantFilters = new List<string>(WantFilters);
        t.Windows = new List<TimeWindow>(Windows);
        t.Sections = new List<string>(Sections);
        return t;
    }
}
