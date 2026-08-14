using System.Threading.Tasks;
using NUnit.Framework;
using ValveResourceFormat.Renderer.Materials;

namespace Tests.Renderer
{
    [TestFixture]
    public class RenderModesTest
    {
        // Shader parsing runs on several threads: ShaderLoader starts a background pre-parse from its
        // static constructor that can race a foreground parse. The caller reads GetShaderId, sees zero,
        // and registers, so two threads can both decide to register the same render mode. Backing this
        // with a plain Dictionary and Add threw ArgumentException, unpredictably enough to survive
        // dozens of runs before showing up.
        [Test]
        public void AddShaderIdIsIdempotent()
        {
            RenderModes.AddShaderId("TestIdempotent", 7);

            Assert.DoesNotThrow(() => RenderModes.AddShaderId("TestIdempotent", 7));
            Assert.That(RenderModes.GetShaderId("TestIdempotent"), Is.EqualTo(7));
        }

        [Test]
        public void AddShaderIdSurvivesConcurrentRegistration()
        {
            const int Threads = 16;
            const int PerThread = 500;

            Assert.DoesNotThrowAsync(() => Parallel.ForAsync(0, Threads, (worker, _) =>
            {
                for (var i = 0; i < PerThread; i++)
                {
                    // Same value from every thread, as the real caller derives it from the render
                    // mode's fixed index in RenderModes.Items.
                    RenderModes.AddShaderId($"TestConcurrent{i}", (byte)(i % 256));
                    RenderModes.GetShaderId($"TestConcurrent{i}");
                }

                return ValueTask.CompletedTask;
            }));

            Assert.That(RenderModes.GetShaderId("TestConcurrent0"), Is.EqualTo(0));
            Assert.That(RenderModes.GetShaderId($"TestConcurrent{PerThread - 1}"), Is.EqualTo((byte)((PerThread - 1) % 256)));
        }

        [Test]
        public void GetShaderIdReturnsZeroWhenUnregistered()
        {
            Assert.That(RenderModes.GetShaderId("TestNeverRegistered"), Is.EqualTo(0));
        }
    }
}
