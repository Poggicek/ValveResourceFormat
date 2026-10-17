using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Input;

namespace Tests.Renderer
{
    /// <summary>
    /// Drives the real <see cref="UserInput"/> and <see cref="PlayerMovement"/> frame by frame, as the
    /// viewer does, on a flat floor at z = 0.
    /// </summary>
    public class PlayerMovementInputTest
    {
        private readonly List<IDisposable> Disposables = [];

        [After(HookType.Test)]
        public void DisposeContexts()
        {
            for (var i = Disposables.Count - 1; i >= 0; i--)
            {
                Disposables[i].Dispose();
            }

            Disposables.Clear();
        }

        private (UserInput Input, Camera RenderCamera) CreateWalkingInput(float fps)
        {
            var fileLoader = new GameFileLoader(null, null);
            Disposables.Add(fileLoader);

            var context = new RendererContext(fileLoader, NullLogger.Instance);
            Disposables.Add(context);

            var input = new UserInput(new ValveResourceFormat.Renderer.Renderer(context));
            var renderCamera = new Camera();

            input.PlayerMovement.DebugCollisionPlanes.Add(new Vector4(Vector3.UnitZ, 0f));
            input.Camera.Location = new Vector3(0f, 0f, 70f);
            input.Camera.Yaw = 0f;
            input.Camera.Pitch = 0f;

            // Enter walk mode and land
            input.Tick(1f / fps, TrackedKeys.X, Vector2.Zero, renderCamera);
            Run(input, renderCamera, fps, TrackedKeys.None, 1f);

            return (input, renderCamera);
        }

        private static void Run(UserInput input, Camera renderCamera, float fps, TrackedKeys keys, float seconds)
        {
            for (var i = 0; i < (int)(seconds * fps); i++)
            {
                input.Tick(1f / fps, keys, Vector2.Zero, renderCamera);
            }
        }

        private static float Speed2D(Vector3 v) => MathF.Sqrt((v.X * v.X) + (v.Y * v.Y));

        [Test]
        [Arguments(30f)]
        [Arguments(64f)]
        [Arguments(144f)]
        [Arguments(1000f)]
        public async Task RunsAtTheSameSpeedAtAnyFrameRate(float fps)
        {
            var (input, renderCamera) = CreateWalkingInput(fps);

            await Assert.That(input.PlayerMovement.OnGround).IsTrue();

            Run(input, renderCamera, fps, TrackedKeys.W, 2f);

            // No weapon loaded: the unarmed max speed
            await Assert.That(Speed2D(input.PlayerMovement.Velocity)).IsEqualTo(260f).Within(0.01f);
        }

        [Test]
        public async Task CameraSitsAtEyeHeight()
        {
            var (input, _) = CreateWalkingInput(144f);

            var eye = input.PlayerMovement.EyePosition;
            await Assert.That(eye.Z - input.PlayerMovement.RenderPosition.Z).IsEqualTo(64f);
            await Assert.That(input.Camera.Location).IsEqualTo(eye);
        }

        [Test]
        public async Task MouseWheelJumps()
        {
            var (input, renderCamera) = CreateWalkingInput(144f);

            input.Tick(1f / 144f, TrackedKeys.MouseWheelDown, Vector2.Zero, renderCamera);

            var jumped = false;

            for (var i = 0; i < 16 && !jumped; i++)
            {
                input.Tick(1f / 144f, TrackedKeys.None, Vector2.Zero, renderCamera);
                jumped = input.PlayerMovement.Jumped;
            }

            await Assert.That(jumped).IsTrue();
        }

        [Test]
        public async Task SpaceJumpsOncePerPress()
        {
            var (input, renderCamera) = CreateWalkingInput(144f);

            var jumps = 0;

            for (var i = 0; i < 144 * 2; i++)
            {
                input.Tick(1f / 144f, TrackedKeys.Space, Vector2.Zero, renderCamera);
                jumps += input.PlayerMovement.Jumped ? 1 : 0;
            }

            await Assert.That(jumps).IsEqualTo(1);
        }

        [Test]
        public async Task LandingDipsTheEyeAndRecovers()
        {
            var (input, renderCamera) = CreateWalkingInput(144f);
            var movement = input.PlayerMovement;

            // Fall from 128 units up, which lands at about 450 units per second
            movement.Teleport(movement.Position + new Vector3(0f, 0f, 128f), null);

            var lowest = float.MaxValue;

            for (var i = 0; i < 144 * 2; i++)
            {
                input.Tick(1f / 144f, TrackedKeys.None, Vector2.Zero, renderCamera);

                if (movement.OnGround)
                {
                    lowest = MathF.Min(lowest, movement.EyePosition.Z - movement.RenderPosition.Z);
                }
            }

            await Assert.That(lowest).IsEqualTo(64f - 3f).Within(0.05f);
            await Assert.That(movement.EyePosition.Z - movement.RenderPosition.Z).IsEqualTo(64f);
        }

        [Test]
        public async Task DrawsBetweenTheLastTwoTicks()
        {
            var (input, renderCamera) = CreateWalkingInput(256f);
            var movement = input.PlayerMovement;

            Run(input, renderCamera, 256f, TrackedKeys.W, 1f);

            // At top speed the drawn position trails the newest tick by up to one tick of travel
            var behind = movement.Position.X - movement.RenderPosition.X;

            await Assert.That(behind).IsGreaterThan(0f);
            await Assert.That(behind).IsLessThanOrEqualTo(260f / 64f);
        }
    }
}
