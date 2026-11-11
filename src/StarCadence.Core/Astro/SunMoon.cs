// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using static StarCadence.Core.Astro.AstroMath;

namespace StarCadence.Core.Astro;

/// <summary>Apparent geocentric position of the Sun (Meeus ch. 25, low accuracy: about 0.01 degree).</summary>
public static class Sun {
    public static (double ra, double dec, double distanceAu) Position(double jdUtc) {
        var t = AstroTime.CenturiesTT(jdUtc);
        var l0 = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
        var m = 357.52911 + 35999.05029 * t - 0.0001537 * t * t;
        var e = 0.016708634 - 0.000042037 * t - 0.0000001267 * t * t;
        var c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * SinD(m) + (0.019993 - 0.000101 * t) * SinD(2 * m) + 0.000289 * SinD(3 * m);
        var trueLong = l0 + c;
        var nu = m + c;
        var r = 1.000001018 * (1 - e * e) / (1 + e * CosD(nu));
        var omega = 125.04 - 1934.136 * t;
        var lambda = trueLong - 0.00569 - 0.00478 * SinD(omega);
        var eps = 23.439291 - 0.0130042 * t + 0.00256 * CosD(omega);
        var ra = Norm360(Atan2D(CosD(eps) * SinD(lambda), CosD(lambda)));
        var dec = AsinD(SinD(eps) * SinD(lambda));
        return (ra, dec, r);
    }

    public static double Altitude(double jdUtc, double lat, double lonEast) {
        var (ra, dec, _) = Position(jdUtc);
        return Coords.Horizontal(ra, dec, lat, AstroTime.LmstDegrees(jdUtc, lonEast)).alt;
    }

    /// <summary>
    /// Light-time difference (days) between arrival at the Sun's centre and at the Earth for a star at (ra, dec):
    /// HJD = JD + this value. Within a few seconds of the barycentric correction, which is enough for scheduling.
    /// </summary>
    public static double HeliocentricCorrectionDays(double jdUtc, double raDeg, double decDeg) {
        var (sra, sdec, r) = Position(jdUtc);
        var cosAngle = SinD(decDeg) * SinD(sdec) + CosD(decDeg) * CosD(sdec) * CosD(raDeg - sra);
        return -r * 499.004784 / 86400.0 * cosAngle;
    }

    /// <summary>Convert a BJD_TDB (or HJD) event time to a UTC Julian date for a star at (ra, dec).</summary>
    public static double BjdTdbToJdUtc(double bjd, double raDeg, double decDeg) {
        var jdTdb = bjd;
        for (var i = 0; i < 3; i++) jdTdb = bjd - HeliocentricCorrectionDays(jdTdb - AstroTime.TtMinusUtcSeconds / 86400.0, raDeg, decDeg);
        return jdTdb - AstroTime.TtMinusUtcSeconds / 86400.0;
    }
}

/// <summary>
/// Geocentric position of the Moon from the largest terms of Meeus ch. 47 (tables 47.A and 47.B).
/// Accuracy is a few hundredths of a degree, far finer than any Moon-avoidance limit.
/// </summary>
public static class Moon {
    // D, M, M', F, sigma-l (1e-6 deg), sigma-r (1e-3 km)
    static readonly (int d, int m, int mp, int f, double l, double r)[] LR = {
        (0, 0, 1, 0, 6288774, -20905355), (2, 0, -1, 0, 1274027, -3699111), (2, 0, 0, 0, 658314, -2955968),
        (0, 0, 2, 0, 213618, -569925), (0, 1, 0, 0, -185116, 48888), (0, 0, 0, 2, -114332, -3149),
        (2, 0, -2, 0, 58793, 246158), (2, -1, -1, 0, 57066, -152138), (2, 0, 1, 0, 53322, -170733),
        (2, -1, 0, 0, 45758, -204586), (0, 1, -1, 0, -40923, -129620), (1, 0, 0, 0, -34720, 108743),
        (0, 1, 1, 0, -30383, 104755), (2, 0, 0, -2, 15327, 10321), (0, 0, 1, 2, -12528, 0),
        (0, 0, 1, -2, 10980, 79661), (4, 0, -1, 0, 10675, -34782), (0, 0, 3, 0, 10034, -23210),
        (4, 0, -2, 0, 8548, -21636), (2, 1, -1, 0, -7888, 24208), (2, 1, 0, 0, -6766, 30824),
        (1, 0, -1, 0, -5163, -8379), (1, 1, 0, 0, 4987, -16675), (2, -1, 1, 0, 4036, -12831),
        (2, 0, 2, 0, 3994, -10445), (4, 0, 0, 0, 3861, -11650), (2, 0, -3, 0, 3665, 14403),
        (0, 1, -2, 0, -2689, -7003), (2, 0, -1, 2, -2602, 0), (2, -1, -2, 0, 2390, 10056),
        (1, 0, 1, 0, -2348, 6322), (2, -2, 0, 0, 2236, -9884), (0, 1, 2, 0, -2120, 5751),
        (0, 2, 0, 0, -2069, 0), (2, -2, -1, 0, 2048, -4950), (2, 0, 1, -2, -1773, 4130),
        (2, 0, 0, 2, -1595, 0), (4, -1, -1, 0, 1215, -3958), (0, 0, 2, 2, -1110, 0),
        (3, 0, -1, 0, -892, 3258), (2, 1, 1, 0, -810, 2616), (4, -1, -2, 0, 759, -1897),
        (0, 2, -1, 0, -713, -2117), (2, 2, -1, 0, -700, 2354), (2, 1, -2, 0, 691, 0),
        (2, -1, 0, -2, 596, 0), (4, 0, 1, 0, 549, -1423), (0, 0, 4, 0, 537, -1117),
        (4, -1, 0, 0, 520, -1571), (1, 0, -2, 0, -487, -1739), (2, 1, 0, -2, -399, 0),
        (0, 0, 2, -2, -381, -4421), (1, 1, 1, 0, 351, 0), (3, 0, -2, 0, -340, 0),
        (4, 0, -3, 0, 330, 0), (2, -1, 2, 0, 327, 0), (0, 2, 1, 0, -323, 1165),
        (1, 1, -1, 0, 299, 0), (2, 0, 3, 0, 294, 0), (2, 0, -1, -2, 0, 8752),
    };

    // D, M, M', F, sigma-b (1e-6 deg)
    static readonly (int d, int m, int mp, int f, double b)[] B = {
        (0, 0, 0, 1, 5128122), (0, 0, 1, 1, 280602), (0, 0, 1, -1, 277693), (2, 0, 0, -1, 173237),
        (2, 0, -1, 1, 55413), (2, 0, -1, -1, 46271), (2, 0, 0, 1, 32573), (0, 0, 2, 1, 17198),
        (2, 0, 1, -1, 9266), (0, 0, 2, -1, 8822), (2, -1, 0, -1, 8216), (2, 0, -2, -1, 4324),
        (2, 0, 1, 1, 4200), (2, 1, 0, -1, -3359), (2, -1, -1, 1, 2463), (2, -1, 0, 1, 2211),
        (2, -1, -1, -1, 2065), (0, 1, -1, -1, -1870), (4, 0, -1, -1, 1828), (0, 1, 0, 1, -1794),
        (0, 0, 0, 3, -1749), (0, 1, -1, 1, -1565), (1, 0, 0, 1, -1491), (0, 1, 1, 1, -1475),
        (0, 1, 1, -1, -1410), (0, 1, 0, -1, -1344), (1, 0, 0, -1, -1335), (0, 0, 3, 1, 1107),
        (4, 0, 0, -1, 1021), (4, 0, -1, 1, 833),
    };

    static (double lp, double d, double m, double mp, double f, double e, double t) Args(double jdUtc) {
        var t = AstroTime.CenturiesTT(jdUtc);
        var lp = Norm360(218.3164477 + 481267.88123421 * t - 0.0015786 * t * t + t * t * t / 538841.0);
        var d = Norm360(297.8501921 + 445267.1114034 * t - 0.0018819 * t * t + t * t * t / 545868.0);
        var m = Norm360(357.5291092 + 35999.0502909 * t - 0.0001536 * t * t);
        var mp = Norm360(134.9633964 + 477198.8675055 * t + 0.0087414 * t * t + t * t * t / 69699.0);
        var f = Norm360(93.2720950 + 483202.0175233 * t - 0.0036539 * t * t);
        var e = 1 - 0.002516 * t - 0.0000074 * t * t;
        return (lp, d, m, mp, f, e, t);
    }

    /// <summary>Ecliptic longitude/latitude (degrees, mean equinox of date) and distance (km).</summary>
    public static (double lambda, double beta, double distanceKm) Ecliptic(double jdUtc) {
        var (lp, d, m, mp, f, e, t) = Args(jdUtc);
        double sl = 0, sr = 0, sb = 0;
        foreach (var x in LR) {
            var arg = x.d * d + x.m * m + x.mp * mp + x.f * f;
            var k = Math.Abs(x.m) == 1 ? e : Math.Abs(x.m) == 2 ? e * e : 1;
            sl += k * x.l * SinD(arg);
            sr += k * x.r * CosD(arg);
        }
        foreach (var x in B) {
            var arg = x.d * d + x.m * m + x.mp * mp + x.f * f;
            var k = Math.Abs(x.m) == 1 ? e : Math.Abs(x.m) == 2 ? e * e : 1;
            sb += k * x.b * SinD(arg);
        }
        var a1 = 119.75 + 131.849 * t;
        var a2 = 53.09 + 479264.290 * t;
        var a3 = 313.45 + 481266.484 * t;
        sl += 3958 * SinD(a1) + 1962 * SinD(lp - f) + 318 * SinD(a2);
        sb += -2235 * SinD(lp) + 382 * SinD(a3) + 175 * SinD(a1 - f) + 175 * SinD(a1 + f) + 127 * SinD(lp - mp) - 115 * SinD(lp + mp);
        return (Norm360(lp + sl / 1e6), sb / 1e6, 385000.56 + sr / 1000.0);
    }

    /// <summary>Geocentric right ascension, declination (equinox of date, degrees) and distance (km).</summary>
    public static (double ra, double dec, double distanceKm) Position(double jdUtc) {
        var (lambda, beta, dist) = Ecliptic(jdUtc);
        var t = AstroTime.CenturiesTT(jdUtc);
        var eps = 23.4392911 - 0.0130042 * t;
        var ra = Norm360(Atan2D(SinD(lambda) * CosD(eps) - TanD(beta) * SinD(eps), CosD(lambda)));
        var dec = AsinD(SinD(beta) * CosD(eps) + CosD(beta) * SinD(eps) * SinD(lambda));
        return (ra, dec, dist);
    }

    /// <summary>Topocentric altitude (degrees): geocentric altitude lowered by the Moon's parallax.</summary>
    public static double Altitude(double jdUtc, double lat, double lonEast) {
        var (ra, dec, dist) = Position(jdUtc);
        var alt = Coords.Horizontal(ra, dec, lat, AstroTime.LmstDegrees(jdUtc, lonEast)).alt;
        var parallax = AsinD(6378.14 / dist);
        return alt - parallax * CosD(alt);
    }

    /// <summary>Illuminated fraction 0..1 (Meeus 48.4 and 48.1).</summary>
    public static double Illumination(double jdUtc) {
        var (_, d, m, mp, _, _, _) = Args(jdUtc);
        var i = 180 - d - 6.289 * SinD(mp) + 2.100 * SinD(m) - 1.274 * SinD(2 * d - mp) - 0.658 * SinD(2 * d) - 0.214 * SinD(2 * mp) - 0.110 * SinD(d);
        return (1 + CosD(i)) / 2;
    }
}
