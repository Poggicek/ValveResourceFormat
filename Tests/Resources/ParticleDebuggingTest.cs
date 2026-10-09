using System.Linq;
using System.Threading.Tasks;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Particles;
using ValveResourceFormat.Particles.Debugging;
using ValveResourceFormat.ResourceTypes;

namespace Tests.Resources
{
    public class ParticleDebuggingTest
    {
        private const float StepTime = 1f / 30f;

        private static ParticleSystemSimulation Simulate(Resource resource, string name, ParticleDebugSession session)
        {
            resource.Read(TestFixtures.Path(name));

            var simulation = new ParticleSystemSimulation((ParticleSystem)resource.DataBlock!, new NullFileLoader());
            simulation.AttachDebugSession(session);

            return simulation;
        }

        /// <summary>Steps until <paramref name="done"/> holds, since an emitter may wait a few steps before spawning.</summary>
        private static void RunUntil(ParticleSystemSimulation simulation, Func<bool> done)
        {
            for (var i = 0; i < 60 && !done(); i++)
            {
                simulation.Update(StepTime, i * StepTime);
            }
        }

        [Test]
        public async Task TracesEveryFunctionInPipelineOrder()
        {
            using var resource = new Resource();
            var simulation = Simulate(resource, "vent_impact_dust_02.vpcf_c", new ParticleDebugSession());

            simulation.Update(StepTime, 0f);

            var trace = simulation.Trace!;
            var steps = trace.Steps;
            var traced = new[] { ParticleFunctionStage.PreEmissionOperator, ParticleFunctionStage.Emitter, ParticleFunctionStage.Operator, ParticleFunctionStage.Constraint };
            var expected = simulation.DebugFunctions.Where(function => function.CanBypass && traced.Contains(function.Stage)).ToList();
            var stages = steps.Where(static step => step.Function != null).Select(static step => step.Function!.Stage).ToList();

            using (Assert.Multiple())
            {
                await Assert.That(trace.StepNumber).IsEqualTo(1);
                await Assert.That(steps[0].Label).IsEqualTo("Step start");
                await Assert.That(steps[^1].Label).IsEqualTo("Remove expired");
                await Assert.That(expected.All(function => steps.Any(step => step.Function == function))).IsTrue();
                await Assert.That(stages.SequenceEqual(stages.Order())).IsTrue();
                await Assert.That(steps[^1].Count).IsEqualTo(simulation.Particles.Count);
            }
        }

        [Test]
        public async Task KeepsHowEachParticleWasInitialized()
        {
            using var resource = new Resource();
            var simulation = Simulate(resource, "vent_impact_dust_02.vpcf_c", new ParticleDebugSession());

            RunUntil(simulation, () => simulation.Particles.Count > 0);

            var initializers = simulation.DebugFunctions.Count(static function => function is { Stage: ParticleFunctionStage.Initializer, CanBypass: true });
            var particle = simulation.Particles.Current[0];

            await Assert.That(simulation.Trace!.TryGetSpawnTrace(particle.UniqueParticleId, out var spawn)).IsTrue();

            using (Assert.Multiple())
            {
                await Assert.That(spawn!.Stages.Count).IsEqualTo(initializers + 1);
                await Assert.That(spawn.Stages[0].Initializer).IsNull();
                await Assert.That(spawn.Stages[^1].State.Lifetime).IsEqualTo(simulation.Particles.Initial[0].Lifetime);
                await Assert.That(simulation.Trace.Steps.Any(static step => step.Label == "Constants")).IsTrue();
            }
        }

        [Test]
        public async Task BypassedFunctionsAreSkipped()
        {
            using var resource = new Resource();
            var simulation = Simulate(resource, "vent_impact_dust_02.vpcf_c", new ParticleDebugSession());
            var operators = simulation.DebugFunctions.Where(static function => function is { Stage: ParticleFunctionStage.Operator, CanBypass: true }).ToList();

            foreach (var function in operators)
            {
                function.Bypassed = true;
            }

            simulation.Update(StepTime, 0f);

            var operatorSteps = simulation.Trace!.Steps.Where(step => operators.Contains(step.Function!)).ToList();

            using (Assert.Multiple())
            {
                await Assert.That(operatorSteps.Count).IsEqualTo(operators.Count);
                await Assert.That(operatorSteps.All(static step => step.Skipped && step.ChangedAttributes == 0)).IsTrue();
            }
        }

        [Test]
        public async Task BreaksOnTheFirstFunctionToIntroduceAnAnomaly()
        {
            using var resource = new Resource();
            var session = new ParticleDebugSession { BreakOn = ParticleAnomalies.NonFinite };
            var simulation = Simulate(resource, "vent_impact_dust_02.vpcf_c", session);

            // Particles spawn on control point 0, so a NaN there shows up before any initializer runs
            simulation.MainControlPoint.Position = new Vector3(float.NaN);
            RunUntil(simulation, () => session.Break != null);

            var stepNumber = simulation.Trace!.StepNumber;
            simulation.Update(StepTime, 0f);

            using (Assert.Multiple())
            {
                await Assert.That(session.Break).IsNotNull();
                await Assert.That(session.Break!.Step.Label).IsEqualTo("Constants");
                await Assert.That(session.Break.Anomalies).IsEqualTo(ParticleAnomalies.NonFinite);

                // The trace holds on to the step that broke
                await Assert.That(simulation.Trace.StepNumber).IsEqualTo(stepNumber);
            }

            session.Continue();
            simulation.Update(StepTime, 0f);

            await Assert.That(simulation.Trace.StepNumber).IsEqualTo(stepNumber + 1);
        }

        [Test]
        public async Task AttributesChangesToTheFunctionThatMadeThem()
        {
            using var resource = new Resource();
            var simulation = Simulate(resource, "vent_impact_dust_02.vpcf_c", new ParticleDebugSession());

            RunUntil(simulation, () => simulation.Particles.Count > 0);
            simulation.Update(StepTime, 0f);

            var steps = simulation.Trace!.Steps;
            var position = 1UL << ParticleAttribute.All.Single(static attribute => attribute.Name == "Position").Index;
            var movement = steps.Single(static step => step.Function?.ClassName == "C_OP_BasicMovement");

            using (Assert.Multiple())
            {
                await Assert.That(movement.ChangedAttributes & position).IsNotEqualTo(0UL);
                await Assert.That(steps[0].ChangedAttributes).IsEqualTo(0UL);
                await Assert.That(ParticleAttribute.All.Count).IsLessThanOrEqualTo(64);
            }
        }

        [Test]
        public async Task DiagnosesNonFiniteParticles()
        {
            using var resource = new Resource();
            var simulation = Simulate(resource, "vent_impact_dust_02.vpcf_c", new ParticleDebugSession());

            simulation.MainControlPoint.Position = new Vector3(float.NaN);
            RunUntil(simulation, () => simulation.Particles.Count > 0);

            var diagnostics = ParticleDiagnostics.Collect(simulation);

            using (Assert.Multiple())
            {
                await Assert.That(diagnostics[0].Severity).IsEqualTo(ParticleDiagnosticSeverity.Error);
                await Assert.That(diagnostics.Any(static diagnostic => diagnostic.Message.Contains("NaN", StringComparison.Ordinal))).IsTrue();
            }
        }
    }
}
