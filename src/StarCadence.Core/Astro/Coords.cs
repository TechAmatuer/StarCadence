// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.RegularExpressions;
using static StarCadence.Core.Astro.AstroMath;

namespace StarCadence.Core.Astro;

/// <summary>Coordinate parsing, precession and horizon coordinates.</summary>
public static partial class Coords {
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Right ascension in degrees. Sexagesimal text ("05 34 31.9", "05:34:31.9", "5h34m31.9s") is read as hours;
    /// a plain number is read as degrees unless <paramref name="plainIsHours"/> is set.
    /// </summary>
    public static double? ParseRa(string? text, bool plainIsHours = false) {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (IsSexagesimal(s)) {
            var v = ParseSexagesimal(s);
            return v is null ? null : Norm360(v.Value * 15.0);
        }
        if (!double.TryParse(s, NumberStyles.Float, Inv, out var d)) return null;
        return Norm360(plainIsHours ? d * 15.0 : d);
    }

    /// <summary>Declination in degrees from "+22 00 52.1", "-05:23:28", "22d00m52s" or a plain number.</summary>
    public static double? ParseDec(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        double? v = IsSexagesimal(s) ? ParseSexagesimal(s)
            : double.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : null;
        return v is null || Math.Abs(v.Value) > 90 ? null : v;
    }

    static bool IsSexagesimal(string s) => SexRx().IsMatch(s);

    [GeneratedRegex(@"[:hdm°'\s]", RegexOptions.IgnoreCase)]
    private static partial Regex SexRx();

    [GeneratedRegex(@"[-+]?\d+(?:\.\d+)?")]
    private static partial Regex NumRx();

    static double? ParseSexagesimal(string s) {
        var neg = s.TrimStart().StartsWith('-') || s.TrimStart().StartsWith('−');
        var parts = NumRx().Matches(s.Replace('−', '-')).Select(m => m.Value.TrimStart('+', '-')).ToList();
        if (parts.Count == 0) return null;
        double total = 0, scale = 1;
        foreach (var p in parts.Take(3)) {
            if (!double.TryParse(p, NumberStyles.Float, Inv, out var x)) return null;
            total += x / scale;
            scale *= 60;
        }
        return neg ? -total : total;
    }

    public static string FormatRa(double raDeg, int decimals = 1) {
        var h = Norm360(raDeg) / 15.0;
        var hh = (int)h; var mFull = (h - hh) * 60; var mm = (int)mFull; var ss = (mFull - mm) * 60;
        if (Math.Round(ss, decimals) >= 60) { ss = 0; mm++; }
        if (mm >= 60) { mm = 0; hh = (hh + 1) % 24; }
        return string.Format(Inv, "{0:00}:{1:00}:{2:" + (decimals > 0 ? "00." + new string('0', decimals) : "00") + "}", hh, mm, ss);
    }

    public static string FormatDec(double decDeg, int decimals = 0) {
        var sign = decDeg < 0 ? "-" : "+";
        var a = Math.Abs(decDeg);
        var dd = (int)a; var mFull = (a - dd) * 60; var mm = (int)mFull; var ss = (mFull - mm) * 60;
        if (Math.Round(ss, decimals) >= 60) { ss = 0; mm++; }
        if (mm >= 60) { mm = 0; dd++; }
        return sign + string.Format(Inv, "{0:00}:{1:00}:{2:" + (decimals > 0 ? "00." + new string('0', decimals) : "00") + "}", dd, mm, ss);
    }

    /// <summary>Precess J2000 coordinates to the equinox of the given date (Meeus 21.2-21.4).</summary>
    public static (double ra, double dec) PrecessFromJ2000(double raDeg, double decDeg, double jd) {
        var t = (jd - AstroTime.J2000) / 36525.0;
        var zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) / 3600.0;
        var z = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) / 3600.0;
        var theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) / 3600.0;
        var a = CosD(decDeg) * SinD(raDeg + zeta);
        var b = CosD(theta) * CosD(decDeg) * CosD(raDeg + zeta) - SinD(theta) * SinD(decDeg);
        var c = SinD(theta) * CosD(decDeg) * CosD(raDeg + zeta) + CosD(theta) * SinD(decDeg);
        var ra = Norm360(Atan2D(a, b) + z);
        var dec = Math.Abs(decDeg) > 85 ? Math.Acos(Math.Clamp(Math.Sqrt(a * a + b * b), -1, 1)) / Deg * Math.Sign(c) : AsinD(c);
        return (ra, dec);
    }

    /// <summary>Altitude and azimuth (degrees, azimuth from north through east) for coordinates of date.</summary>
    public static (double alt, double az) Horizontal(double raDeg, double decDeg, double latDeg, double lstDeg) {
        var h = lstDeg - raDeg;
        var sinAlt = SinD(latDeg) * SinD(decDeg) + CosD(latDeg) * CosD(decDeg) * CosD(h);
        var alt = AsinD(sinAlt);
        var y = -CosD(decDeg) * SinD(h);
        var x = SinD(decDeg) * CosD(latDeg) - CosD(decDeg) * SinD(latDeg) * CosD(h);
        var az = Norm360(Atan2D(y, x));
        return (alt, az);
    }

    /// <summary>Plane-parallel airmass with the Young (1994) correction; large number near or below the horizon.</summary>
    public static double Airmass(double altDeg) {
        if (altDeg <= 0.5) return 99;
        var z = (90 - altDeg) * Deg;
        var c = Math.Cos(z);
        return (1.002432 * c * c + 0.148386 * c + 0.0096467) / (c * c * c + 0.149864 * c * c + 0.0102963 * c + 0.000303978);
    }

    public static double AltitudeForAirmass(double airmass) {
        if (airmass <= 1) return 90;
        double lo = 0.5, hi = 90;
        for (var i = 0; i < 50; i++) {
            var mid = (lo + hi) / 2;
            if (Airmass(mid) > airmass) lo = mid; else hi = mid;
        }
        return hi;
    }
}
