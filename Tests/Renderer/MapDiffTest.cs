using System.Linq;
using System.Threading.Tasks;
using ValveKeyValue;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer.World;
using ValveResourceFormat.Renderer.World.Diff;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Utils;

namespace Tests.Renderer
{
    /// <summary>What comparing two builds of a map reports, with the second build made by editing files of the first as they load.</summary>
    public class MapDiffTest
    {
        private const string RiverflowPackage = "dota_riverflow_fx.vpk";
        private const string RiverflowLump = "maps/prefabs/dota_riverflow_fx/entities/default_ents.vents_c";
        private const string RiverflowNode = "maps/prefabs/dota_riverflow_fx/worldnodes/n0.vwnod_c";

        private readonly List<IDisposable> disposables = [];

        [After(HookType.Test)]
        public void Dispose()
        {
            for (var i = disposables.Count - 1; i >= 0; i--)
            {
                disposables[i].Dispose();
            }

            disposables.Clear();
        }

        /// <summary>Hands out a package's files, letting the test change one as it is loaded.</summary>
        private sealed class EditingFileLoader(Package package, string packagePath, string editedFile, Action<Resource> edit)
            : GameFileLoader(package, packagePath)
        {
            public override Resource? LoadFile(string file)
            {
                var resource = base.LoadFile(file);

                // World node names are written with backslashes
                if (resource != null && file.Replace('\\', '/').Equals(editedFile, StringComparison.OrdinalIgnoreCase))
                {
                    edit(resource);
                }

                return resource;
            }
        }

        private MapDiffSource Load(string packageName, string? editedFile = null, Action<Resource>? edit = null)
        {
            var packagePath = TestFixtures.Path(packageName);
            var package = new Package();
            disposables.Add(package);
            package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
            package.Read(packagePath);

            GameFileLoader loader = editedFile != null && edit != null
                ? new EditingFileLoader(package, packagePath, editedFile, edit)
                : new GameFileLoader(package, packagePath);
            disposables.Add(loader);

            var map = package.Entries!["vmap_c"][0].GetFullPath();
            var world = loader.LoadFileCompiled(WorldLoader.GetWorldNameFromMap(map))?.DataBlock as World;

            return MapDiffSource.Load(loader, world!);
        }

        /// <summary>The keyvalues of an entity, which are split between its values and its attributes.</summary>
        private static IEnumerable<KVObject> KeyValueCollections(KVObject entity)
        {
            var data = entity.GetSubCollection("keyValues3Data");

            foreach (var name in new[] { "values", "attributes" })
            {
                if (data.TryGetValue(name, out var collection) && collection.ValueType == KVValueType.Collection)
                {
                    yield return collection;
                }
            }
        }

        /// <summary>Finds a keyvalue by the lowercase name the parser gives it, whatever case the file stores it in.</summary>
        private static (KVObject Collection, string Key)? FindKey(KVObject entity, string key)
        {
            foreach (var collection in KeyValueCollections(entity))
            {
                foreach (var child in collection.Children)
                {
                    if (child.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        return (collection, child.Key);
                    }
                }
            }

            return null;
        }

        private static string? GetKey(KVObject entity, string key)
            => FindKey(entity, key) is { } found ? found.Collection.GetStringProperty(found.Key) : null;

        private static void SetKey(KVObject entity, string key, string value)
        {
            var (collection, name) = FindKey(entity, key) ?? (KeyValueCollections(entity).First(), key);
            collection[name] = new KVObject(value);
        }

        private static KVObject Entity(Resource lump, string hammerUniqueId)
            => ((EntityLump)lump.DataBlock!).Data.GetArray("m_entityKeyValues").FirstOrDefault(entity => GetKey(entity, "hammeruniqueid") == hammerUniqueId)
                ?? throw new InvalidOperationException($"No entity with id {hammerUniqueId}");

        private MapDiffResult CompareWithEdit(string editedFile, Action<Resource> edit)
            => MapDiffer.Compute(Load(RiverflowPackage), Load(RiverflowPackage, editedFile, edit));

        [Test]
        [Arguments("small_map_with_material.vpk")]
        [Arguments(RiverflowPackage)]
        [Arguments("point_template_test.vpk")]
        [Arguments("entity_io_param_map_test.vpk")]
        public async Task SameBuildHasNoDifferences(string packageName)
        {
            var result = MapDiffer.Compute(Load(packageName), Load(packageName));

            await Assert.That(result.Entries).IsEmpty();
            await Assert.That(result.OldEntityCount).IsGreaterThan(0);
        }

        [Test]
        public async Task MovedEntityIsReportedAsMoved()
        {
            var result = CompareWithEdit(RiverflowLump, lump => SetKey(Entity(lump, "17"), "origin", "384 -5704 0"));

            await Assert.That(result.Entries).HasSingleItem();

            var entry = result.Entries[0];
            await Assert.That(entry.Kind).IsEqualTo(MapDiffKind.Moved);
            await Assert.That(entry.Category).IsEqualTo(MapDiffCategory.Entity);
            await Assert.That(entry.Type).IsEqualTo("logic_relay");
            await Assert.That(entry.Detail).IsEqualTo("moved 56 units");
            await Assert.That(entry.NewBounds!.Value.Center.Y).IsEqualTo(-5704f).Within(0.01f);
        }

        [Test]
        public async Task ChangedKeyIsReportedWithBothValues()
        {
            var result = CompareWithEdit(RiverflowLump, lump => SetKey(Entity(lump, "4"), "targetname", "renamed"));

            await Assert.That(result.Entries).HasSingleItem();

            var entry = result.Entries[0];
            await Assert.That(entry.Kind).IsEqualTo(MapDiffKind.Modified);
            await Assert.That(entry.Changes.Single(change => change.Key == "targetname").NewValue).IsEqualTo("renamed");
        }

        [Test]
        public async Task DeletedEntityIsReportedAsRemoved()
        {
            var result = CompareWithEdit(RiverflowLump, lump =>
            {
                var entities = ((EntityLump)lump.DataBlock!).Data["m_entityKeyValues"];

                for (var i = 0; i < entities.Count; i++)
                {
                    if (GetKey(entities[i], "hammeruniqueid") == "5")
                    {
                        entities.RemoveAt(i);
                        break;
                    }
                }
            });

            await Assert.That(result.Entries).HasSingleItem();
            await Assert.That(result.Entries[0].Kind).IsEqualTo(MapDiffKind.Removed);
            await Assert.That(result.Entries[0].OldEntity).IsNotNull();
            await Assert.That(result.NewEntityCount).IsEqualTo(result.OldEntityCount - 1);
        }

        /// <summary>The compiler renumbers the ids of entities it takes from prefabs, which on its own is no change.</summary>
        [Test]
        public async Task RenumberedIdsAreNotDifferences()
        {
            var result = CompareWithEdit(RiverflowLump, lump =>
            {
                foreach (var entity in ((EntityLump)lump.DataBlock!).Data.GetArray("m_entityKeyValues"))
                {
                    SetKey(entity, "hammeruniqueid", GetKey(entity, "hammeruniqueid") + "000");
                }
            });

            await Assert.That(result.Entries).IsEmpty();
        }

        [Test]
        public async Task FloatNoiseIsNotADifference()
        {
            var result = CompareWithEdit(RiverflowLump, lump => SetKey(Entity(lump, "17"), "origin", "384.000031 -5760.000061 0.000002"));

            await Assert.That(result.Entries).IsEmpty();
        }

        [Test]
        public async Task MovedSceneObjectIsReportedAsMovedGeometry()
        {
            static KVObject Row(float x, float y, float z, float w) => KVObject.Array([new(x), new(y), new(z), new(w)]);

            // The fixture places it with no rotation at the origin, this moves it along X
            var result = CompareWithEdit(RiverflowNode, node =>
                ((WorldNode)node.DataBlock!).SceneObjects[0]["m_vTransform"] = KVObject.Array([Row(1, 0, 0, 96), Row(0, 1, 0, 0), Row(0, 0, 1, 0)]));

            await Assert.That(result.Entries).HasSingleItem();

            var entry = result.Entries[0];
            await Assert.That(entry.Kind).IsEqualTo(MapDiffKind.Moved);
            await Assert.That(entry.Category).IsEqualTo(MapDiffCategory.Geometry);
            await Assert.That(entry.Detail).IsEqualTo("moved 96 units");
            await Assert.That(entry.NewBounds!.Value.Center.X - entry.OldBounds!.Value.Center.X).IsEqualTo(96f).Within(0.01f);
        }

        [Test]
        public async Task RemovedSceneObjectIsReportedWhereItWas()
        {
            AABB? removedBounds = null;

            var result = CompareWithEdit(RiverflowNode, node =>
            {
                ((WorldNode)node.DataBlock!).Data["m_sceneObjects"].RemoveAt(0);
            });

            foreach (var entry in result.Entries)
            {
                removedBounds = removedBounds?.Union(entry.OldBounds!.Value) ?? entry.OldBounds;
            }

            await Assert.That(result.Entries).IsNotEmpty();
            await Assert.That(result.Entries.All(static entry => entry.Kind == MapDiffKind.Removed && entry.Category == MapDiffCategory.Geometry)).IsTrue();
            await Assert.That(removedBounds).IsNotNull();
        }
    }
}
