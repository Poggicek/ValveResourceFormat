using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ValveResourceFormat.Particles.Debugging
{
    /// <summary>
    /// Records what every function of one particle system did during its most recent simulation step,
    /// and how each live particle was set up when it spawned. A broken effect usually shows its symptom
    /// far from its cause, since everything after a bad value builds on it; the trace shows the first
    /// function that produced it.
    /// </summary>
    public sealed class ParticleSimulationTrace
    {
        private readonly ParticleSystemSimulation system;
        private readonly Dictionary<int, ParticleSpawnTrace> spawnTraces = [];
        private readonly List<int> deadSpawnTraces = [];

        // Particle snapshots are large and taken every step, so their buffers are kept and reused.
        private readonly List<Particle[]> bufferPool = [];
        private int buffersInUse;

        private List<ParticleTraceStep> steps = [];
        private List<ParticleTraceStep> recordingSteps = [];
        private bool recording;

        // State the previous step left, which the next one is compared against
        private Particle[] previousParticles = [];
        private int previousCount;
        private ParticleAnomalies[] previousAnomalies = [];
        private ParticleControlPointState[] previousControlPoints = [];
        private readonly SortedList<int, ControlPoint> controlPointScratch = [];

        // Initializer results for the particles spawned since the last step, merged across spawns
        private SpawnAggregate[] spawnAggregates = [];
        private int spawnedSinceStep;
        private ParticleSpawnStage[]? spawnStages;
        private ParticleAnomalies spawnAnomalies;

        private struct SpawnAggregate
        {
            public ParticleDebugFunction? Initializer;
            public int Initialized;
            public ulong Changed;
            public ParticleAnomalies Introduced;
            public int IntroducedCount;
        }

        internal ParticleSimulationTrace(ParticleSystemSimulation system, ParticleDebugSession session)
        {
            this.system = system;
            Session = session;
        }

        /// <summary>The session this trace records for.</summary>
        public ParticleDebugSession Session { get; }

        /// <summary>How many steps have been traced.</summary>
        public int StepNumber { get; private set; }

        /// <summary>How long the most recently traced step was, in seconds.</summary>
        public float StepTime { get; private set; }

        /// <summary>The system's age at the end of the most recently traced step.</summary>
        public float SystemAge { get; private set; }

        /// <summary>
        /// Everything that ran in the most recently traced step, in order. Replaced as a whole by the next
        /// step, and the particle memory of its steps is reused, so copy what you need before the system
        /// simulates again.
        /// </summary>
        public IReadOnlyList<ParticleTraceStep> Steps => steps;

        /// <summary>Gets how the live particle with <paramref name="uniqueParticleId"/> was set up.</summary>
        public bool TryGetSpawnTrace(int uniqueParticleId, [MaybeNullWhen(false)] out ParticleSpawnTrace trace)
            => spawnTraces.TryGetValue(uniqueParticleId, out trace);

        /// <summary>Forgets every particle, for a system whose particles were all dropped.</summary>
        internal void Clear()
        {
            spawnTraces.Clear();
            Array.Clear(spawnAggregates);
            spawnedSinceStep = 0;
            previousCount = 0;
        }

        internal void BeginStep(ParticleCollection particles, ParticleSystemState state, float stepTime)
        {
            recording = Session.Break == null;

            if (!recording)
            {
                return;
            }

            buffersInUse = 0;
            recordingSteps.Clear();
            StepTime = stepTime;

            // Particles alive since the last step are the baseline; ones spawned outside a step, by the
            // initial burst, were set up already and only their initializers are of interest.
            previousCount = 0;
            Record("Step start", null, particles, state, baseline: true);
        }

        /// <summary>Records what a function did, after it ran or was skipped.</summary>
        internal void Record(ParticleFunction function, ParticleCollection particles, ParticleSystemState state, float strength, int pass = 0)
        {
            if (recording)
            {
                Record(function.DebugFunction?.ClassName ?? function.GetType().Name, function.DebugFunction, particles, state, strength, pass);
            }
        }

        /// <summary>Records one of the simulation's own bookkeeping steps.</summary>
        internal void Record(string label, ParticleCollection particles, ParticleSystemState state)
        {
            if (recording)
            {
                Record(label, null, particles, state);
            }
        }

        /// <summary>Records the initializers, merged over every particle spawned since the last time.</summary>
        internal void RecordSpawns()
        {
            if (!recording || spawnedSinceStep == 0)
            {
                return;
            }

            var buffer = previousParticles;
            var controlPoints = previousControlPoints;

            foreach (var aggregate in spawnAggregates)
            {
                var step = new ParticleTraceStep(aggregate.Initializer?.ClassName ?? "Constants", aggregate.Initializer, buffer, previousCount, controlPoints)
                {
                    CountBefore = previousCount,
                    Strength = aggregate.Initialized > 0 || aggregate.Initializer == null ? 1f : 0f,
                    Initialized = aggregate.Initialized,
                    ChangedAttributes = aggregate.Changed,
                    Introduced = aggregate.Introduced,
                    IntroducedCount = aggregate.IntroducedCount,
                };

                recordingSteps.Add(step);
                CheckBreak(step);
            }

            Array.Clear(spawnAggregates);
            spawnedSinceStep = 0;
        }

        internal void EndStep(ParticleCollection particles, ParticleSystemState state)
        {
            if (!recording)
            {
                return;
            }

            recording = false;
            StepNumber++;
            SystemAge = state.Age;

            (steps, recordingSteps) = (recordingSteps, steps);

            foreach (ref var particle in particles.Current)
            {
                if (spawnTraces.TryGetValue(particle.UniqueParticleId, out var spawnTrace))
                {
                    spawnTrace.LastSeenStep = StepNumber;
                }
            }

            foreach (var (id, spawnTrace) in spawnTraces)
            {
                if (spawnTrace.LastSeenStep != StepNumber)
                {
                    deadSpawnTraces.Add(id);
                }
            }

            foreach (var id in deadSpawnTraces)
            {
                spawnTraces.Remove(id);
            }

            deadSpawnTraces.Clear();
        }

        internal void BeginSpawn(in Particle particle, int initializerCount)
        {
            if (Session.Break != null)
            {
                spawnStages = null;
                return;
            }

            spawnStages = new ParticleSpawnStage[initializerCount + 1];
            spawnStages[0] = new ParticleSpawnStage(null, ParticleSpawnOutcome.Ran, particle);
            spawnAnomalies = ParticleAnomalyDetector.Detect(in particle, system.MainControlPoint.Position);

            if (spawnAggregates.Length != spawnStages.Length)
            {
                spawnAggregates = new SpawnAggregate[spawnStages.Length];
            }

            ref var constants = ref spawnAggregates[0];
            constants.Initialized++;
            Introduce(ref constants, spawnAnomalies);
        }

        internal void RecordInitializer(int index, ParticleFunction initializer, in Particle particle, ParticleSpawnOutcome outcome)
        {
            if (spawnStages == null)
            {
                return;
            }

            var before = spawnStages[index].State;
            spawnStages[index + 1] = new ParticleSpawnStage(initializer.DebugFunction, outcome, particle);

            ref var aggregate = ref spawnAggregates[index + 1];
            aggregate.Initializer = initializer.DebugFunction;

            if (outcome != ParticleSpawnOutcome.Ran)
            {
                return;
            }

            aggregate.Initialized++;
            aggregate.Changed |= ChangedAttributes(in before, in particle);

            var anomalies = ParticleAnomalyDetector.Detect(in particle, system.MainControlPoint.Position);
            Introduce(ref aggregate, anomalies & ~spawnAnomalies);
            spawnAnomalies = anomalies;
        }

        internal void EndSpawn(in Particle particle, float systemAge)
        {
            if (spawnStages == null)
            {
                return;
            }

            spawnTraces[particle.UniqueParticleId] = new ParticleSpawnTrace(particle.UniqueParticleId, systemAge, spawnStages)
            {
                LastSeenStep = StepNumber,
            };

            spawnedSinceStep++;
            spawnStages = null;
        }

        private static void Introduce(ref SpawnAggregate aggregate, ParticleAnomalies introduced)
        {
            if (introduced != ParticleAnomalies.None)
            {
                aggregate.Introduced |= introduced;
                aggregate.IntroducedCount++;
            }
        }

        private void Record(string label, ParticleDebugFunction? function, ParticleCollection particles, ParticleSystemState state, float strength = 1f, int pass = 0, bool baseline = false)
        {
            var current = particles.Current;
            var origin = system.MainControlPoint.Position;
            var countBefore = previousCount;

            var changed = 0UL;
            var killed = 0;
            var introduced = ParticleAnomalies.None;
            var introducedCount = 0;
            var anyChange = current.Length != countBefore;

            if (previousAnomalies.Length < particles.Capacity)
            {
                Array.Resize(ref previousAnomalies, particles.Capacity);
            }

            if (current.Length < countBefore)
            {
                // Only removing the dead shrinks the collection, and it compacts the survivors, so indices
                // no longer line up with the previous step.
                killed = countBefore - current.Length;

                for (var i = 0; i < current.Length; i++)
                {
                    previousAnomalies[i] = ParticleAnomalyDetector.Detect(in current[i], origin);
                }
            }
            else
            {
                for (var i = 0; i < current.Length; i++)
                {
                    ref var particle = ref current[i];

                    // Particles new to this step were spawned in it, and their initializers own what they show.
                    if (i >= countBefore)
                    {
                        previousAnomalies[i] = ParticleAnomalyDetector.Detect(in particle, origin);
                        continue;
                    }

                    ref var before = ref previousParticles[i];

                    if (BitwiseEqual(in before, in particle))
                    {
                        continue;
                    }

                    anyChange = true;
                    changed |= ChangedAttributes(in before, in particle);

                    if (particle.MarkedAsKilled && !before.MarkedAsKilled)
                    {
                        killed++;
                    }

                    var anomalies = ParticleAnomalyDetector.Detect(in particle, origin);
                    var gained = anomalies & ~previousAnomalies[i];

                    if (gained != ParticleAnomalies.None)
                    {
                        introduced |= gained;
                        introducedCount++;
                    }

                    previousAnomalies[i] = anomalies;
                }
            }

            // A step that changed nothing shares the snapshot of the one before it.
            if (anyChange || buffersInUse == 0)
            {
                previousParticles = RentBuffer(particles.Capacity);
                current.CopyTo(previousParticles);
            }

            previousCount = current.Length;

            var controlPoints = SnapshotControlPoints(state, out var changedControlPoints);

            var step = new ParticleTraceStep(label, function, previousParticles, previousCount, controlPoints)
            {
                Pass = pass,
                Strength = strength,
                CountBefore = baseline ? current.Length : countBefore,
                Killed = killed,
                ChangedAttributes = changed,
                Introduced = introduced,
                IntroducedCount = introducedCount,
                ChangedControlPoints = changedControlPoints,
            };

            recordingSteps.Add(step);
            CheckBreak(step);
        }

        private void CheckBreak(ParticleTraceStep step)
        {
            var hit = step.Introduced & Session.BreakOn;

            if (hit != ParticleAnomalies.None && Session.Break == null)
            {
                Session.Break = new ParticleBreak(system, step, hit);
            }
        }

        private ParticleControlPointState[] SnapshotControlPoints(ParticleSystemState state, out IReadOnlyList<int> changed)
        {
            controlPointScratch.Clear();
            state.CollectControlPoints(controlPointScratch);

            changed = [];
            var unchanged = controlPointScratch.Count == previousControlPoints.Length;

            if (unchanged)
            {
                for (var i = 0; i < previousControlPoints.Length; i++)
                {
                    var point = controlPointScratch.GetValueAtIndex(i);

                    if (previousControlPoints[i] != new ParticleControlPointState(controlPointScratch.GetKeyAtIndex(i), point.Position, point.Orientation))
                    {
                        unchanged = false;
                        break;
                    }
                }
            }

            if (unchanged)
            {
                return previousControlPoints;
            }

            var snapshot = new ParticleControlPointState[controlPointScratch.Count];
            var changedIndices = new List<int>();

            for (var i = 0; i < snapshot.Length; i++)
            {
                var point = controlPointScratch.GetValueAtIndex(i);
                snapshot[i] = new ParticleControlPointState(controlPointScratch.GetKeyAtIndex(i), point.Position, point.Orientation);

                if (Array.IndexOf(previousControlPoints, snapshot[i]) < 0)
                {
                    changedIndices.Add(snapshot[i].Index);
                }
            }

            // The first snapshot of a step is the baseline, not a change
            if (recordingSteps.Count > 0)
            {
                changed = changedIndices;
            }

            previousControlPoints = snapshot;
            return snapshot;
        }

        private Particle[] RentBuffer(int capacity)
        {
            if (buffersInUse == bufferPool.Count)
            {
                bufferPool.Add(new Particle[capacity]);
            }

            return bufferPool[buffersInUse++];
        }

        private static ulong ChangedAttributes(in Particle before, in Particle after)
        {
            var changed = 0UL;

            foreach (var attribute in ParticleAttribute.All)
            {
                if (!attribute.ValuesEqual(in before, in after))
                {
                    changed |= 1UL << attribute.Index;
                }
            }

            return changed;
        }

        private static bool BitwiseEqual(in Particle a, in Particle b)
            => MemoryMarshal.AsBytes(new ReadOnlySpan<Particle>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Particle>(in b)));
    }
}
