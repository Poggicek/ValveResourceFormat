using System.Globalization;
using System.Linq;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Particles.Debugging
{
    /// <summary>How much a <see cref="ParticleDiagnostic"/> matters.</summary>
    public enum ParticleDiagnosticSeverity
    {
        /// <summary>Explains what the system is doing, which may be intended.</summary>
        Info,

        /// <summary>Likely makes the effect look different from the game.</summary>
        Warning,

        /// <summary>Certainly makes the effect look different from the game.</summary>
        Error,
    }

    /// <summary>A finding about why a particle system may not look the way it should.</summary>
    /// <param name="System">The system it is about.</param>
    /// <param name="Severity">How much it matters.</param>
    /// <param name="Message">What was found.</param>
    /// <param name="Function">The function it is about, if any.</param>
    public sealed record ParticleDiagnostic(ParticleSystemSimulation System, ParticleDiagnosticSeverity Severity, string Message, ParticleDebugFunction? Function = null);

    /// <summary>Looks through a particle system for the usual reasons an effect breaks.</summary>
    public static class ParticleDiagnostics
    {
        private static readonly (ParticleAnomalies Anomaly, string Description)[] AnomalyDescriptions =
        [
            (ParticleAnomalies.NonFinite, "NaN or infinite attributes"),
            (ParticleAnomalies.FarAway, "positions absurdly far from control point 0"),
            (ParticleAnomalies.HugeRadius, "a huge radius"),
            (ParticleAnomalies.NoLifetime, "no lifetime"),
            (ParticleAnomalies.ZeroRadius, "zero radius"),
            (ParticleAnomalies.Transparent, "zero alpha"),
            (ParticleAnomalies.Black, "black color"),
        ];

        /// <summary>Collects findings for <paramref name="root"/> and every system under it, most severe first.</summary>
        /// <param name="root">The system to look through.</param>
        public static List<ParticleDiagnostic> Collect(ParticleSystemSimulation root)
        {
            var diagnostics = new List<ParticleDiagnostic>();
            Collect(root, diagnostics);

            // Stable, so each severity keeps the order systems and functions were visited in
            return [.. diagnostics.OrderByDescending(static diagnostic => diagnostic.Severity)];
        }

        /// <summary>Names the anomalies set in <paramref name="anomalies"/>.</summary>
        public static string Describe(ParticleAnomalies anomalies)
        {
            var names = new List<string>();

            foreach (var (anomaly, description) in AnomalyDescriptions)
            {
                if ((anomalies & anomaly) != 0)
                {
                    names.Add(description);
                }
            }

            return string.Join(", ", names);
        }

        private static void Collect(ParticleSystemSimulation system, List<ParticleDiagnostic> diagnostics)
        {
            void Add(ParticleDiagnosticSeverity severity, string message, ParticleDebugFunction? function = null)
                => diagnostics.Add(new ParticleDiagnostic(system, severity, message, function));

            foreach (var skipped in system.SkippedChildren)
            {
                Add(ParticleDiagnosticSeverity.Error, $"Child system skipped, {skipped}");
            }

            CheckFunctions(system, Add);
            CheckEmission(system, Add);
            CheckParticles(system, Add);
            CheckTrace(system, Add);

            foreach (var child in system.Children)
            {
                Collect(child, diagnostics);
            }
        }

        private static void CheckFunctions(ParticleSystemSimulation system, Action<ParticleDiagnosticSeverity, string, ParticleDebugFunction?> add)
        {
            foreach (var function in system.DebugFunctions)
            {
                if (function.Status == ParticleFunctionStatus.Unsupported)
                {
                    add(ParticleDiagnosticSeverity.Error, $"{function.ClassName} {function.Note ?? "is not implemented"}", function);
                }

                if (function.Bypassed)
                {
                    add(ParticleDiagnosticSeverity.Warning, $"{function.ClassName} is bypassed in the debugger", function);
                }

                foreach (var warning in function.Warnings)
                {
                    add(ParticleDiagnosticSeverity.Warning, $"{function.ClassName}: {warning}", function);
                }
            }

            if (system.Children.Count == 0 && system.Definition.GetArray("m_Renderers") is not { Count: > 0 })
            {
                add(ParticleDiagnosticSeverity.Warning, "Has no renderers and no children, so it draws nothing", null);
            }
        }

        private static void CheckEmission(ParticleSystemSimulation system, Action<ParticleDiagnosticSeverity, string, ParticleDebugFunction?> add)
        {
            if (system.DormantReason is { } reason)
            {
                add(ParticleDiagnosticSeverity.Info, $"Not running: {reason}", null);
            }

            if (system.IsFrozen)
            {
                add(ParticleDiagnosticSeverity.Info, "Frozen, by an endcap freeze or m_flStopSimulationAfterTime", null);
            }

            var emitsAnything = system.DebugFunctions.Any(static function => function is { Stage: ParticleFunctionStage.Emitter, Status: ParticleFunctionStatus.Active })
                || system.InitialParticles > 0;

            if (!emitsAnything)
            {
                add(ParticleDiagnosticSeverity.Warning, "Has no working emitter and no initial particles, so it never spawns a particle", null);
            }

            var count = system.Particles.Count;

            if (count > 0 && count >= system.MaxParticles)
            {
                add(ParticleDiagnosticSeverity.Warning, string.Create(CultureInfo.InvariantCulture,
                    $"Particle pool is full at {system.MaxParticles}, so nothing spawns until a particle dies"), null);
            }
            else if (count == 0 && system.EmittersFinished && system.DormantReason == null)
            {
                add(ParticleDiagnosticSeverity.Info, "Finished: the emitters are done and every particle has expired", null);
            }
        }

        private static void CheckParticles(ParticleSystemSimulation system, Action<ParticleDiagnosticSeverity, string, ParticleDebugFunction?> add)
        {
            var particles = system.Particles.Current;

            if (particles.IsEmpty)
            {
                return;
            }

            var origin = system.MainControlPoint.Position;
            Span<int> counts = stackalloc int[AnomalyDescriptions.Length];

            foreach (ref var particle in particles)
            {
                var anomalies = ParticleAnomalyDetector.Detect(in particle, origin);

                for (var i = 0; i < AnomalyDescriptions.Length; i++)
                {
                    if ((anomalies & AnomalyDescriptions[i].Anomaly) != 0)
                    {
                        counts[i]++;
                    }
                }
            }

            for (var i = 0; i < AnomalyDescriptions.Length; i++)
            {
                var (anomaly, description) = AnomalyDescriptions[i];

                if (counts[i] == 0)
                {
                    continue;
                }

                // The ones a fade causes on purpose only matter when nothing is left to see.
                var severe = (anomaly & ParticleAnomalyDetector.Severe) != 0;

                if (!severe && counts[i] < particles.Length)
                {
                    continue;
                }

                add(severe ? ParticleDiagnosticSeverity.Error : ParticleDiagnosticSeverity.Warning,
                    string.Create(CultureInfo.InvariantCulture, $"{counts[i]} of {particles.Length} particles have {description}"), null);
            }
        }

        private static void CheckTrace(ParticleSystemSimulation system, Action<ParticleDiagnosticSeverity, string, ParticleDebugFunction?> add)
        {
            if (system.Trace is not { } trace)
            {
                return;
            }

            foreach (var step in trace.Steps)
            {
                var severe = step.Introduced & ParticleAnomalyDetector.Severe;

                if (severe == ParticleAnomalies.None)
                {
                    continue;
                }

                add(ParticleDiagnosticSeverity.Error, string.Create(CultureInfo.InvariantCulture,
                    $"{step} introduced {Describe(severe)} on {step.IntroducedCount} particles"), step.Function);
            }
        }
    }
}
