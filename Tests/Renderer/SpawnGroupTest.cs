using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Entities;
using ValveResourceFormat.Renderer.World;
using ValveResourceFormat.ResourceTypes;

namespace Tests.Renderer
{
    /// <summary>
    /// Maps streamed in and out as spawn groups: queued by entity I/O, loaded and unloaded at the frame
    /// boundary rather than mid-tick, taking their entities with them, and found by the names maps refer to
    /// them and their entities by.
    /// </summary>
    public class SpawnGroupTest
    {
        private const string RecorderClass = "vrf_test_spawngroup_recorder";

        private static readonly string?[] BothConnectionsFired = ["unmarked", "marked"];

        private readonly List<IDisposable> harnessContexts = [];

        [After(HookType.Test)]
        public void DisposeHarnessContexts()
        {
            for (var i = harnessContexts.Count - 1; i >= 0; i--)
            {
                harnessContexts[i].Dispose();
            }

            harnessContexts.Clear();
        }

        /// <summary>Records the parameter of every <c>Record</c> input it is sent, and draws nothing.</summary>
        private sealed class RecorderEntity(EntitySystem system, EntitySpawnInfo spawnInfo) : BaseEntity(system, spawnInfo)
        {
            public List<string?> Received { get; } = [];

            protected override SceneNode? CreateRootNode() => null;

            [EntityInput("Record")]
            private void InputRecord(EntityInputData data) => Received.Add(data.Parameter);
        }

        private (EntitySystem EntitySystem, Scene Scene) CreateWorld()
        {
            EntityFactory.Register<RecorderEntity>(RecorderClass, static (system, spawnInfo) => new RecorderEntity(system, spawnInfo));

            var fileLoader = new GameFileLoader(null, null);
            harnessContexts.Add(fileLoader);

            var context = new RendererContext(fileLoader, NullLogger.Instance);
            harnessContexts.Add(context);

            var scene = new Scene(context);
            harnessContexts.Add(scene);

            return (new EntitySystem(context), scene);
        }

        private static EntityLump.Entity MakeEntity(string targetName, params (string Output, string Target, string Parameter)[] connections)
        {
            var entity = new EntityLump.Entity { ParentLump = new EntityLump { Resource = new Resource() } };

            entity.Add("classname", RecorderClass);
            entity.Add("targetname", targetName);

            if (connections.Length > 0)
            {
                entity.Connections = [.. connections.Select(connection => new EntityLump.Connection
                {
                    SourceEntity = entity,
                    OutputName = connection.Output,
                    TargetName = connection.Target,
                    InputName = "Record",
                    OverrideParam = connection.Parameter,
                    Delay = 0f,
                    TimesToFire = -1,
                    TargetType = EntityIOTargetType.EntityNameOrClassName,
                })];
            }

            return entity;
        }

        private static RecorderEntity Spawn(EntitySystem entitySystem, Scene scene, string targetName, params (string Output, string Target, string Parameter)[] connections)
            => (RecorderEntity)entitySystem.CreateEntity(MakeEntity(targetName, connections), Matrix4x4.Identity, null, scene)!;

        /// <summary>A loader standing in for a map: it spawns one recorder into the spawn group.</summary>
        private static SpawnGroupLoader SpawnRecorder(EntitySystem entitySystem, string targetName)
            => spawnGroup => entitySystem.CreateEntity(MakeEntity(targetName), Matrix4x4.Identity, null, spawnGroup) != null;

        [Test]
        public async Task NormalizesMapNamesTheWaysMapsReferToThem()
        {
            await Assert.That(SpawnGroupManager.NormalizeMapName("stages/stage1")).IsEqualTo("stages/stage1");
            await Assert.That(SpawnGroupManager.NormalizeMapName("maps/stages/stage1.vmap")).IsEqualTo("stages/stage1");
            await Assert.That(SpawnGroupManager.NormalizeMapName("maps\\stages\\stage1.vmap_c")).IsEqualTo("stages/stage1");
            await Assert.That(SpawnGroupManager.NormalizeMapName("maps/stages/stage1.vpk")).IsEqualTo("stages/stage1");
        }

        [Test]
        public async Task LoadsAtTheFrameBoundaryNotWhenRequested()
        {
            var (entitySystem, scene) = CreateWorld();
            Spawn(entitySystem, scene, "anchor");

            var loads = 0;
            entitySystem.SpawnGroups.Loader = spawnGroup =>
            {
                loads++;
                return SpawnRecorder(entitySystem, "stage_relay")(spawnGroup);
            };

            var spawnGroup = entitySystem.SpawnGroups.RequestLoad("maps/stages/stage1.vmap", null, null, Vector3.Zero);

            await Assert.That(spawnGroup).IsNotNull();
            await Assert.That(spawnGroup!.State).IsEqualTo(SpawnGroupState.Queued);
            await Assert.That(loads).IsEqualTo(0);

            entitySystem.Update(EntitySystem.TickInterval);

            await Assert.That(loads).IsEqualTo(1);
            await Assert.That(spawnGroup.State).IsEqualTo(SpawnGroupState.Loaded);
            await Assert.That(spawnGroup.WorldGroup).IsEqualTo(entitySystem.SpawnGroups.MainWorldGroup);
            await Assert.That(entitySystem.SpawnGroups.Find("stages/stage1")).IsEqualTo(spawnGroup);

            var spawned = entitySystem.FindAllByTargetName("stage_relay").Single();

            await Assert.That(spawned.SpawnGroup).IsEqualTo(spawnGroup);
            await Assert.That(spawned.Scene).IsEqualTo(spawnGroup.Scene);
        }

        [Test]
        public async Task RefusesAMapThatIsAlreadyLoadedOrOnItsWay()
        {
            var (entitySystem, scene) = CreateWorld();
            Spawn(entitySystem, scene, "anchor");
            entitySystem.SpawnGroups.Loader = SpawnRecorder(entitySystem, "stage_relay");

            var first = entitySystem.SpawnGroups.RequestLoad("stages/stage1", null, null, Vector3.Zero);

            await Assert.That(first).IsNotNull();
            await Assert.That(entitySystem.SpawnGroups.RequestLoad("maps/stages/stage1.vmap", null, null, Vector3.Zero)).IsNull();

            entitySystem.Update(EntitySystem.TickInterval);

            await Assert.That(entitySystem.SpawnGroups.RequestLoad("stages/stage1", null, null, Vector3.Zero)).IsNull();
        }

        [Test]
        public async Task UnloadingTakesTheEntitiesAndTheMapsItStreamedIn()
        {
            var (entitySystem, scene) = CreateWorld();
            var anchor = Spawn(entitySystem, scene, "anchor");
            var manager = entitySystem.SpawnGroups;

            manager.Loader = SpawnRecorder(entitySystem, "stage_relay");
            var stage = manager.RequestLoad("stages/stage1", null, null, Vector3.Zero)!;
            entitySystem.Update(EntitySystem.TickInterval);

            manager.Loader = SpawnRecorder(entitySystem, "nested_relay");
            var nested = manager.RequestLoad("stages/stage1_extra", stage, null, Vector3.Zero)!;
            entitySystem.Update(EntitySystem.TickInterval);

            var stageEntity = entitySystem.FindAllByTargetName("stage_relay").Single();
            var nestedEntity = entitySystem.FindAllByTargetName("nested_relay").Single();

            await Assert.That(nested.Parent).IsEqualTo(stage);
            await Assert.That(manager.RequestUnload(stage)).IsTrue();

            // Queued, so the entities stay until the frame boundary
            await Assert.That(stage.IsLoaded).IsTrue();
            await Assert.That(stageEntity.IsRemoved).IsFalse();

            entitySystem.Update(EntitySystem.TickInterval);

            await Assert.That(stage.State).IsEqualTo(SpawnGroupState.Unloaded);
            await Assert.That(nested.State).IsEqualTo(SpawnGroupState.Unloaded);
            await Assert.That(stageEntity.IsRemoved).IsTrue();
            await Assert.That(nestedEntity.IsRemoved).IsTrue();
            await Assert.That(anchor.IsRemoved).IsFalse();
            await Assert.That(manager.SpawnGroups.Count).IsEqualTo(0);
            await Assert.That(manager.MainWorldGroup.SpawnGroups.Count).IsEqualTo(0);

            // Gone for good, so the same map can be streamed in again
            manager.Loader = SpawnRecorder(entitySystem, "stage_relay");

            await Assert.That(manager.RequestLoad("stages/stage1", null, null, Vector3.Zero)).IsNotNull();
        }

        [Test]
        public async Task AMapThatFailsToLoadLeavesNothingBehind()
        {
            var (entitySystem, scene) = CreateWorld();
            Spawn(entitySystem, scene, "anchor");

            entitySystem.SpawnGroups.Loader = spawnGroup =>
            {
                entitySystem.CreateEntity(MakeEntity("half_spawned"), Matrix4x4.Identity, null, spawnGroup);
                throw new InvalidDataException("broken map");
            };

            var spawnGroup = entitySystem.SpawnGroups.RequestLoad("stages/broken", null, null, Vector3.Zero)!;

            entitySystem.Update(EntitySystem.TickInterval);

            await Assert.That(spawnGroup.State).IsEqualTo(SpawnGroupState.Unloaded);
            await Assert.That(entitySystem.SpawnGroups.Find("stages/broken")).IsNull();
            await Assert.That(entitySystem.FindAllByTargetName("half_spawned").Any()).IsFalse();
        }

        [Test]
        public async Task KeepsTheLandmarkItWasAskedToLineUpWith()
        {
            var (entitySystem, _) = CreateWorld();
            var landmarkOrigin = new Vector3(128f, -64f, 32f);

            var spawnGroup = entitySystem.SpawnGroups.RequestLoad("stages/stage1", null, "base_landmark", landmarkOrigin)!;

            await Assert.That(spawnGroup.Landmark?.Name).IsEqualTo("base_landmark");
            await Assert.That(spawnGroup.Landmark?.Origin).IsEqualTo(landmarkOrigin);
            await Assert.That(entitySystem.SpawnGroups.RequestLoad("stages/stage2", null, null, landmarkOrigin)!.Landmark).IsNull();
        }

        [Test]
        public async Task NamesAnswerOnceThePrefabMarkerIsFixedUp()
        {
            var (entitySystem, scene) = CreateWorld();
            var target = Spawn(entitySystem, scene, "[PR#]stage_relay");

            // A keyvalue that names an entity carries no marker, while the entity itself does
            await Assert.That(entitySystem.FindAllByTargetName("stage_relay").Single()).IsEqualTo(target);
            await Assert.That(entitySystem.FindAllByTargetName("[PR#]stage_relay").Single()).IsEqualTo(target);
            await Assert.That(target.Name).IsEqualTo("stage_relay");
            await Assert.That(target.TargetName).IsEqualTo("[PR#]stage_relay");

            var source = Spawn(entitySystem, scene, "[PR#]source",
                ("OnUser1", "stage_relay", "unmarked"),
                ("OnUser1", "[PR#]stage_relay", "marked"));

            entitySystem.TriggerOutput(source, "OnUser1");
            entitySystem.Update(EntitySystem.TickInterval);

            await Assert.That(target.Received).IsEquivalentTo(BothConnectionsFired);
        }
    }
}
