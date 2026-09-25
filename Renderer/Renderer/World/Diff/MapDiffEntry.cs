using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Utils;

namespace ValveResourceFormat.Renderer.World.Diff;

/// <summary>What happened to something between two builds of a map.</summary>
public enum MapDiffKind
{
    /// <summary>Only in the new build.</summary>
    Added,

    /// <summary>Only in the old build.</summary>
    Removed,

    /// <summary>In both, placed differently but otherwise the same.</summary>
    Moved,

    /// <summary>In both, with different properties or appearance.</summary>
    Modified,
}

/// <summary>What kind of thing a <see cref="MapDiffEntry"/> is about.</summary>
public enum MapDiffCategory
{
    /// <summary>An entity from the entity lumps.</summary>
    Entity,

    /// <summary>Static geometry from the world nodes: world meshes and props baked into the map.</summary>
    Geometry,

    /// <summary>The world's collision, such as player and grenade clips.</summary>
    Collision,
}

/// <summary>One value that differs between the two builds.</summary>
/// <param name="Key">The keyvalue, or a description of what changed.</param>
/// <param name="OldValue">The value in the old build, <see langword="null"/> when it has none.</param>
/// <param name="NewValue">The value in the new build, <see langword="null"/> when it has none.</param>
public readonly record struct MapDiffPropertyChange(string Key, string? OldValue, string? NewValue);

/// <summary>One difference between two builds of a map.</summary>
public sealed class MapDiffEntry
{
    /// <summary>Gets what happened.</summary>
    public required MapDiffKind Kind { get; init; }

    /// <summary>Gets what kind of thing changed.</summary>
    public required MapDiffCategory Category { get; init; }

    /// <summary>Gets the entity class, or the kind of geometry.</summary>
    public required string Type { get; init; }

    /// <summary>Gets a short name for what changed, such as a target name or model.</summary>
    public required string Name { get; init; }

    /// <summary>Gets a one line summary of the change.</summary>
    public required string Detail { get; init; }

    /// <summary>Gets where it is in the old build, when it is there.</summary>
    public AABB? OldBounds { get; init; }

    /// <summary>Gets where it is in the new build, when it is there.</summary>
    public AABB? NewBounds { get; init; }

    /// <summary>Gets the entity in the old build, for entity changes.</summary>
    public EntityLump.Entity? OldEntity { get; init; }

    /// <summary>Gets the entity in the new build, for entity changes.</summary>
    public EntityLump.Entity? NewEntity { get; init; }

    /// <summary>Gets the individual values that differ.</summary>
    public IReadOnlyList<MapDiffPropertyChange> Changes { get; init; } = [];

    /// <summary>Gets where to look at the change: the new build's bounds, or the old build's when it was removed.</summary>
    public AABB Bounds => NewBounds ?? OldBounds ?? default;
}

/// <summary>Every difference found between two builds of a map.</summary>
public sealed class MapDiffResult
{
    /// <summary>Gets the differences, entities first, each group ordered by position.</summary>
    public required IReadOnlyList<MapDiffEntry> Entries { get; init; }

    /// <summary>Gets how many entities the old build has.</summary>
    public int OldEntityCount { get; init; }

    /// <summary>Gets how many entities the new build has.</summary>
    public int NewEntityCount { get; init; }

    /// <summary>Gets how many placed draws of static geometry the old build has.</summary>
    public int OldDrawCount { get; init; }

    /// <summary>Gets how many placed draws of static geometry the new build has.</summary>
    public int NewDrawCount { get; init; }
}
