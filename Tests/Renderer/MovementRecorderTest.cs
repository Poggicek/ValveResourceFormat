using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Movement;

namespace Tests.Renderer
{
    /// <summary>
    /// Surfs a steep ramp made of a half-space next to a hull wall, records it, and checks the export has
    /// what a ramp bug needs to be understood, and replays to the bit.
    /// </summary>
    public class MovementRecorderTest
    {
        // A 53 degree ramp facing -X through the origin, too steep to stand on
        private static readonly Vector4 Ramp = new(-0.8f, 0f, 0.6f, 0f);

        // A wall across the ramp ahead of the player
        private static readonly Rubikon.PhysicsHullData Wall = new([], [], new Vector3(-4096f, 1024f, -4096f), new Vector3(4096f, 1088f, 4096f),
            [
                new(-4096f, 1024f, -4096f), new(4096f, 1024f, -4096f), new(-4096f, 1088f, -4096f), new(4096f, 1088f, -4096f),
                new(-4096f, 1024f, 4096f), new(4096f, 1024f, 4096f), new(-4096f, 1088f, 4096f), new(4096f, 1088f, 4096f),
            ], [], [], [])
        {
            Centroid = new Vector3(0f, 1056f, 0f),
        };

        private static (MovementServices Services, MovementRecorder Recorder) Surf(int ticks, int capacity = MovementRecorder.DefaultCapacity)
        {
            var vars = new MovementVars { AirAccelerate = MovementVars.AirAccelerateMovementMaps };
            var collision = new MovementCollision();
            collision.Planes.Add(Ramp);
            collision.Hulls.Add(Wall);

            var recorder = new MovementRecorder(vars, capacity);
            var services = new MovementServices(vars, collision) { Recorder = recorder, WeaponMaxSpeed = 250f };
            services.Reset(new Vector3(200f, -600f, 500f), Vector3.Zero);
            services.AbsVelocity = new Vector3(0f, 1500f, 0f);

            var held = InputButtons.None;

            for (var i = 0; i < ticks; i++)
            {
                // Strafe into the ramp, switching sides now and then with the edge somewhere inside the tick
                var buttons = (i / 24 % 2 == 0 ? InputButtons.MoveLeft : InputButtons.MoveRight) | (i % 50 == 40 ? InputButtons.Jump : 0);
                var when = (i % 7) / 7f;
                var cmd = new UserCmd
                {
                    CommandNumber = i + 1,
                    ViewAngles = new Vector3(10f, 90f + (MathF.Sin(i * 0.1f) * 20f), 0f),
                    LeftMove = (buttons & InputButtons.MoveLeft) != 0 ? 1f : -1f,
                };

                var state = new ButtonState { Held = (ulong)held };

                for (var bit = 1ul; bit != 0; bit <<= 1)
                {
                    var button = (InputButtons)bit;
                    var isDown = (buttons & button) != 0;

                    if (((held & button) != 0) != isDown)
                    {
                        state.AddButtonEdge(button, isDown);
                        cmd.SubtickMoves.Add(new SubtickMoveStep { Button = button, Pressed = isDown, When = when, YawDelta = -0.5f });
                    }
                }

                cmd.Buttons = new ButtonState { Held = (ulong)buttons, Changed = state.Changed, ChangedTwice = state.ChangedTwice };
                held = buttons;

                services.RunCommand(cmd);
            }

            return (services, recorder);
        }

        private static MemoryStream Export(MovementRecorder recorder)
        {
            var stream = new MemoryStream();
            recorder.Export(stream, "surf_test");
            stream.Position = 0;
            return stream;
        }

        [Test]
        public async Task KeepsOnlyTheLastTicks()
        {
            var (_, recorder) = Surf(25, capacity: 10);

            using var stream = Export(recorder);
            using var document = JsonDocument.Parse(stream.ToArray());
            var ticks = document.RootElement.GetProperty("ticks").EnumerateArray().Select(static tick => tick.GetProperty("tick").GetInt32()).ToList();

            await Assert.That(recorder.TickCount).IsEqualTo(10);
            await Assert.That(ticks).IsEquivalentTo(Enumerable.Range(16, 10), EqualityComparer<int>.Default, CollectionOrdering.Matching);
        }

        [Test]
        public async Task RecordsTheSurfInDetail()
        {
            var (_, recorder) = Surf(192);

            using var stream = Export(recorder);
            using var document = JsonDocument.Parse(stream.ToArray());
            var root = document.RootElement;
            var events = root.GetProperty("ticks").EnumerateArray().SelectMany(static tick => tick.GetProperty("events").EnumerateArray()).ToList();

            string Type(JsonElement e) => e.GetProperty("type").GetString()!;

            var traces = events.Where(e => Type(e) == "trace").ToList();
            var clips = events.Where(e => Type(e) == "clip").ToList();

            await Assert.That(root.GetProperty("map").GetString()).IsEqualTo("surf_test");
            await Assert.That(root.GetProperty("vars").GetProperty("sv_airaccelerate").GetSingle()).IsEqualTo(MovementVars.AirAccelerateMovementMaps);
            await Assert.That(events.Count(e => Type(e) == "slice")).IsEqualTo(events.Count(e => Type(e) == "sliceEnd"));

            // Sliding along the ramp reflects off it while airborne
            await Assert.That(clips.Any(static clip => clip.GetProperty("outcome").GetString() == nameof(ClipOutcome.Reflected))).IsTrue();
            await Assert.That(traces.Any(static trace => trace.TryGetProperty("shape", out var shape) && shape.GetProperty("kind").GetString() == "plane"
                && MathF.Abs(trace.GetProperty("normal")[2].GetSingle() - 0.6f) < 1e-4f)).IsTrue();

            // The wall at the end of the ramp is a hull, kept with its vertices
            var hullHit = traces.FirstOrDefault(static trace => trace.TryGetProperty("shape", out var shape) && shape.GetProperty("kind").GetString() == "hull");
            await Assert.That(hullHit.ValueKind).IsEqualTo(JsonValueKind.Object);
            await Assert.That(hullHit.GetProperty("shape").GetProperty("vertices").GetArrayLength()).IsEqualTo(8);
            await Assert.That(traces.All(static trace => trace.GetProperty("caller").GetString()!.Length > 0)).IsTrue();
        }

        [Test]
        public async Task ReplaysExactlyFromAnyTick()
        {
            var (services, recorder) = Surf(256);

            using var stream = Export(recorder);
            var recording = MovementRecording.Load(stream);

            var fromStart = new MovementReplay(recording, null, null, null);
            fromStart.Collision.Planes.Add(Ramp);
            fromStart.Collision.Hulls.Add(Wall);

            await Assert.That(recording.Ticks.Count).IsEqualTo(256);
            await Assert.That(fromStart.Run()).IsEqualTo(-1);
            await Assert.That(fromStart.Services.AbsOrigin).IsEqualTo(services.AbsOrigin);

            var fromMiddle = new MovementReplay(recording, null, null, null);
            fromMiddle.Collision.Planes.Add(Ramp);
            fromMiddle.Collision.Hulls.Add(Wall);

            await Assert.That(fromMiddle.Run(start: 137)).IsEqualTo(-1);
            await Assert.That(fromMiddle.Recorder.TickCount).IsEqualTo(256 - 137);
        }

        [Test]
        public async Task ReplayFindsWhereTheWorldDiffers()
        {
            var (_, recorder) = Surf(192);

            using var stream = Export(recorder);
            var recording = MovementRecording.Load(stream);

            // Without the wall the run goes elsewhere once it would have touched it
            var replay = new MovementReplay(recording, null, null, null);
            replay.Collision.Planes.Add(Ramp);

            await Assert.That(replay.Run()).IsGreaterThan(0);
        }
    }
}
