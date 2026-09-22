using ValveResourceFormat.Renderer.World;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// A scene and the camera it is drawn through. A frame has one for the map, one for every map streamed
/// in next to it, and one for its 3D sky, and the first person viewmodel is drawn as a variation of the
/// map's.
/// </summary>
internal readonly record struct SceneView
{
    public required Scene Scene { get; init; }

    public required Camera Camera { get; init; }

    /// <summary>The 3D sky this view draws, or <see langword="null"/> when it draws the map itself.</summary>
    public Skybox3D? Skybox { get; init; }

    /// <summary>The frustum culling is frozen at, or <see langword="null"/> to follow <see cref="Camera"/>.</summary>
    public Frustum? LockedCullFrustum { get; init; }

    /// <summary>The camera whose light binning this view reuses, or <see langword="null"/> when it has its own.</summary>
    public Camera? BinnedFor { get; init; }

    /// <summary>
    /// The scene whose fog the view draws with instead of its own: a map streamed in next to the main one
    /// is seen through the same air as the main one.
    /// </summary>
    public Scene? FogFrom { get; init; }

    public WorldFogInfo Fog => Skybox?.FogInfo ?? (FogFrom ?? Scene).FogInfo;

    public FogSpace FogSpace => Skybox?.FogSpace ?? FogSpace.World;
}
