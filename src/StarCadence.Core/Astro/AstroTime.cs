// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

namespace StarCadence.Core.Astro;

/// <summary>Time scales and sidereal time (Meeus, Astronomical Algorithms, 2nd ed., ch. 7 and 12).</summary>
public static class AstroTime {
    /// <summary>TT - UTC in seconds (32.184 s + 37 leap seconds since 2017). TDB differs from TT by under 2 ms.</summary>
    public const double TtMinusUtcSeconds = 69.184;

    public const double J2000 = 2451545.0;

    public static double JulianDate(DateTime utc) {
        var u = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
        return u.Ticks / (double)TimeSpan.TicksPerDay + 1721425.5;
    }

    public static DateTime FromJulianDate(double jd) {
        var ticks = (long)Math.Round((jd - 1721425.5) * TimeSpan.TicksPerDay);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>Julian centuries of TT since J2000 for a UTC instant.</summary>
    public static double CenturiesTT(double jdUtc) => (jdUtc + TtMinusUtcSeconds / 86400.0 - J2000) / 36525.0;

    /// <summary>Greenwich mean sidereal time in degrees (Meeus 12.4).</summary>
    public static double GmstDegrees(double jdUt) {
        var t = (jdUt - J2000) / 36525.0;
        var theta = 280.46061837 + 360.98564736629 * (jdUt - J2000) + 0.000387933 * t * t - t * t * t / 38710000.0;
        return AstroMath.Norm360(theta);
    }

    /// <summary>Local mean sidereal time in degrees; longitude east positive.</summary>
    public static double LmstDegrees(double jdUt, double longitudeEast) => AstroMath.Norm360(GmstDegrees(jdUt) + longitudeEast);

    public static DateTime AsUtc(DateTime t) => t.Kind switch {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc)
    };
}

public static class AstroMath {
    public const double Deg = Math.PI / 180.0;

    public static double Norm360(double d) {
        d %= 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    public static double Norm180(double d) {
        d = Norm360(d);
        return d > 180 ? d - 360 : d;
    }

    public static double SinD(double d) => Math.Sin(d * Deg);
    public static double CosD(double d) => Math.Cos(d * Deg);
    public static double TanD(double d) => Math.Tan(d * Deg);
    public static double AsinD(double x) => Math.Asin(Math.Clamp(x, -1, 1)) / Deg;
    public static double Atan2D(double y, double x) => Math.Atan2(y, x) / Deg;

    /// <summary>Great-circle separation in degrees between two (RA, Dec) positions given in degrees.</summary>
    public static double Separation(double ra1, double dec1, double ra2, double dec2) {
        // Vincenty form: stable for small and large angles.
        var dra = (ra2 - ra1) * Deg;
        double s1 = Math.Sin(dec1 * Deg), c1 = Math.Cos(dec1 * Deg), s2 = Math.Sin(dec2 * Deg), c2 = Math.Cos(dec2 * Deg);
        var num = Math.Sqrt(Math.Pow(c2 * Math.Sin(dra), 2) + Math.Pow(c1 * s2 - s1 * c2 * Math.Cos(dra), 2));
        var den = s1 * s2 + c1 * c2 * Math.Cos(dra);
        return Math.Atan2(num, den) / Deg;
    }
}
