using ValveKeyValue;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Runs the state-machine part of an animgraph 1 graph (<c>.vanmgrph</c>): the part that decides which
    /// animation an entity is playing, and changes its mind when a parameter changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Half-Life Alyx NPC is not told which animation to play. The map sets named parameters on it -
    /// <c>bActionA=true</c> - and the graph turns those into a state, and the state into a sequence. Without
    /// something to evaluate that, every such NPC stands in whatever it spawned in while the map fires
    /// parameters at it that nothing reads.
    /// </para>
    /// <para>
    /// What runs here is the spine of the graph and nothing else: the root, the state machines, the states
    /// and their transitions, and the sequence leaves those states resolve to. That is what chooses the
    /// animation. The rest of a graph shapes how it is played rather than which it is - blends, bone masks,
    /// IK, look-ats, aim matrices, motors - and is walked through rather than evaluated, taking the first
    /// child, so a state buried under a blend still finds its sequence.
    /// </para>
    /// <para>
    /// Tag conditions are the known gap. Tags are spans an animation carries and states raise, held by the
    /// graph's tag manager, and none of that is modelled: a transition guarded by one is never taken. A
    /// transition guarded by a parameter, by an animation finishing, or by time in state is.
    /// </para>
    /// </remarks>
    public sealed class AnimationGraphRuntime
    {
        /// <summary>
        /// How a condition compares what it reads against the value it was authored with.
        /// </summary>
        /// <remarks>
        /// The order is the graph's own <c>m_comparisonOp</c>, which the compiled data writes as a bare
        /// integer. Equality is what the parameter conditions in practice use; the ordered comparisons show
        /// up on the time conditions.
        /// </remarks>
        private enum ComparisonOp
        {
            Equal,
            NotEqual,
            GreaterThan,
            GreaterThanOrEqual,
            LessThan,
            LessThanOrEqual,
        }

        /// <summary>One state of a state machine, and where it can go from here.</summary>
        private sealed record AnimState(uint Id, string Name, uint ChildNodeId, bool IsStartState, IReadOnlyList<KVObject> Transitions);

        /// <summary>A state machine node: its states, and which one it starts in.</summary>
        private sealed class StateMachine
        {
            public required Dictionary<uint, AnimState> States { get; init; }
            public required uint StartStateId { get; init; }

            public uint CurrentStateId { get; set; }
            public float TimeInState { get; set; }
        }

        /// <summary>Gets the sequence the graph currently wants played, or <see langword="null"/> for none.</summary>
        public string? CurrentSequence { get; private set; }

        /// <summary>Gets whether the current sequence repeats.</summary>
        public bool CurrentSequenceLoops { get; private set; }

        /// <summary>Gets the names of every parameter the graph declares, for diagnostics.</summary>
        public IReadOnlyCollection<string> ParameterNames => parameterIds.Keys;

        private readonly Dictionary<uint, KVObject> nodes = [];
        private readonly Dictionary<string, uint> parameterIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, float> parameterValues = [];
        private readonly HashSet<uint> autoResetParameters = [];
        private readonly Dictionary<uint, StateMachine> stateMachines = [];
        private uint rootNodeId;

        private AnimationGraphRuntime()
        {
        }

        /// <summary>
        /// Loads the animation graph a model references, if it has one.
        /// </summary>
        /// <param name="modelResource">The model to read the reference from.</param>
        /// <param name="fileLoader">Loader for the graph itself.</param>
        /// <returns>The graph, or <see langword="null"/> when the model names none or it will not load.</returns>
        /// <remarks>
        /// The reference is in the model's external resources rather than its keyvalues: a model does not
        /// describe its graph, it just ships alongside one, and the compiler records the dependency.
        /// </remarks>
        public static AnimationGraphRuntime? Load(Resource modelResource, IFileLoader fileLoader)
        {
            var graphName = FindGraphName(modelResource);

            if (graphName == null || fileLoader.LoadFileCompiled(graphName)?.DataBlock is not BinaryKV3 graph)
            {
                return null;
            }

            var runtime = new AnimationGraphRuntime();

            return runtime.Parse(graph.Data.Root) ? runtime : null;
        }

        private static string? FindGraphName(Resource modelResource)
        {
            foreach (var external in modelResource.ExternalReferences?.ResourceRefInfoList ?? [])
            {
                if (external.Name.EndsWith(".vanmgrph", StringComparison.OrdinalIgnoreCase))
                {
                    return external.Name;
                }
            }

            return null;
        }

        private bool Parse(KVObject root)
        {
            rootNodeId = ReadId(root.GetSubCollection("m_rootNodeID"));

            if (rootNodeId == 0)
            {
                return false;
            }

            // The node table is a map written as key/value pairs; every node also carries its own id, so
            // the value alone is enough to index them
            foreach (var entry in root.GetArray("m_nodes") ?? [])
            {
                var node = entry.GetSubCollection("value");

                if (node == null)
                {
                    continue;
                }

                var id = ReadId(node.GetSubCollection("m_nNodeID"));

                if (id != 0)
                {
                    nodes[id] = node;
                }
            }

            ParseParameters(root.GetSubCollection("m_pParameterList"));

            foreach (var (id, node) in nodes)
            {
                if (ClassOf(node) == "CStateMachineAnimNode")
                {
                    ParseStateMachine(id, node);
                }
            }

            return nodes.Count > 0;
        }

        private void ParseParameters(KVObject? parameterList)
        {
            foreach (var parameter in parameterList?.GetArray("m_Parameters") ?? [])
            {
                var name = parameter.GetStringProperty("m_name");
                var id = ReadId(parameter.GetSubCollection("m_id"));

                if (string.IsNullOrEmpty(name) || id == 0)
                {
                    continue;
                }

                parameterIds[name] = id;

                parameterValues[id] = ClassOf(parameter) switch
                {
                    "CBoolAnimParameter" => parameter.GetBooleanProperty("m_bDefaultValue") ? 1f : 0f,
                    _ => parameter.GetFloatProperty("m_fDefaultValue"),
                };

                if (parameter.GetBooleanProperty("m_bAutoReset"))
                {
                    autoResetParameters.Add(id);
                }
            }
        }

        private void ParseStateMachine(uint nodeId, KVObject node)
        {
            var states = new Dictionary<uint, AnimState>();
            var startStateId = 0u;

            foreach (var state in node.GetArray("m_states") ?? [])
            {
                var id = ReadId(state.GetSubCollection("m_stateID"));

                if (id == 0)
                {
                    continue;
                }

                var isStart = state.GetBooleanProperty("m_bIsStartState");

                states[id] = new AnimState(
                    id,
                    state.GetStringProperty("m_name") ?? string.Empty,
                    ReadId(state.GetSubCollection("m_childNodeID")),
                    isStart,
                    state.GetArray("m_transitions") ?? []);

                if (isStart || startStateId == 0)
                {
                    startStateId = isStart ? id : startStateId;
                }
            }

            if (states.Count == 0)
            {
                return;
            }

            // A machine with no state flagged as the start still has to begin somewhere
            if (startStateId == 0)
            {
                foreach (var id in states.Keys)
                {
                    startStateId = id;
                    break;
                }
            }

            stateMachines[nodeId] = new StateMachine
            {
                States = states,
                StartStateId = startStateId,
                CurrentStateId = startStateId,
            };
        }

        /// <summary>
        /// Sets a parameter, as the map's <c>SetAnimGraphParameter</c> input does.
        /// </summary>
        /// <param name="name">The parameter's name.</param>
        /// <param name="value">Its new value; a boolean parameter reads any non-zero as true.</param>
        /// <returns><see langword="true"/> when the graph declares a parameter by that name.</returns>
        public bool SetParameter(string name, float value)
        {
            if (!parameterIds.TryGetValue(name, out var id))
            {
                return false;
            }

            parameterValues[id] = value;

            return true;
        }

        /// <summary>
        /// Advances the graph and works out what it now wants played.
        /// </summary>
        /// <param name="deltaTime">Seconds since the last evaluation.</param>
        /// <param name="animationFinished">Whether the sequence it last asked for has played out.</param>
        /// <returns><see langword="true"/> when <see cref="CurrentSequence"/> changed and should be played.</returns>
        public bool Update(float deltaTime, bool animationFinished)
        {
            var previous = CurrentSequence;

            foreach (var machine in stateMachines.Values)
            {
                machine.TimeInState += deltaTime;
            }

            CurrentSequence = null;
            CurrentSequenceLoops = false;

            Resolve(rootNodeId, animationFinished, 0);

            // Set for one evaluation and then forgotten, which is how a graph models a one-off event
            foreach (var id in autoResetParameters)
            {
                parameterValues[id] = 0f;
            }

            return CurrentSequence != previous;
        }

        /// <summary>
        /// Walks from a node down to the sequence it resolves to, stepping the state machines on the way.
        /// </summary>
        private void Resolve(uint nodeId, bool animationFinished, int depth)
        {
            // A graph should be a tree, but it is data from a file and a cycle in it must not hang the map
            if (depth > 64 || !nodes.TryGetValue(nodeId, out var node))
            {
                return;
            }

            var nodeClass = ClassOf(node);

            if (nodeClass == "CSequenceAnimNode")
            {
                CurrentSequence = node.GetStringProperty("m_sequenceName");
                CurrentSequenceLoops = node.GetBooleanProperty("m_bLoop");
                return;
            }

            if (nodeClass == "CStateMachineAnimNode" && stateMachines.TryGetValue(nodeId, out var machine))
            {
                Resolve(StepStateMachine(machine, animationFinished), animationFinished, depth + 1);
                return;
            }

            // Everything else shapes the pose rather than choosing it, so walk through to the first thing
            // underneath that might
            Resolve(FirstChildOf(node), animationFinished, depth + 1);
        }

        /// <summary>
        /// Takes the first transition out of the current state whose conditions all hold, and reports the
        /// node the resulting state plays.
        /// </summary>
        private uint StepStateMachine(StateMachine machine, bool animationFinished)
        {
            if (!machine.States.TryGetValue(machine.CurrentStateId, out var state))
            {
                machine.CurrentStateId = machine.StartStateId;

                if (!machine.States.TryGetValue(machine.CurrentStateId, out state))
                {
                    return 0;
                }
            }

            foreach (var transition in state.Transitions)
            {
                if (transition.GetBooleanProperty("m_bDisabled"))
                {
                    continue;
                }

                var destination = ReadId(transition.GetSubCollection("m_destState"));

                if (destination == 0 || destination == state.Id || !machine.States.ContainsKey(destination))
                {
                    continue;
                }

                if (!ConditionsHold(transition, machine, animationFinished))
                {
                    continue;
                }

                machine.CurrentStateId = destination;
                machine.TimeInState = 0f;

                // One transition per evaluation, as the engine does: a chain of them takes a tick each
                return machine.States[destination].ChildNodeId;
            }

            return state.ChildNodeId;
        }

        private bool ConditionsHold(KVObject transition, StateMachine machine, bool animationFinished)
        {
            var conditions = transition.GetArray("m_conditions");

            // A transition with nothing guarding it is taken as soon as it is looked at
            if (conditions == null || conditions.Count == 0)
            {
                return true;
            }

            foreach (var condition in conditions)
            {
                if (!ConditionHolds(condition, machine, animationFinished))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ConditionHolds(KVObject condition, StateMachine machine, bool animationFinished)
        {
            var op = (ComparisonOp)condition.GetInt32Property("m_comparisonOp");

            switch (ClassOf(condition))
            {
                case "CParameterAnimCondition":
                    {
                        var id = ReadId(condition.GetSubCollection("m_paramID"));

                        if (!parameterValues.TryGetValue(id, out var value))
                        {
                            return false;
                        }

                        var authored = condition.GetSubCollection("m_comparisonValue")?.GetFloatProperty("m_data") ?? 0f;

                        return Compare(value, authored, op);
                    }

                case "CFinishedCondition":
                    return animationFinished == condition.GetBooleanProperty("m_bIsFinished");

                case "CTimeCondition":
                    return Compare(machine.TimeInState, condition.GetFloatProperty("m_comparisonValue"), op);

                // Tags are not modelled, and a transition that needs one cannot be shown to hold
                default:
                    return false;
            }
        }

        private static bool Compare(float value, float authored, ComparisonOp op) => op switch
        {
            ComparisonOp.Equal => value == authored,
            ComparisonOp.NotEqual => value != authored,
            ComparisonOp.GreaterThan => value > authored,
            ComparisonOp.GreaterThanOrEqual => value >= authored,
            ComparisonOp.LessThan => value < authored,
            ComparisonOp.LessThanOrEqual => value <= authored,
            _ => false,
        };

        /// <summary>The node underneath this one, whatever it calls it.</summary>
        private static uint FirstChildOf(KVObject node)
        {
            if (node.GetSubCollection("m_childID") is { } child)
            {
                return ReadId(child);
            }

            if (node.GetSubCollection("m_childNodeID") is { } childNode)
            {
                return ReadId(childNode);
            }

            foreach (var entry in node.GetArray("m_children") ?? [])
            {
                var id = ReadId(entry.GetSubCollection("m_nodeID"));

                if (id != 0)
                {
                    return id;
                }
            }

            return 0;
        }

        private static string? ClassOf(KVObject node) => node.GetStringProperty("_class");

        /// <summary>Reads one of the graph's identifiers, which are wrappers around a single hash.</summary>
        private static uint ReadId(KVObject? id) => id?.GetUInt32Property("m_id") ?? 0u;
    }
}
