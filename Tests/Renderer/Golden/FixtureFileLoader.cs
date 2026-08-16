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
    ///
    /// <para>A fixture that is itself a VPK is a different case and needs no stand-in at all. Some of the
    /// fixtures under <c>Tests/Files</c> are whole packages holding a map and everything it refers to, and
    /// mounting one through <see cref="MountPackage"/> gives that map a search path in which its references
    /// really do resolve. See <see cref="GoldenSceneCatalog"/>'s map scenes for what that buys.</para>
    /// </summary>
    internal sealed class FixtureFileLoader : GameFileLoader
    {
        private readonly Dictionary<string, string> substitutions = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> mountedPackages = new(StringComparer.OrdinalIgnoreCase);

        public FixtureFileLoader() : base(null, null)
        {
        }

        /// <summary>
        /// Adds a VPK fixture to this loader's search paths, so everything inside it resolves by its
        /// packaged path.
        ///
        /// <para>One loader serves every scene in a run, so mounting is idempotent: a scene declares the
        /// package it needs without having to know whether an earlier scene already asked for it.</para>
        ///
        /// <para>Nothing is unmounted between scenes, which means a package mounted by one scene is on the
        /// search path for every scene after it. That is deliberate -- unmounting would make a scene's
        /// result depend on the order the catalog happened to run in -- but it does put the burden on the
        /// package fixtures not to answer a reference some other scene makes. Check that before adding one:
        /// today neither package holds anything a non-map scene asks for, and a package that did would
        /// silently change an existing baseline rather than fail.</para>
        /// </summary>
        /// <param name="fixtureRelativePath">A <c>.vpk</c> path under <c>Tests/Files</c>.</param>
        public void MountPackage(string fixtureRelativePath)
        {
            if (!mountedPackages.Add(fixtureRelativePath))
            {
                return;
            }

            // Owned by the base loader from here on: it disposes everything in its search list.
            AddPackageToSearch(GoldenSceneSetup.FixturePath(fixtureRelativePath));
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
