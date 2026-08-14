using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using SkiaSharp;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// Reads and writes the baseline PNGs, and drops the expected/actual/diff triple somewhere findable when
    /// a scene fails.
    /// </summary>
    internal static class GoldenImageStore
    {
        /// <summary>
        /// Setting this environment variable to <c>1</c> makes a run overwrite every baseline with what the
        /// renderer produces instead of comparing against it. This is how the baselines are regenerated after
        /// an intentional visual change; the diff of the resulting PNGs is the review artifact.
        /// </summary>
        public const string UpdateEnvironmentVariable = "VRF_GOLDEN_UPDATE";

        /// <summary>Whether this run regenerates baselines rather than checking them.</summary>
        public static bool IsUpdating
            => Environment.GetEnvironmentVariable(UpdateEnvironmentVariable) is "1" or "true";

        /// <summary>
        /// Baselines in the source tree. Regeneration writes here so the new PNGs land in the working copy
        /// ready to be reviewed, rather than in an output directory that the next build wipes.
        /// </summary>
        public static string SourceBaselineDirectory { get; } = Path.Combine(SourceDirectory(), "Baselines");

        /// <summary>
        /// Baselines copied next to the test binary. Comparison reads here so the suite still works when it
        /// runs from a published output with no source tree beside it.
        /// </summary>
        public static string OutputBaselineDirectory { get; }
            = Path.Combine(TestContext.CurrentContext.TestDirectory, "Renderer", "Golden", "Baselines");

        /// <summary>Where the expected/actual/diff PNGs of a failing scene are written.</summary>
        public static string FailureDirectory { get; }
            = Path.Combine(TestContext.CurrentContext.WorkDirectory, "GoldenImageFailures");

        private static string SourceDirectory([CallerFilePath] string sourceFilePath = "")
            => Path.GetDirectoryName(sourceFilePath)!;

        private static string BaselineFileName(string sceneName) => sceneName + ".png";

        /// <summary>
        /// Locates a scene's baseline, preferring the source tree so that a regenerated baseline is picked up
        /// without waiting for a rebuild to copy it.
        /// </summary>
        /// <returns>The path, or <see langword="null"/> when no baseline has been recorded for this scene.</returns>
        public static string? FindBaseline(string sceneName)
        {
            var fileName = BaselineFileName(sceneName);

            var sourcePath = Path.Combine(SourceBaselineDirectory, fileName);

            if (File.Exists(sourcePath))
            {
                return sourcePath;
            }

            var outputPath = Path.Combine(OutputBaselineDirectory, fileName);

            return File.Exists(outputPath) ? outputPath : null;
        }

        /// <summary>Loads a baseline PNG in the same pixel layout the renderer reads back.</summary>
        public static SKBitmap LoadBaseline(string path)
        {
            using var data = SKData.Create(path)
                ?? throw new InvalidDataException($"Could not read the baseline image at '{path}'.");

            using var codecBitmap = SKBitmap.Decode(data)
                ?? throw new InvalidDataException($"'{path}' is not a decodable image.");

            if (codecBitmap.ColorType == SKColorType.Bgra8888 && codecBitmap.AlphaType == SKAlphaType.Opaque)
            {
                return codecBitmap.Copy();
            }

            var converted = new SKBitmap(codecBitmap.Width, codecBitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            codecBitmap.CopyTo(converted, SKColorType.Bgra8888);

            return converted;
        }

        /// <summary>Writes a baseline into the source tree, creating the directory on first use.</summary>
        /// <returns>The path written.</returns>
        public static string WriteBaseline(string sceneName, SKBitmap image)
        {
            Directory.CreateDirectory(SourceBaselineDirectory);

            var path = Path.Combine(SourceBaselineDirectory, BaselineFileName(sceneName));
            WritePng(path, image);

            return path;
        }

        /// <summary>
        /// Writes the three PNGs a reviewer needs to understand a failure and attaches them to the test
        /// result, so a CI run surfaces them without anyone having to know the directory layout.
        /// </summary>
        /// <returns>The directory the artifacts were written to.</returns>
        public static string WriteFailureArtifacts(string sceneName, SKBitmap? expected, SKBitmap actual)
        {
            Directory.CreateDirectory(FailureDirectory);

            var actualPath = Path.Combine(FailureDirectory, sceneName + ".actual.png");
            WritePng(actualPath, actual);
            TestContext.AddTestAttachment(actualPath, $"{sceneName}: rendered this run");

            if (expected != null)
            {
                var expectedPath = Path.Combine(FailureDirectory, sceneName + ".expected.png");
                WritePng(expectedPath, expected);
                TestContext.AddTestAttachment(expectedPath, $"{sceneName}: recorded baseline");

                using var diff = ImageDiff.Render(expected, actual);
                var diffPath = Path.Combine(FailureDirectory, sceneName + ".diff.png");
                WritePng(diffPath, diff);
                TestContext.AddTestAttachment(diffPath, $"{sceneName}: differing pixels in magenta");
            }

            return FailureDirectory;
        }

        private static void WritePng(string path, SKBitmap image)
        {
            using var pixmap = image.PeekPixels();
            using var encoded = pixmap.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException($"Failed to PNG encode the image for '{path}'.");

            using var stream = File.Create(path);
            encoded.SaveTo(stream);
        }
    }
}
