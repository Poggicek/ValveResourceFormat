using ValveResourceFormat.Particles;
using ValveResourceFormat.Particles.Debugging;
using ValveResourceFormat.Renderer.SceneNodes;

namespace ValveResourceFormat.Renderer.Particles
{
    /// <summary>
    /// Draws where the simulation puts each particle, whether or not its renderer shows it, so a broken
    /// effect can be told apart into a simulation that goes wrong and a renderer that draws it wrong.
    /// Particles are crosses the size of their radius, with a line along their velocity; control points
    /// are boxes with a line along their forward direction.
    /// </summary>
    public class ParticleDebugRenderer : LineDebugRenderer
    {
        // How far ahead the velocity line reaches, in seconds of travel
        private const float VelocityLineTime = 0.1f;
        private const float MinimumCrossSize = 0.5f;
        private const float ControlPointSize = 2f;
        private const float ControlPointForwardLength = 16f;

        private static readonly Color32 SevereColor = new(1f, 0.15f, 0.15f, 1f);
        private static readonly Color32 InvisibleColor = new(1f, 0.6f, 0f, 1f);
        private static readonly Color32 ControlPointColor = new(1f, 0.3f, 1f, 1f);
        private static readonly Color32 SelectedColor = Color32.Yellow;

        // Told apart by hue so that a child's particles can be found among its parent's
        private static readonly Color32[] SystemColors =
        [
            new(0.3f, 1f, 0.4f, 1f),
            new(0.3f, 0.8f, 1f, 1f),
            new(1f, 0.85f, 0.3f, 1f),
            new(0.75f, 0.55f, 1f, 1f),
            new(0.4f, 1f, 0.9f, 1f),
            new(1f, 0.55f, 0.75f, 1f),
        ];

        private readonly List<SimpleVertex> vertices = new(1024);
        private readonly SortedList<int, ControlPoint> controlPoints = [];
        private int systemIndex;

        /// <summary>Whether particles get a line along their velocity.</summary>
        public bool ShowVelocity { get; set; } = true;

        /// <summary>Whether control points are drawn.</summary>
        public bool ShowControlPoints { get; set; } = true;

        /// <summary>Initializes the renderer and creates GPU resources.</summary>
        /// <param name="rendererContext">Renderer context for loading shaders.</param>
        public ParticleDebugRenderer(RendererContext rendererContext)
            : base(rendererContext, nameof(ParticleDebugRenderer))
        {
        }

        /// <summary>The color a system's particles are drawn in, to match them up in a list.</summary>
        /// <param name="index">The system's position in a depth-first walk of the tree, the root being 0.</param>
        public static Color32 GetSystemColor(int index) => SystemColors[index % SystemColors.Length];

        /// <summary>Rebuilds and draws the overlay for a system tree.</summary>
        /// <param name="root">The root system.</param>
        /// <param name="selectedSystem">The system whose particle is picked out, if any.</param>
        /// <param name="selectedParticleId">The <see cref="Particle.UniqueParticleId"/> of the particle to pick out.</param>
        public void Render(ParticleSystemSimulation root, ParticleSystemSimulation? selectedSystem, int selectedParticleId)
        {
            vertices.Clear();
            systemIndex = 0;

            AddSystem(root, selectedSystem, selectedParticleId);

            if (ShowControlPoints)
            {
                AddControlPoints(root);
            }

            Upload(vertices);
            RenderLines();
        }

        private void AddSystem(ParticleSystemSimulation system, ParticleSystemSimulation? selectedSystem, int selectedParticleId)
        {
            var color = GetSystemColor(systemIndex++);
            var origin = system.MainControlPoint.Position;

            foreach (ref var particle in system.Particles.Current)
            {
                var position = particle.Position;

                if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                {
                    continue;
                }

                var anomalies = ParticleAnomalyDetector.Detect(in particle, origin);
                var particleColor = (anomalies & ParticleAnomalyDetector.Severe) != 0 ? SevereColor
                    : (anomalies & (ParticleAnomalies.ZeroRadius | ParticleAnomalies.Transparent | ParticleAnomalies.Black)) != 0 ? InvisibleColor
                    : color;

                var size = float.IsFinite(particle.Radius) ? Math.Clamp(particle.Radius, MinimumCrossSize, ParticleAnomalyDetector.HugeRadius) : MinimumCrossSize;

                ShapeSceneNode.AddLine(vertices, position - Vector3.UnitX * size, position + Vector3.UnitX * size, particleColor);
                ShapeSceneNode.AddLine(vertices, position - Vector3.UnitY * size, position + Vector3.UnitY * size, particleColor);
                ShapeSceneNode.AddLine(vertices, position - Vector3.UnitZ * size, position + Vector3.UnitZ * size, particleColor);

                var velocity = particle.Velocity;

                if (ShowVelocity && velocity != Vector3.Zero && float.IsFinite(velocity.LengthSquared()))
                {
                    var faded = particleColor with { A = 128 };
                    ShapeSceneNode.AddLine(vertices, position, position + velocity * VelocityLineTime, faded);
                }

                if (system == selectedSystem && particle.UniqueParticleId == selectedParticleId)
                {
                    ShapeSceneNode.AddSphere(vertices, position, size, SelectedColor);
                    ShapeSceneNode.AddBox(vertices, new AABB(position - new Vector3(size * 1.5f), position + new Vector3(size * 1.5f)), SelectedColor);
                }
            }

            foreach (var child in system.Children)
            {
                AddSystem(child, selectedSystem, selectedParticleId);
            }
        }

        private void AddControlPoints(ParticleSystemSimulation root)
        {
            controlPoints.Clear();
            root.RenderState.CollectControlPoints(controlPoints);

            foreach (var point in controlPoints.Values)
            {
                var position = point.Position;

                if (!float.IsFinite(position.LengthSquared()))
                {
                    continue;
                }

                ShapeSceneNode.AddBox(vertices, new AABB(position - new Vector3(ControlPointSize), position + new Vector3(ControlPointSize)), ControlPointColor);

                if (point.Orientation != Vector3.Zero && float.IsFinite(point.Orientation.LengthSquared()))
                {
                    ShapeSceneNode.AddLine(vertices, position, position + Vector3.Normalize(point.Orientation) * ControlPointForwardLength, ControlPointColor);
                }
            }
        }
    }
}
