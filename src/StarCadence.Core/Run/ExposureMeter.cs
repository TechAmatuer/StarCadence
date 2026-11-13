// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using StarCadence.Core.Planning;

namespace StarCadence.Core.Run;

/// <summary>What a test frame showed about the target star.</summary>
/// <param name="PeakAdu">The star's peak above the background (its brightest pixel).</param>
/// <param name="Background">The sky level, bias included.</param>
/// <param name="Noise">The background's scatter (robust standard deviation).</param>
/// <param name="Saturated">Two or more pixels at the star's centre are at or near full scale.</param>
/// <param name="X">Column of the star's peak.</param>
/// <param name="Y">Row of the star's peak.</param>
public sealed record ExposureMeasurement(double PeakAdu, double Background, double Noise, bool Saturated, int X, int Y) {
    /// <summary>True when the star stands out clearly enough to scale from.</summary>
    public bool Clear => PeakAdu >= 10 * Math.Max(Noise, 1);
}

/// <summary>
/// Sets the exposure from a test frame instead of from the catalogue magnitude, as the N.I.N.A. ExoPlanets plug-in's
/// "Calculate Exposure Time" does: find the target star near the centre of a frame taken just after centring, measure
/// its peak, and scale the exposure so the peak lands at a chosen share of the camera's range (well below saturation,
/// where the response is linear, which photometry needs).
/// </summary>
public static class ExposureMeter {
    /// <summary>
    /// The brightest star within <paramref name="radius"/> pixels of (<paramref name="cx"/>, <paramref name="cy"/>).
    /// Each pixel is replaced by the median of its 3x3 neighbourhood first, so a hot pixel or a cosmic-ray hit is not
    /// taken for a star. If another star brighter than the target sits inside the radius, that one is measured, which
    /// only makes the exposure shorter: it never saturates the target.
    /// </summary>
    public static ExposureMeasurement Measure(ReadOnlySpan<ushort> pixels, int width, int height, double cx, double cy, double radius, double maxAdu) {
        if (width < 3 || height < 3 || pixels.Length < width * height) throw new ArgumentException("image too small or pixel count does not match its size");
        var (background, noise) = Background(pixels, width, height);

        var x0 = Math.Max(1, (int)Math.Floor(cx - radius)); var x1 = Math.Min(width - 2, (int)Math.Ceiling(cx + radius));
        var y0 = Math.Max(1, (int)Math.Floor(cy - radius)); var y1 = Math.Min(height - 2, (int)Math.Ceiling(cy + radius));
        var r2 = radius * radius;
        Span<ushort> patch = stackalloc ushort[9];
        double best = double.MinValue; int bx = (int)cx, by = (int)cy;
        for (var y = y0; y <= y1; y++) {
            var dy = y - cy;
            for (var x = x0; x <= x1; x++) {
                var dx = x - cx;
                if (dx * dx + dy * dy > r2) continue;
                var k = 0;
                for (var j = -1; j <= 1; j++) for (var i = -1; i <= 1; i++) patch[k++] = pixels[(y + j) * width + x + i];
                patch.Sort();
                if (patch[4] > best) { best = patch[4]; bx = x; by = y; }
            }
        }
        if (best == double.MinValue) best = background;

        // The median found the star; its peak is the brightest raw pixel at that spot (the median under-reads sharp stars).
        // Saturated: at least two of those pixels at 95% of full scale or more (one alone is likely a hot pixel).
        var nearFull = 0; double peak = best;
        for (var j = -1; j <= 1; j++)
            for (var i = -1; i <= 1; i++) {
                var v = pixels[(by + j) * width + bx + i];
                if (v > peak) peak = v;
                if (v >= 0.95 * maxAdu) nearFull++;
            }
        return new ExposureMeasurement(Math.Max(0, peak - background), background, noise, nearFull >= 2, bx, by);
    }

    /// <summary>
    /// The exposure (seconds) that puts the measured star's peak at <paramref name="targetFraction"/> of
    /// <paramref name="maxAdu"/>, rounded to a familiar value (rounding down near the limit) and kept within the limits.
    /// </summary>
    public static double Suggest(double testSeconds, ExposureMeasurement m, double maxAdu, double targetFraction, double minSeconds, double maxSeconds) {
        var room = targetFraction * maxAdu - m.Background;
        if (room <= 0 || m.PeakAdu <= 0) return minSeconds;
        var raw = testSeconds * room / m.PeakAdu;
        return Math.Clamp(ExposureCalculator.Round(raw, down: true), minSeconds, maxSeconds);
    }

    /// <summary>
    /// The next test exposure to try: shorter when saturated, longer when the star is too faint to measure, or null
    /// when the measurement is good enough to use (or no better test is possible within the limits).
    /// </summary>
    public static double? NextTest(double testSeconds, ExposureMeasurement m, double minSeconds, double maxSeconds) {
        if (m.Saturated) return testSeconds / 2 >= minSeconds ? testSeconds / 2 : null;
        if (!m.Clear) return testSeconds * 4 <= maxSeconds ? testSeconds * 4 : (testSeconds < maxSeconds ? maxSeconds : null);
        return null;
    }

    /// <summary>Full scale for a camera of the given bit depth (65535 when unknown).</summary>
    public static double FullScale(int bitDepth) => bitDepth is >= 8 and <= 16 ? (1 << bitDepth) - 1 : 65535;

    /// <summary>Image scale in arcseconds per pixel.</summary>
    public static double ArcsecPerPixel(double pixelSizeMicrons, double focalLengthMm, int binning = 1) =>
        pixelSizeMicrons > 0 && focalLengthMm > 0 ? 206.265 * pixelSizeMicrons * Math.Max(1, binning) / focalLengthMm : double.NaN;

    // Median and robust scatter of a sparse sample of the frame (about 20,000 pixels).
    static (double median, double sigma) Background(ReadOnlySpan<ushort> pixels, int width, int height) {
        var n = width * height;
        var step = Math.Max(1, n / 20000);
        var sample = new ushort[(n + step - 1) / step];
        var count = 0;
        for (var i = 0; i < n; i += step) sample[count++] = pixels[i];
        var s = sample.AsSpan(0, count);
        s.Sort();
        double median = s[count / 2];
        var dev = new double[count];
        for (var i = 0; i < count; i++) dev[i] = Math.Abs(s[i] - median);
        Array.Sort(dev);
        return (median, 1.4826 * dev[count / 2]);
    }
}
