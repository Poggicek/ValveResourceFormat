using ValveKeyValue;

namespace ValveResourceFormat.Particles.Debugging
{
    /// <summary>The part of the pipeline a particle function belongs to, in the order a step runs them.</summary>
    public enum ParticleFunctionStage
    {
        /// <summary>Runs once per step before emission, on the system rather than on particles.</summary>
        PreEmissionOperator,

        /// <summary>Decides how many particles to spawn.</summary>
        Emitter,

        /// <summary>Sets up each particle as it spawns.</summary>
        Initializer,

        /// <summary>Changes the particles every step.</summary>
        Operator,

        /// <summary>Adds forces, run from inside the movement operator rather than on its own.</summary>
        ForceGenerator,

        /// <summary>Corrects particle positions after the operators.</summary>
        Constraint,

        /// <summary>Draws the particles.</summary>
        Renderer,
    }

    /// <summary>Whether a function in the definition takes part in the simulation.</summary>
    public enum ParticleFunctionStatus
    {
        /// <summary>Implemented and running.</summary>
        Active,

        /// <summary>Switched off in the definition with <c>m_bDisableOperator</c>.</summary>
        Disabled,

        /// <summary>Not implemented, so it is skipped and whatever it would have done is missing.</summary>
        Unsupported,
    }

    /// <summary>
    /// One function of a particle system definition as the debugger sees it, whether or not it could be
    /// built, with a switch to take it out of the pipeline and the warnings it raised.
    /// </summary>
    public sealed class ParticleDebugFunction
    {
        internal ParticleDebugFunction(ParticleFunctionStage stage, string className, int definitionIndex, KVObject definition, ParticleFunctionStatus status, ParticleFunction? function = null, ParticleFunctionLog? log = null)
        {
            Stage = stage;
            ClassName = className;
            DefinitionIndex = definitionIndex;
            Definition = definition;
            Status = status;
            Function = function;
            this.log = log;
        }

        private readonly ParticleFunctionLog? log;

        /// <summary>The part of the pipeline the function belongs to.</summary>
        public ParticleFunctionStage Stage { get; }

        /// <summary>The function's class, such as <c>C_OP_BasicMovement</c>.</summary>
        public string ClassName { get; }

        /// <summary>The function's position in its list in the definition.</summary>
        public int DefinitionIndex { get; }

        /// <summary>The function's block in the upgraded definition.</summary>
        public KVObject Definition { get; }

        /// <summary>Whether the function takes part in the simulation.</summary>
        public ParticleFunctionStatus Status { get; }

        /// <summary>More about how the function takes part, such as the pass a renderer draws in.</summary>
        public string? Note { get; internal init; }

        internal ParticleFunction? Function { get; }

        /// <summary>
        /// Whether the function is taken out of the pipeline, to see what the effect looks like without
        /// it. Only an <see cref="ParticleFunctionStatus.Active"/> function can be bypassed.
        /// </summary>
        public bool Bypassed
        {
            get => Function?.Bypassed ?? false;
            set => Function?.Bypassed = value;
        }

        /// <summary>Whether the function was built, so that <see cref="Bypassed"/> can take it out.</summary>
        public bool CanBypass => Function != null;

        /// <summary>
        /// Warnings the function raised while it was built or while it ran, such as an input mode that is
        /// not implemented and falls back to a literal value.
        /// </summary>
        public IReadOnlyList<string> Warnings => log?.Messages ?? [];

        /// <summary>The class name without its <c>C_OP_</c> or <c>C_INIT_</c> prefix.</summary>
        public string ShortName => ClassName switch
        {
            _ when ClassName.StartsWith("C_OP_", StringComparison.Ordinal) => ClassName[5..],
            _ when ClassName.StartsWith("C_INIT_", StringComparison.Ordinal) => ClassName[7..],
            _ => ClassName,
        };

        /// <inheritdoc/>
        public override string ToString() => $"{Stage} {ClassName}";
    }
}
