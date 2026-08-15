using System.IO;
using ValveResourceFormat;
using ValveResourceFormat.IO;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// A file loader that can satisfy a named external reference from a fixture on disk.
    ///
    /// <para>Nothing under <c>Tests/Files</c> sits in a game tree, so every external reference a fixture
    /// makes resolves to nothing and the renderer substitutes its error material. That is fine for geometry
    /// -- the error material is deterministic and the shape is still under test -- but it is fatal wherever
    /// a missing reference stops construction outright rather than degrading. The morph path is the case
    /// that matters: <c>MorphComposite</c>'s constructor throws on a null texture atlas, so a morph scene
    /// cannot exist at all without one.</para>
    ///
    /// <para>Registering a stand-in is honest for that purpose because the morph composite's own logic --
    /// which atlas rectangles it selects and where it writes them -- does not depend on what the atlas
    /// contains. Substituting a texture with strong spatial variation makes that selection <em>visible</em>
    /// in the composited image, which is precisely what an oracle for it needs.</para>
    /// </summary>
    internal sealed class FixtureFileLoader : GameFileLoader
    {
        private readonly Dictionary<string, string> substitutions = new(StringComparer.OrdinalIgnoreCase);

        public FixtureFileLoader() : base(null, null)
        {
        }

        /// <summary>
        /// Serves <paramref name="fixtureRelativePath"/> whenever something asks for <paramref name="referencePath"/>.
        /// </summary>
        /// <param name="referencePath">The path as the resource refers to it, with or without the compiled suffix.</param>
        /// <param name="fixtureRelativePath">A path under <c>Tests/Files</c>.</param>
        public void Substitute(string referencePath, string fixtureRelativePath)
        {
            substitutions[Normalize(referencePath)] = fixtureRelativePath;
        }

        private static string Normalize(string path)
        {
            var normalized = path.Replace('\\', '/');

            return normalized.EndsWith(CompiledFileSuffix, StringComparison.OrdinalIgnoreCase)
                ? normalized[..^CompiledFileSuffix.Length]
                : normalized;
        }

        /// <inheritdoc/>
        public override Resource? LoadFile(string file)
        {
            if (!substitutions.TryGetValue(Normalize(file), out var fixture))
            {
                return base.LoadFile(file);
            }

            var path = GoldenSceneSetup.FixturePath(fixture);

            if (!File.Exists(path))
            {
                return null;
            }

            // Named as the caller asked for it, not as the fixture is named on disk, so anything that keys
            // off the resource's file name still sees the reference it requested.
            var resource = new Resource { FileName = file.Replace('\\', '/') };
            resource.Read(path);

            return resource;
        }
    }
}
