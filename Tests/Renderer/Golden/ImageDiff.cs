using System.Globalization;
using SkiaSharp;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// How far a rendered image is allowed to drift from its baseline before the scene is considered broken.
    ///
    /// Three numbers rather than one, because the failures they catch are different in kind. A single wrong
    /// texel from a format mismatch moves <see cref="MaxChannelDeviation"/> and nothing else; a transposed
    /// matrix moves <see cref="MaxDeviantPixelFraction"/>; a wrong tonemap or clear colour moves
    /// <see cref="MaxMeanChannelDeviation"/> while leaving the other two looking survivable. A budget that
    /// only bounded the mean would pass all three.
    /// </summary>
    /// <param name="MaxChannelDeviation">
    /// Largest allowed absolute difference on any single channel of any single pixel, in 0..1.
    /// </param>
    /// <param name="MaxMeanChannelDeviation">
    /// Largest allowed mean absolute channel difference over the whole image, in 0..1.
    /// </param>
    /// <param name="MaxDeviantPixelFraction">
    /// Largest allowed fraction of pixels whose perceptual difference exceeds <see cref="DeviantPixelThreshold"/>.
    /// </param>
    /// <param name="DeviantPixelThreshold">
    /// Perceptual (luma weighted) difference above which a pixel counts as deviant, in 0..1.
    /// </param>
    internal readonly record struct ImageTolerance(
        double MaxChannelDeviation,
        double MaxMeanChannelDeviation,
        double MaxDeviantPixelFraction,
        double DeviantPixelThreshold = 4.0 / 255.0)
    {
        /// <summary>
        /// The reproducibility floor of the renderer, in channel levels. It is now zero.
        ///
        /// <para>It was not always. <c>PostProcessRenderer</c> used to re-randomise the blue-noise dither
        /// offset every frame from an unseeded <see cref="Random"/>, with an amplitude of
        /// <c>2.0f / 255.0f</c>, and every budget below had to clear that. The generator is now seeded, and
        /// re-recording the whole catalog twice produces byte-identical PNGs, so the budgets below are
        /// sized for genuine difference rather than for noise.</para>
        ///
        /// <para>Kept as a named constant, at zero, because it is the assumption the tight budgets rest on:
        /// anything that reintroduces per-frame randomness into the frame will show up as most of the suite
        /// going red at once, and this is where the explanation lives.</para>
        /// </summary>
        public const double DitherFloor = 0.0;

        /// <summary>
        /// The budget for a scene whose output should be reproduced almost exactly: flat colour, unlit
        /// geometry, a directly read back depth buffer, or anything else with no accumulation in it.
        ///
        /// <para>A single channel level on a single pixel. Not zero, only because a driver update that
        /// changes a rounding mode somewhere should read as a baseline to review rather than as the suite
        /// collapsing; anything a change in the renderer does is larger than this.</para>
        /// </summary>
        public static ImageTolerance Strict { get; } = new(1.0 / 255.0, 0.05 / 255.0, 0.0002, 1.0 / 255.0);

        /// <summary>
        /// The budget for lit geometry. Enough to absorb the last bit of floating point drift in a lighting
        /// or shadow term across driver revisions, and nothing beyond that.
        /// </summary>
        public static ImageTolerance Lit { get; } = new(4.0 / 255.0, 0.2 / 255.0, 0.0005, 2.0 / 255.0);

        /// <summary>
        /// The budget for the quad overdraw heat map, which is not reproducible and cannot be made so.
        ///
        /// <para>The counting pass accumulates per-quad shading cost through unordered image stores from
        /// concurrent fragment shader invocations. How many invocations a silhouette edge produces depends
        /// on helper-lane scheduling, so counts at edges vary by one between runs -- and because the heat
        /// map quantises counts into colour bands, a count that moves by one across a band boundary moves
        /// that pixel by over half the colour range. Measured across runs in both the OpenGL and the RHI
        /// path: max channel deviation pinned at 135/255, between 48 and 132 pixels affected out of 76800,
        /// mean deviation never above 0.10/255.</para>
        ///
        /// <para>So the maximum is deliberately not the instrument here; the mean and the affected fraction
        /// are, and both are held tight. A regression that actually broke the counter, the legend or the
        /// pass ordering moves a large share of the image and fails on those two immediately.</para>
        ///
        /// <para><b>Re-measured, and left where it is.</b> A recording run once reported 35/36 and could
        /// not be reproduced, which put this budget under suspicion. It is not the cause: over further
        /// runs in catalog order the scene measured max pinned at 135/255 against 160, mean 0.05-0.06/255
        /// against 0.5, and 64 to 88 deviant pixels against a budget of 384 -- the same numbers recorded
        /// above, with the mean and fraction the tight instruments and 4x to 8x of room in each. What did
        /// reproduce is that the scene's image depends on what ran before it; see
        /// <c>:OverdrawSceneOrderDependence</c> in <c>GoldenSceneCatalog</c>, which is where a one-off
        /// should be looked for first.</para>
        /// </summary>
        public static ImageTolerance CountingRace { get; } = new(160.0 / 255.0, 0.5 / 255.0, 0.005, 8.0 / 255.0);

        /// <summary>
        /// The budget for the parts of the frame that accumulate over many texels: bloom, depth of field
        /// and anything else where a blur kernel spreads small differences across the image.
        /// </summary>
        public static ImageTolerance Accumulating { get; } = new(8.0 / 255.0, 0.5 / 255.0, 0.002, 4.0 / 255.0);
    }

    /// <summary>Measured difference between a rendered image and its baseline.</summary>
    internal sealed record ImageDiffResult(
        int Width,
        int Height,
        double MaxChannelDeviation,
        double MeanChannelDeviation,
        int DeviantPixelCount,
        int TotalPixelCount)
    {
        /// <summary>Fraction of pixels that exceeded the perceptual threshold.</summary>
        public double DeviantPixelFraction => TotalPixelCount == 0 ? 0 : (double)DeviantPixelCount / TotalPixelCount;

        /// <summary>Whether every measured quantity sits inside <paramref name="tolerance"/>.</summary>
        public bool IsWithin(ImageTolerance tolerance)
            => MaxChannelDeviation <= tolerance.MaxChannelDeviation
            && MeanChannelDeviation <= tolerance.MaxMeanChannelDeviation
            && DeviantPixelFraction <= tolerance.MaxDeviantPixelFraction;

        /// <summary>Formats the measurements next to the budget they were checked against.</summary>
        public string Describe(ImageTolerance tolerance)
        {
            static string Channels(double value) => (value * 255.0).ToString("F2", CultureInfo.InvariantCulture);
            static string Percent(double value) => (value * 100.0).ToString("F4", CultureInfo.InvariantCulture);

            return $"max channel deviation {Channels(MaxChannelDeviation)}/255 (budget {Channels(tolerance.MaxChannelDeviation)}), "
                + $"mean channel deviation {Channels(MeanChannelDeviation)}/255 (budget {Channels(tolerance.MaxMeanChannelDeviation)}), "
                + $"{DeviantPixelCount} of {TotalPixelCount} pixels over threshold = {Percent(DeviantPixelFraction)}% "
                + $"(budget {Percent(tolerance.MaxDeviantPixelFraction)}%)";
        }
    }

    /// <summary>Compares two rendered images and renders the difference between them.</summary>
    internal static class ImageDiff
    {
        // Rec. 709 luma weights. A blue-channel error of a given size is far less visible than the same
        // error in green, and weighting the per-pixel difference this way keeps the deviant-pixel count
        // from being dominated by channels a reviewer would never notice in the artifact PNGs.
        private const double LumaR = 0.2126;
        private const double LumaG = 0.7152;
        private const double LumaB = 0.0722;

        /// <summary>
        /// Measures how far <paramref name="actual"/> is from <paramref name="expected"/>.
        /// </summary>
        /// <exception cref="ArgumentException">The two images do not have the same dimensions.</exception>
        public static ImageDiffResult Compare(SKBitmap expected, SKBitmap actual, ImageTolerance tolerance)
        {
            if (expected.Width != actual.Width || expected.Height != actual.Height)
            {
                throw new ArgumentException(
                    $"Image sizes differ: baseline is {expected.Width}x{expected.Height}, render is {actual.Width}x{actual.Height}.",
                    nameof(actual));
            }

            var width = expected.Width;
            var height = expected.Height;
            var expectedPixels = expected.GetPixelSpan();
            var actualPixels = actual.GetPixelSpan();

            var maxDeviation = 0;
            var deviationSum = 0L;
            var deviantPixels = 0;
            var threshold = tolerance.DeviantPixelThreshold * 255.0;

            for (var i = 0; i < width * height; i++)
            {
                var offset = i * 4;

                // Bgra8888, and alpha is ignored: the harness always renders opaque frames, and an alpha
                // channel that the scene never writes would otherwise contribute noise to the mean.
                var deltaB = Math.Abs(expectedPixels[offset + 0] - actualPixels[offset + 0]);
                var deltaG = Math.Abs(expectedPixels[offset + 1] - actualPixels[offset + 1]);
                var deltaR = Math.Abs(expectedPixels[offset + 2] - actualPixels[offset + 2]);

                maxDeviation = Math.Max(maxDeviation, Math.Max(deltaR, Math.Max(deltaG, deltaB)));
                deviationSum += deltaR + deltaG + deltaB;

                var perceptual = LumaR * deltaR + LumaG * deltaG + LumaB * deltaB;

                if (perceptual > threshold)
                {
                    deviantPixels++;
                }
            }

            var totalPixels = width * height;
            var meanDeviation = totalPixels == 0 ? 0 : deviationSum / (double)(totalPixels * 3);

            return new ImageDiffResult(
                width,
                height,
                maxDeviation / 255.0,
                meanDeviation / 255.0,
                deviantPixels,
                totalPixels);
        }

        /// <summary>
        /// Renders the difference between two images for a human to look at: the baseline dimmed to a faint
        /// grey so the shape of the scene stays legible, with every differing pixel painted a saturated
        /// magenta whose brightness tracks how far it moved.
        /// </summary>
        public static SKBitmap Render(SKBitmap expected, SKBitmap actual)
        {
            var width = Math.Min(expected.Width, actual.Width);
            var height = Math.Min(expected.Height, actual.Height);

            var diff = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var expectedPixels = expected.GetPixelSpan();
            var actualPixels = actual.GetPixelSpan();
            var diffPixels = diff.GetPixelSpan();

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var expectedOffset = (y * expected.Width + x) * 4;
                    var actualOffset = (y * actual.Width + x) * 4;
                    var diffOffset = (y * width + x) * 4;

                    var deltaB = Math.Abs(expectedPixels[expectedOffset + 0] - actualPixels[actualOffset + 0]);
                    var deltaG = Math.Abs(expectedPixels[expectedOffset + 1] - actualPixels[actualOffset + 1]);
                    var deltaR = Math.Abs(expectedPixels[expectedOffset + 2] - actualPixels[actualOffset + 2]);
                    var delta = Math.Max(deltaR, Math.Max(deltaG, deltaB));

                    if (delta == 0)
                    {
                        var grey = (byte)((expectedPixels[expectedOffset + 0]
                            + expectedPixels[expectedOffset + 1]
                            + expectedPixels[expectedOffset + 2]) / 12);

                        diffPixels[diffOffset + 0] = grey;
                        diffPixels[diffOffset + 1] = grey;
                        diffPixels[diffOffset + 2] = grey;
                    }
                    else
                    {
                        // Amplified so a difference of one or two levels is still visible on a screen.
                        var intensity = (byte)Math.Min(255, 64 + delta * 4);

                        diffPixels[diffOffset + 0] = intensity;
                        diffPixels[diffOffset + 1] = 0;
                        diffPixels[diffOffset + 2] = intensity;
                    }

                    diffPixels[diffOffset + 3] = 255;
                }
            }

            return diff;
        }
    }
}
