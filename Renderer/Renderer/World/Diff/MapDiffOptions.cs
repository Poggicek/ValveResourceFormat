namespace ValveResourceFormat.Renderer.World.Diff;

/// <summary>Tolerances for <see cref="MapDiffer"/>.</summary>
public sealed record MapDiffOptions
{
    /// <summary>Gets the distance in units under which a position counts as unchanged.</summary>
    public float PositionTolerance { get; init; } = 0.5f;

    /// <summary>Gets the angle in degrees under which a rotation counts as unchanged.</summary>
    public float AngleTolerance { get; init; } = 0.5f;

    /// <summary>Gets the relative difference under which two numeric keyvalues count as equal.</summary>
    public float NumberTolerance { get; init; } = 1e-3f;

    /// <summary>Gets how far an entity or prop can move and still be matched to itself rather than counted as removed and added.</summary>
    public float RematchRadius { get; init; } = 512f;

    /// <summary>Gets the grid step in units that vertex positions are compared at.</summary>
    public float VertexPrecision { get; init; } = 1f / 32f;

    /// <summary>Gets the area in square units under which a changed triangle is too thin to see and is ignored.</summary>
    public float MinimumTriangleArea { get; init; } = 0.25f;

    /// <summary>Gets the area in square units a change of geometry or collision has to cover to be reported.</summary>
    public float MinimumChangeArea { get; init; } = 4f;

    /// <summary>Gets how close changed triangles have to be to be reported as one change.</summary>
    public float ClusterRadius { get; init; } = 64f;

    /// <summary>
    /// Gets the keyvalues that the map compiler derives from other data, whose changes on their own do not
    /// make an entity count as modified.
    /// </summary>
    public IReadOnlyCollection<string> DerivedKeys { get; init; } = ["hammeruniqueid", "compile_source_id", "handshake", "array_index"];

    /// <summary>Gets the prefixes of keyvalues treated like <see cref="DerivedKeys"/>.</summary>
    public IReadOnlyCollection<string> DerivedKeyPrefixes { get; init; } = ["precomputed", "light_probe_atlas"];
}
