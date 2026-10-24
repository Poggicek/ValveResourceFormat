using ValveResourceFormat.Renderer.Input;
using ValveResourceFormat.Renderer.SceneNodes;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Draws a movement playback against its recording: the path the replayed player took, the path the
    /// game had them take, the hull where the game had them on the last tick played and, for a camera off
    /// the player, the replayed player's own hull.
    /// </summary>
    public class MovementPlaybackDebugRenderer : LineDebugRenderer
    {
        private static readonly Color32 ReplayedColor = new(0.25f, 1f, 1f, 1f);
        private static readonly Color32 ServerColor = new(1f, 0.5f, 0f, 1f);

        private readonly List<SimpleVertex> vertices = [];
        private object? builtPlayback;
        private int builtVersion = -1;
        private Vector3? builtPlayerPosition;

        /// <summary>Initializes the renderer and creates GPU resources.</summary>
        /// <param name="rendererContext">Renderer context for loading shaders.</param>
        public MovementPlaybackDebugRenderer(RendererContext rendererContext)
            : base(rendererContext, nameof(MovementPlaybackDebugRenderer))
        {
        }

        /// <summary>Draws the playback the player movement is running, if any.</summary>
        /// <param name="movement">The player movement.</param>
        /// <param name="drawPlayerHull">Whether to draw the replayed player's hull, for a camera that is not at their eyes.</param>
        public void Render(PlayerMovement movement, bool drawPlayerHull)
        {
            if (movement.Playback is not { } playback)
            {
                return;
            }

            Vector3? playerPosition = drawPlayerHull ? movement.RenderPosition : null;

            if (builtPlayback != playback || builtVersion != playback.Version || builtPlayerPosition != playerPosition)
            {
                builtPlayback = playback;
                builtVersion = playback.Version;
                builtPlayerPosition = playerPosition;
                Rebuild(movement, playerPosition);
            }

            RenderLines();
        }

        private void Rebuild(PlayerMovement movement, Vector3? playerPosition)
        {
            var playback = movement.Playback!;
            var halfExtents = movement.HullHalfExtents;
            vertices.Clear();

            AddPath(playback.ReplayedPath, ReplayedColor);
            AddPath(playback.ServerPath, ServerColor);

            if (playback.ServerOrigin is { } origin)
            {
                AddHull(origin, halfExtents, ServerColor);
            }

            if (playerPosition is { } feet)
            {
                AddHull(feet, halfExtents, ReplayedColor);
            }

            Upload(vertices);
        }

        private void AddHull(Vector3 feet, Vector3 halfExtents, Color32 color)
        {
            var hull = new AABB(
                feet - new Vector3(halfExtents.X, halfExtents.Y, 0f),
                feet + new Vector3(halfExtents.X, halfExtents.Y, halfExtents.Z * 2f));

            ShapeSceneNode.AddBox(vertices, hull, color);
        }

        private void AddPath(IReadOnlyList<Vector3> path, Color32 color)
        {
            for (var i = 1; i < path.Count; i++)
            {
                ShapeSceneNode.AddLine(vertices, path[i - 1], path[i], color);
            }
        }
    }
}
