namespace ValveResourceFormat.Particles.Debugging
{
    /// <summary>
    /// Debugging attached to a particle system and every system under it: it gives each one a
    /// <see cref="ParticleSimulationTrace"/> and can stop playback the moment a function introduces an
    /// anomaly.
    /// </summary>
    /// <seealso cref="ParticleSystemSimulation.AttachDebugSession"/>
    public sealed class ParticleDebugSession
    {
        /// <summary>The anomalies that hit <see cref="Break"/> when a function introduces them.</summary>
        public ParticleAnomalies BreakOn { get; set; }

        /// <summary>
        /// Where playback broke, or null while it runs. Once set, the traces stop recording so they keep
        /// showing the step that broke, until <see cref="Continue"/> is called.
        /// </summary>
        public ParticleBreak? Break { get; internal set; }

        /// <summary>Clears <see cref="Break"/> so the traces record again.</summary>
        public void Continue() => Break = null;
    }

    /// <summary>A function introducing an anomaly that <see cref="ParticleDebugSession.BreakOn"/> asked to stop on.</summary>
    /// <param name="System">The system the function belongs to.</param>
    /// <param name="Step">The step of the system's trace that introduced it.</param>
    /// <param name="Anomalies">The anomalies it introduced that were asked for.</param>
    public sealed record ParticleBreak(ParticleSystemSimulation System, ParticleTraceStep Step, ParticleAnomalies Anomalies);
}
