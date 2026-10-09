using System.Globalization;
using ValveResourceFormat.Particles.Debugging;

namespace ValveResourceFormat.Particles
{
    /// <summary>What a debugger needs to see into the system.</summary>
    public partial class ParticleSystemSimulation
    {
        private readonly List<ParticleDebugFunction> debugFunctions = [];
        private readonly List<string> skippedChildren = [];

        /// <summary>
        /// Every function of the definition, including the ones that are disabled or not implemented,
        /// followed by the renderers once a drawing layer has registered them.
        /// </summary>
        public IReadOnlyList<ParticleDebugFunction> DebugFunctions => debugFunctions;

        /// <summary>Lets the drawing layer list its renderers alongside the simulation's functions.</summary>
        internal void AddDebugFunction(ParticleDebugFunction function) => debugFunctions.Add(function);

        /// <summary>The children of the definition that could not be loaded, and why.</summary>
        public IReadOnlyList<string> SkippedChildren => skippedChildren;

        /// <summary>The trace of the system's last step, while a debug session is attached.</summary>
        public ParticleSimulationTrace? Trace { get; private set; }

        /// <summary>
        /// Starts tracing this system and every system under it for <paramref name="session"/>, or stops
        /// when it is null. Tracing copies every particle after every function, so it slows the system down.
        /// </summary>
        /// <param name="session">The session to trace for.</param>
        public void AttachDebugSession(ParticleDebugSession? session)
        {
            Trace = session == null ? null : new ParticleSimulationTrace(this, session);

            foreach (var childSimulation in childSimulations)
            {
                childSimulation.AttachDebugSession(session);
            }
        }

        /// <summary>The system running this one as a child, or null for the root.</summary>
        public ParticleSystemSimulation? Parent => systemState.ParentSystem?.Data;

        /// <summary>The most particles this system can hold at once.</summary>
        public int MaxParticles => particleCollection.Capacity;

        /// <summary>How many particles the system spawns at once when it first starts (<c>m_nInitialParticles</c>).</summary>
        public int InitialParticles => initialParticles;

        /// <summary>How many particles the system has spawned since it last started over.</summary>
        public int ParticlesEmitted => particlesEmitted;

        /// <summary>Whether every emitter has finished.</summary>
        public bool EmittersFinished => emitters.TrueForAll(static emitter => emitter.IsFinished);

        /// <summary>
        /// Whether the system is held in place, by an endcap freeze or by reaching
        /// <c>m_flStopSimulationAfterTime</c>.
        /// </summary>
        public bool IsFrozen => systemState.Frozen || (stopSimulationAfterTime > 0f && systemState.Age >= stopSimulationAfterTime);

        /// <summary>
        /// Why this child is not being advanced right now, or null when it is, or when it is the root.
        /// Only the reasons belonging to this system are given; a child under a dormant parent is dormant too.
        /// </summary>
        public string? DormantReason
        {
            get
            {
                if (systemState.ParentSystem is not { } parentState)
                {
                    return null;
                }

                if (!childEnabled)
                {
                    return "Not picked by the parent's random child selection";
                }

                if (detailLevel > parentState.DetailLevel)
                {
                    return $"Needs detail level {detailLevel}, playing at {parentState.DetailLevel}";
                }

                if (isEndCapChild)
                {
                    return parentState.InEndCap ? null : "Only plays during the parent's endcap";
                }

                if (parentState.Age < startDelay)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"Starts {startDelay:0.###}s into the parent's life");
                }

                if (!IsWithinDrawDistance(systemState.CameraPosition))
                {
                    return string.Create(CultureInfo.InvariantCulture, $"Camera is beyond its max draw distance of {MathF.Sqrt(MaxDrawDistanceSquared):0.#}");
                }

                return null;
            }
        }
    }
}
