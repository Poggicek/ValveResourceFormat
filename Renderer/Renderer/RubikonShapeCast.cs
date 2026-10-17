namespace ValveResourceFormat.Renderer;

/// <summary>
/// The result of sweeping an axis-aligned box against one collision shape, the way the game's
/// physics reports it to player movement.
/// </summary>
internal struct ShapeCastHit
{
    /// <summary>How far along the sweep the box stops, from 0 at its start to 1 at its end.</summary>
    public float Fraction;

    /// <summary>The surface normal at the hit, pointing back against the sweep.</summary>
    public Vector3 Normal;

    /// <summary>Whether the box already overlapped the shape at the start of the sweep.</summary>
    public bool StartSolid;

    /// <summary>The index of the mesh triangle that was hit, or -1 for anything else.</summary>
    public int TriangleIndex;

    /// <summary>Gets a result that has not hit anything yet.</summary>
    public static ShapeCastHit None => new() { Fraction = 1f, TriangleIndex = -1 };
}

/// <summary>
/// Box casts against Rubikon shapes that reproduce the game's player movement traces bit for bit:
/// the box stops <see cref="Skin"/> short of a surface, and the start-solid and grazing rules match.
/// </summary>
/// <remarks>
/// Every float expression keeps the operand order and association of the game's code, since float
/// addition is not associative. Do not "simplify" them.
/// </remarks>
internal static partial class RubikonShapeCast
{
    /// <summary>The gap a cast box keeps from every surface, 1/32 unit.</summary>
    public const float Skin = 0.03125f;

    // minss / maxss: the second operand is returned when either is NaN
    private static float SseMin(float a, float b) => a < b ? a : b;
    private static float SseMax(float a, float b) => a > b ? a : b;
}
