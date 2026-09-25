using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Forms;
using GUI.Types.GLViewers;
using GUI.Utils;
using ValvePak;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.World;
using ValveResourceFormat.ResourceTypes;

namespace GUI.Types.Viewers
{
    /// <summary>
    /// Tab contents comparing two builds of a map, each opened through its own file loader so files with the
    /// same name in both builds resolve to their own build.
    /// </summary>
    /// <param name="newContext">The new build, owned by the tab.</param>
    /// <param name="newMapFile">The new build's map, <c>maps/name.vmap_c</c>.</param>
    /// <param name="oldContext">The old build, owned by this viewer.</param>
    /// <param name="oldMapFile">The old build's map.</param>
    class MapDiff(VrfGuiContext newContext, string newMapFile, VrfGuiContext oldContext, string oldMapFile) : IViewer
    {
        private RendererContext? newRendererContext;
        private RendererContext? oldRendererContext;
        private GLWorldDiffViewer? viewer;

        public Task LoadAsync(Stream? stream)
        {
            var (newWorld, newReferences) = LoadWorld(newContext, newMapFile);
            var (oldWorld, oldReferences) = LoadWorld(oldContext, oldMapFile);

            newRendererContext = newContext.CreateRendererContext();
            oldRendererContext = oldContext.CreateRendererContext();

            viewer = new GLWorldDiffViewer(newContext, newRendererContext, newWorld, newReferences, oldRendererContext, oldWorld, oldReferences);
            viewer.InitializeLoad();

            // The new build's cache is cleared by the viewer once it has loaded, the old one's is not
            oldContext.ClearCache();

            return Task.CompletedTask;
        }

        private static (World World, ResourceExtRefList? ExternalReferences) LoadWorld(VrfGuiContext context, string mapFile)
        {
            var map = context.LoadFile(mapFile) ?? throw new FileNotFoundException($"Failed to load \"{mapFile}\" from {context.FileName}");
            var worldResource = context.LoadFileCompiled(WorldLoader.GetWorldNameFromMap(mapFile));

            if (worldResource?.DataBlock is not World world)
            {
                throw new InvalidDataException($"\"{mapFile}\" in {context.FileName} has no world");
            }

            return (world, map.ExternalReferences);
        }

        public void NotifyVisible() => viewer?.NotifyVisible();

        public void Create(TabPage containerTabPage)
        {
            Debug.Assert(viewer != null);

            containerTabPage.Controls.Add(viewer.InitializeUiControls());
        }

        public void Dispose()
        {
            // Order matters: nothing may dispose a resource until every thread that could still be reading it has stopped
            viewer?.Dispose();
            newRendererContext?.Dispose();
            oldRendererContext?.Dispose();
            oldContext.Dispose();
        }

        /// <summary>
        /// Opens one build of a map for comparing: a map package, whose map is looked up inside it, or a
        /// compiled map on disk.
        /// </summary>
        /// <returns>The build's file loader and its map, or <see langword="null"/> when there is no map or none was picked.</returns>
        public static (VrfGuiContext Context, string MapFile)? OpenBuild(string path)
        {
            if (!path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
            {
                return (new VrfGuiContext(path, null), path);
            }

            var package = new Package();

            try
            {
                package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
                package.Read(path);

                var mapFile = PickMap(package, Path.GetFileNameWithoutExtension(path));

                if (mapFile == null)
                {
                    package.Dispose();
                    return null;
                }

                var context = new VrfGuiContext(path, null)
                {
                    CurrentPackage = package,
                };

                return (context, mapFile);
            }
            catch
            {
                package.Dispose();
                throw;
            }
        }

        private static string? PickMap(Package package, string packageName)
        {
            if (package.Entries == null || !package.Entries.TryGetValue("vmap_c", out var entries) || entries.Count == 0)
            {
                _ = AppMessageDialogs.ShowMessageAsync($"There is no compiled map in {packageName}.vpk.", "Compare maps", MessageIcon.Warning);
                return null;
            }

            var maps = entries.Select(static entry => entry.GetFullPath()).Order(StringComparer.OrdinalIgnoreCase).ToList();

            if (maps.Count == 1)
            {
                return maps[0];
            }

            // A package of an older build is usually renamed, so only use its name when it matches one of its maps
            var named = maps.Find(map => Path.GetFileNameWithoutExtension(map).Equals(packageName, StringComparison.OrdinalIgnoreCase));

            if (named != null)
            {
                return named;
            }

            using var form = new ThemedForm
            {
                Text = $"Pick a map from {packageName}.vpk",
                StartPosition = FormStartPosition.CenterParent,
                Width = 480,
                Height = 360,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
            };

            using var list = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
            };

            list.Items.AddRange([.. maps]);
            list.SelectedIndex = 0;
            list.DoubleClick += (_, _) => form.DialogResult = DialogResult.OK;

            using var button = new Button
            {
                Text = "Compare",
                Dock = DockStyle.Bottom,
                DialogResult = DialogResult.OK,
            };

            form.Controls.Add(list);
            form.Controls.Add(button);
            form.AcceptButton = button;

            return form.ShowDialog(Program.MainForm) == DialogResult.OK ? list.SelectedItem as string : null;
        }
    }
}
