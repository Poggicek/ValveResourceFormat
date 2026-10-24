namespace ValveResourceFormat.Renderer.Movement;

/// <summary>
/// An entity origin as the server networks it: per axis, a cell of the world and a float offset inside
/// it.
/// </summary>
/// <remarks>
/// The server moves the player with the exact origin and only splits it into cells to network it. The
/// split is exact except where the offset's float steps are coarser than the coordinate's, as with small
/// negative coordinates, whose offset is close to the cell size, so the networked origin can be a step
/// off the one the player carries on from.
/// </remarks>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/server/CNetworkOriginCellCoordQuantizedVector">CNetworkOriginCellCoordQuantizedVector</seealso>
internal static class NetworkOrigin
{
    /// <summary>The world's half extent, <c>MapMaxCoord</c> in <c>gameinfo.gi</c>.</summary>
    public const int MaxCoord = 1 << 14;

    // The server keeps cells twice the size of those sent, which are split from its exactly
    private const int CellSize = 1 << 10;
    private const int SentCellBits = 9;
    private const int CellCount = 2 * MaxCoord / CellSize;

    /// <summary>Gets the origin the server networks for <paramref name="origin"/>.</summary>
    public static Vector3 Round(Vector3 origin) => new(RoundAxis(origin.X), RoundAxis(origin.Y), RoundAxis(origin.Z));

    /// <summary>Gets a coordinate from the cell and offset inside it sent in a demo.</summary>
    public static float FromSentCell(int cell, float offset) => ((cell << SentCellBits) - MaxCoord) + offset;

    private static float RoundAxis(float coord)
    {
        var cell = coord < 0f
            ? (int)(MaxCoord + coord) / CellSize
            : ((int)coord / CellSize) + (MaxCoord / CellSize);

        if (MathF.Abs(coord) > MaxCoord)
        {
            cell = Math.Clamp(cell, 0, CellCount - 1);
            coord = Math.Clamp(coord, -MaxCoord, MaxCoord);
        }

        var cellStart = (cell * CellSize) - MaxCoord;
        return cellStart + Math.Clamp(coord - cellStart, 0f, CellSize);
    }
}
