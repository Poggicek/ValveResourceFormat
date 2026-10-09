namespace ValveResourceFormat.Particles.Debugging
{
    /// <summary>A control point as a traced step left it.</summary>
    /// <param name="Index">The control point index.</param>
    /// <param name="Position">Its position, or whatever value a function stores in it.</param>
    /// <param name="Orientation">Its forward direction.</param>
    public readonly record struct ParticleControlPointState(int Index, Vector3 Position, Vector3 Orientation);

    /// <summary>
    /// What one function, or one piece of the simulation's own bookkeeping, did during a traced step:
    /// which attributes it changed, which particles it killed, which anomalies it introduced, and the
    /// state it left behind.
    /// </summary>
    public sealed class ParticleTraceStep
    {
        private readonly Particle[] particles;

        internal ParticleTraceStep(string label, ParticleDebugFunction? function, Particle[] particles, int count, ParticleControlPointState[] controlPoints)
        {
            Label = label;
            Function = function;
            this.particles = particles;
            Count = count;
            ControlPoints = controlPoints;
        }

        /// <summary>What ran, a function's class or the name of a bookkeeping step.</summary>
        public string Label { get; }

        /// <summary>The function that ran, or null for the simulation's own bookkeeping.</summary>
        public ParticleDebugFunction? Function { get; }

        /// <summary>Which constraint pass this was, counting from 1, or 0 when the step is not a constraint.</summary>
        public int Pass { get; internal init; }

        /// <summary>
        /// The strength the function ran at, or 0 when it did not run this step because of its fade,
        /// its endcap state, a run-once flag that already fired or a bypass. Always 1 for bookkeeping, and
        /// for an initializer 1 when it set up any of the particles spawned in the step.
        /// </summary>
        public float Strength { get; internal init; } = 1f;

        /// <summary>For an initializer, how many of the particles spawned in the step it set up.</summary>
        public int Initialized { get; internal init; }

        /// <summary>Whether the function was skipped this step.</summary>
        public bool Skipped => Strength <= 0f;

        /// <summary>How many particles there were before the step.</summary>
        public int CountBefore { get; internal init; }

        /// <summary>How many particles there were after the step.</summary>
        public int Count { get; }

        /// <summary>How many particles the step killed or removed.</summary>
        public int Killed { get; internal init; }

        /// <summary>Which attributes the step changed on any particle, a bit per <see cref="ParticleAttribute.Index"/>.</summary>
        public ulong ChangedAttributes { get; internal init; }

        /// <summary>The anomalies that some particle showed after the step but not before it.</summary>
        public ParticleAnomalies Introduced { get; internal init; }

        /// <summary>How many particles gained an anomaly in the step.</summary>
        public int IntroducedCount { get; internal init; }

        /// <summary>The control points whose value the step changed.</summary>
        public IReadOnlyList<int> ChangedControlPoints { get; internal init; } = [];

        /// <summary>The control points as the step left them.</summary>
        public IReadOnlyList<ParticleControlPointState> ControlPoints { get; }

        /// <summary>
        /// The particles as the step left them. Their indices are the collection's for the whole step,
        /// since dead particles are only removed by its last step. The memory is reused by later steps,
        /// so copy what you need before the system simulates again.
        /// </summary>
        public ReadOnlySpan<Particle> Particles => particles.AsSpan(0, Count);

        /// <inheritdoc/>
        public override string ToString() => Pass > 0 ? $"{Label} (pass {Pass})" : Label;
    }

    /// <summary>How a particle's spawn went through one initializer.</summary>
    public enum ParticleSpawnOutcome
    {
        /// <summary>The initializer ran.</summary>
        Ran,

        /// <summary>The initializer was skipped because of its endcap state.</summary>
        NotInPhase,

        /// <summary>
        /// The initializer was skipped because earlier ones had already written everything it writes,
        /// which below behavior version 6 means the first writer wins.
        /// </summary>
        AlreadyWritten,

        /// <summary>The initializer was bypassed in the debugger.</summary>
        Bypassed,
    }

    /// <summary>One stage of a particle's spawn.</summary>
    /// <param name="Initializer">The initializer, or null for the state the particle starts from.</param>
    /// <param name="Outcome">Whether the initializer ran.</param>
    /// <param name="State">The particle after the stage.</param>
    public readonly record struct ParticleSpawnStage(ParticleDebugFunction? Initializer, ParticleSpawnOutcome Outcome, Particle State);

    /// <summary>How a particle was set up, one stage per initializer.</summary>
    public sealed class ParticleSpawnTrace
    {
        internal ParticleSpawnTrace(int uniqueParticleId, float systemAge, ParticleSpawnStage[] stages)
        {
            UniqueParticleId = uniqueParticleId;
            SystemAge = systemAge;
            Stages = stages;
        }

        /// <summary>The particle's <see cref="Particle.UniqueParticleId"/>.</summary>
        public int UniqueParticleId { get; }

        /// <summary>The system's age when the particle spawned.</summary>
        public float SystemAge { get; }

        /// <summary>
        /// The particle's state before any initializer, from the definition's constants and the emission
        /// placement, followed by its state after each initializer in order.
        /// </summary>
        public IReadOnlyList<ParticleSpawnStage> Stages { get; }

        internal int LastSeenStep { get; set; }
    }
}
