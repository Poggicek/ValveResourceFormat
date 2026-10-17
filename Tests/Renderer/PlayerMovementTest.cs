using System.Threading.Tasks;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Movement;

namespace Tests.Renderer
{
    /// <summary>
    /// Runs the movement headlessly, one user command per tick, on a flat floor at z = 0 made of a solid
    /// half-space, and checks it against values derived from the game's movement constants.
    /// </summary>
    public class PlayerMovementTest
    {
        private const float Tick = 1f / 64f;
        private const float KnifeSpeed = 250f;

        private sealed class Harness
        {
            public MovementVars Vars { get; } = new();
            public MovementCollision Collision { get; } = new();
            public MovementServices Services { get; }

            private int commandNumber;
            private InputButtons held;

            public Harness(Vector3 origin)
            {
                Collision.Planes.Add(new Vector4(Vector3.UnitZ, 0f));
                Services = new MovementServices(Vars, Collision) { WeaponMaxSpeed = KnifeSpeed };
                Services.Reset(origin, Vector3.Zero);
            }

            /// <summary>Runs one tick holding <paramref name="buttons"/>, with any edges at <paramref name="when"/>.</summary>
            public void Run(InputButtons buttons, float when = 0f, Vector3 viewAngles = default)
            {
                var cmd = new UserCmd
                {
                    CommandNumber = ++commandNumber,
                    ViewAngles = viewAngles,
                    ForwardMove = Axis(buttons, InputButtons.Forward, InputButtons.Back),
                    LeftMove = Axis(buttons, InputButtons.MoveLeft, InputButtons.MoveRight),
                };

                var state = new ButtonState { Held = (ulong)held };

                for (var bit = 1ul; bit != 0; bit <<= 1)
                {
                    var button = (InputButtons)bit;
                    var isDown = (buttons & button) != 0;

                    if (((held & button) != 0) != isDown)
                    {
                        state.AddButtonEdge(button, isDown);
                        cmd.SubtickMoves.Add(new SubtickMoveStep { Button = button, Pressed = isDown, When = when });
                    }
                }

                // The command reports what happened during the tick, relative to how it started
                cmd.Buttons = new ButtonState
                {
                    Held = (ulong)buttons,
                    Changed = state.Changed,
                    ChangedTwice = state.ChangedTwice,
                };

                held = buttons;
                Services.RunCommand(cmd);
            }

            public void Run(InputButtons buttons, int ticks)
            {
                for (var i = 0; i < ticks; i++)
                {
                    Run(buttons);
                }
            }

            private static float Axis(InputButtons buttons, InputButtons positive, InputButtons negative)
                => ((buttons & positive) != 0 ? 1f : 0f) - ((buttons & negative) != 0 ? 1f : 0f);
        }

        private static Harness Settled()
        {
            var harness = new Harness(new Vector3(0f, 0f, 1f));
            harness.Run(InputButtons.None, 64);
            return harness;
        }

        private static float Speed2D(Vector3 velocity) => MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));

        [Test]
        public async Task SettlesOnTheFloorOneSkinUp()
        {
            var harness = Settled();

            await Assert.That(harness.Services.OnGround).IsTrue();
            await Assert.That(harness.Services.AbsOrigin.Z).IsEqualTo(RubikonShapeCast.Skin).Within(1e-4f);
            await Assert.That(harness.Services.AbsVelocity).IsEqualTo(Vector3.Zero);
        }

        [Test]
        public async Task RunningReachesTheWeaponSpeed()
        {
            var harness = Settled();
            harness.Run(InputButtons.Forward, 128);

            var velocity = harness.Services.AbsVelocity;
            await Assert.That(Speed2D(velocity)).IsEqualTo(KnifeSpeed).Within(0.01f);
            await Assert.That(velocity.X).IsGreaterThan(0f);
            await Assert.That(MathF.Abs(velocity.Y)).IsLessThan(0.001f);
        }

        [Test]
        public async Task WalkingSettlesAtWalkSpeed()
        {
            var harness = Settled();
            harness.Run(InputButtons.Forward | InputButtons.Speed, 192);

            // 0.52 of the max speed, approached with fading acceleration
            await Assert.That(Speed2D(harness.Services.AbsVelocity)).IsEqualTo(KnifeSpeed * 0.51999998f).Within(1f);
        }

        [Test]
        public async Task ReleasingStopsWithFriction()
        {
            var harness = Settled();
            harness.Run(InputButtons.Forward, 128);
            harness.Run(InputButtons.None, 64);

            await Assert.That(Speed2D(harness.Services.AbsVelocity)).IsEqualTo(0f);
        }

        [Test]
        public async Task JumpHeightMatchesTheImpulse()
        {
            var harness = Settled();
            var startZ = harness.Services.AbsOrigin.Z;
            var peak = startZ;

            harness.Run(InputButtons.Jump);
            await Assert.That(harness.Services.Jumped).IsTrue();

            for (var i = 0; i < 128 && !harness.Services.OnGround; i++)
            {
                harness.Run(InputButtons.None);
                peak = MathF.Max(peak, harness.Services.AbsOrigin.Z);
            }

            // v^2 / 2g with the standing jump losing half a tick of gravity, which jump precision keeps exact
            var impulse = 301.99338f - (800f * 0.5f * 0.0078125f);
            var expected = impulse * impulse / (2f * 800f);

            await Assert.That(harness.Services.OnGround).IsTrue();
            await Assert.That(peak - startZ).IsEqualTo(expected).Within(1f / 32f);
        }

        [Test]
        public async Task SubtickPressMovesLater()
        {
            var early = Settled();
            var late = Settled();

            early.Run(InputButtons.Forward, when: 0f);
            late.Run(InputButtons.Forward, when: 0.5f);

            for (var i = 0; i < 128; i++)
            {
                early.Run(InputButtons.Forward);
                late.Run(InputButtons.Forward);
            }

            var earlyX = early.Services.AbsOrigin.X;
            var lateX = late.Services.AbsOrigin.X;

            // Pressing half a tick later costs about half a tick of full speed, once both are up to speed
            await Assert.That(earlyX - lateX).IsEqualTo(KnifeSpeed * Tick * 0.5f).Within(0.05f);
            await Assert.That(Speed2D(early.Services.AbsVelocity)).IsEqualTo(Speed2D(late.Services.AbsVelocity)).Within(0.01f);
        }

        [Test]
        public async Task DuckingTakesItsTimeThenShrinksTheHull()
        {
            var harness = Settled();

            harness.Run(InputButtons.Duck);
            await Assert.That(harness.Services.Ducked).IsFalse();
            await Assert.That(harness.Services.DuckAmount).IsGreaterThan(0f);

            // Pressing duck costs 2 of the duck speed of 8, and ducking runs at 0.8 of it: about 14 ticks
            harness.Run(InputButtons.Duck, 15);
            await Assert.That(harness.Services.Ducked).IsTrue();
            await Assert.That(harness.Services.DuckAmount).IsEqualTo(1f);
            await Assert.That(harness.Services.PlayerMaxs.Z).IsEqualTo(54f);
            await Assert.That(harness.Services.OnGround).IsTrue();
        }

        [Test]
        public async Task DuckedRunIsSlower()
        {
            var harness = Settled();
            harness.Run(InputButtons.Forward | InputButtons.Duck, 192);

            await Assert.That(Speed2D(harness.Services.AbsVelocity)).IsEqualTo(KnifeSpeed * 0.34f).Within(1f);
        }

        [Test]
        public async Task WalksUpAStep()
        {
            var harness = Settled();

            // A 16 unit step ahead
            harness.Collision.Hulls.Add(Box(new Vector3(64f, -256f, -64f), new Vector3(512f, 256f, 16f)));
            harness.Run(InputButtons.Forward, 64);

            await Assert.That(harness.Services.AbsOrigin.X).IsGreaterThan(64f);
            await Assert.That(harness.Services.AbsOrigin.Z).IsEqualTo(16f + RubikonShapeCast.Skin).Within(0.01f);
            await Assert.That(harness.Services.OnGround).IsTrue();
        }

        [Test]
        public async Task BlockedByAWallTooTallToStep()
        {
            var harness = Settled();
            harness.Collision.Hulls.Add(Box(new Vector3(64f, -256f, -64f), new Vector3(96f, 256f, 128f)));
            harness.Run(InputButtons.Forward, 64);

            // Stopped a skin short of the wall, with the hull 16 units either side of the origin
            await Assert.That(harness.Services.AbsOrigin.X).IsEqualTo(64f - 16f - RubikonShapeCast.Skin).Within(0.05f);
        }

        [Test]
        public async Task HeldJumpDoesNotBunnyHop()
        {
            var harness = Settled();

            harness.Run(InputButtons.Jump);
            var jumps = 1;

            for (var i = 0; i < 192; i++)
            {
                harness.Run(InputButtons.Jump);
                jumps += harness.Services.Jumped ? 1 : 0;
            }

            await Assert.That(jumps).IsEqualTo(1);
        }

        [Test]
        public async Task AutoBunnyHopJumpsOnLanding()
        {
            var harness = Settled();
            harness.Vars.AutoBunnyHopping = true;

            var jumps = 0;

            for (var i = 0; i < 192; i++)
            {
                harness.Run(InputButtons.Jump);
                jumps += harness.Services.Jumped ? 1 : 0;
            }

            // About 0.66 seconds per hop
            await Assert.That(jumps).IsGreaterThanOrEqualTo(4);
        }

        private static Rubikon.PhysicsHullData Box(Vector3 min, Vector3 max)
        {
            Vector3[] vertices =
            [
                new(min.X, min.Y, min.Z),
                new(max.X, min.Y, min.Z),
                new(min.X, max.Y, min.Z),
                new(max.X, max.Y, min.Z),
                new(min.X, min.Y, max.Z),
                new(max.X, min.Y, max.Z),
                new(min.X, max.Y, max.Z),
                new(max.X, max.Y, max.Z),
            ];

            return new Rubikon.PhysicsHullData([], [], min, max, vertices, [], [], []) { Centroid = (min + max) * 0.5f };
        }
    }
}
