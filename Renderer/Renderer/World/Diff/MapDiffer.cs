using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Utils;

namespace ValveResourceFormat.Renderer.World.Diff;

/// <summary>
/// Finds every difference between two builds of a map.
/// </summary>
/// <remarks>
/// Entities are matched by their Hammer unique id, falling back to the nearest entity of the same class, name
/// and model. Static geometry has no identity that survives a compile, so it is compared as a whole: every
/// placed draw is fingerprinted by its triangles, material, tint and placement, and whatever is the same in
/// both builds cancels out however the compiler ordered, split or named it. What is left is compared
/// triangle by triangle in world space, and the triangles that still differ are grouped by where they are.
/// </remarks>
public static class MapDiffer
{
    private static readonly Vector3 PointHalfExtents = new(16f);

    /// <summary>Compares two builds of a map.</summary>
    /// <param name="oldMap">The old build.</param>
    /// <param name="newMap">The new build.</param>
    /// <param name="options">Tolerances, the same ones both builds were loaded with.</param>
    /// <param name="cancellationToken">Stops comparing.</param>
    public static MapDiffResult Compute(MapDiffSource oldMap, MapDiffSource newMap, MapDiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldMap);
        ArgumentNullException.ThrowIfNull(newMap);

        options ??= new MapDiffOptions();

        if (oldMap.Precision != options.VertexPrecision || newMap.Precision != options.VertexPrecision)
        {
            throw new ArgumentException("Both builds must be loaded with the vertex precision they are compared with.", nameof(options));
        }

        var entries = new List<MapDiffEntry>();

        DiffEntities(oldMap, newMap, options, entries);
        cancellationToken.ThrowIfCancellationRequested();
        DiffGeometry(oldMap, newMap, options, entries, cancellationToken);

        entries.Sort(static (a, b) =>
        {
            var order = a.Category.CompareTo(b.Category);
            order = order != 0 ? order : a.Kind.CompareTo(b.Kind);
            order = order != 0 ? order : string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);
            return order != 0 ? order : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new MapDiffResult
        {
            Entries = entries,
            OldEntityCount = oldMap.EntityCount,
            NewEntityCount = newMap.EntityCount,
            OldDrawCount = oldMap.DrawCount,
            NewDrawCount = newMap.DrawCount,
        };
    }

    private static void DiffEntities(MapDiffSource oldMap, MapDiffSource newMap, MapDiffOptions options, List<MapDiffEntry> entries)
    {
        var pairs = new List<(DiffEntity Old, DiffEntity New)>();
        var formatKeys = FormatKeys(oldMap, newMap);

        // Identical entities cancel out first, whatever their ids: the compiler renumbers the ids of entities
        // that come from prefabs, which would otherwise pair unchanged copies up with each other
        var matched = new HashSet<DiffEntity>(ReferenceEqualityComparer.Instance);
        var unchanged = new Dictionary<ulong, Queue<DiffEntity>>();

        foreach (var entity in oldMap.Entities)
        {
            var fingerprint = Fingerprint(entity, oldMap, options, formatKeys);

            if (!unchanged.TryGetValue(fingerprint, out var queue))
            {
                queue = new Queue<DiffEntity>();
                unchanged.Add(fingerprint, queue);
            }

            queue.Enqueue(entity);
        }

        foreach (var entity in newMap.Entities)
        {
            if (unchanged.TryGetValue(Fingerprint(entity, newMap, options, formatKeys), out var queue) && queue.TryDequeue(out var oldEntity))
            {
                matched.Add(oldEntity);
                matched.Add(entity);
            }
        }

        var oldById = UniqueIds([.. oldMap.Entities.Where(entity => !matched.Contains(entity))]);
        var newById = UniqueIds([.. newMap.Entities.Where(entity => !matched.Contains(entity))]);

        foreach (var (id, newEntity) in newById)
        {
            if (oldById.TryGetValue(id, out var oldEntity))
            {
                pairs.Add((oldEntity, newEntity));
                matched.Add(oldEntity);
                matched.Add(newEntity);
            }
        }

        // An entity deleted and placed again, or copied from a prefab, gets a new id
        var removed = new List<DiffEntity>();
        var added = new List<DiffEntity>();

        MatchNearest(
            [.. oldMap.Entities.Where(entity => !matched.Contains(entity))],
            [.. newMap.Entities.Where(entity => !matched.Contains(entity))],
            static entity => (entity.Classname, entity.TargetName, entity.ModelKey),
            static entity => entity.Origin,
            options.RematchRadius,
            pairs, removed, added);

        foreach (var (oldEntity, newEntity) in pairs)
        {
            if (CompareEntities(oldMap, oldEntity, newMap, newEntity, options, formatKeys) is { } entry)
            {
                entries.Add(entry);
            }
        }

        foreach (var entity in removed)
        {
            entries.Add(EntityEntry(MapDiffKind.Removed, entity, oldMap, null, null, "removed", [.. AllValues(entity.Entity, oldMap, options, isOld: true)]));
        }

        foreach (var entity in added)
        {
            entries.Add(EntityEntry(MapDiffKind.Added, null, null, entity, newMap, "added", [.. AllValues(entity.Entity, newMap, options, isOld: false)]));
        }
    }

    /// <summary>
    /// Finds the keys one build writes on (nearly) every entity of a class and the other build on none of them.
    /// Those come from a newer compiler writing out new fields with their defaults, not from anyone editing the map.
    /// </summary>
    private static HashSet<(string Classname, string Key)> FormatKeys(MapDiffSource oldMap, MapDiffSource newMap)
    {
        static Dictionary<string, (int Count, Dictionary<string, int> Keys)> Coverage(MapDiffSource map)
        {
            var coverage = new Dictionary<string, (int Count, Dictionary<string, int> Keys)>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in map.Entities)
            {
                if (!coverage.TryGetValue(entity.Classname, out var entry))
                {
                    entry = (0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
                }

                foreach (var child in entity.Entity.Children)
                {
                    entry.Keys[child.Key] = entry.Keys.GetValueOrDefault(child.Key) + 1;
                }

                coverage[entity.Classname] = entry with { Count = entry.Count + 1 };
            }

            return coverage;
        }

        var formatKeys = new HashSet<(string Classname, string Key)>();
        var oldCoverage = Coverage(oldMap);
        var newCoverage = Coverage(newMap);

        foreach (var (withKey, withoutKey) in new[] { (oldCoverage, newCoverage), (newCoverage, oldCoverage) })
        {
            foreach (var (classname, (count, keys)) in withKey)
            {
                if (!withoutKey.TryGetValue(classname, out var other))
                {
                    continue;
                }

                foreach (var (key, keyCount) in keys)
                {
                    if (keyCount >= count * 0.9f && !other.Keys.ContainsKey(key))
                    {
                        formatKeys.Add((classname.ToLowerInvariant(), key.ToLowerInvariant()));
                    }
                }
            }
        }

        return formatKeys;
    }

    private static bool IsFormatKey(HashSet<(string Classname, string Key)> formatKeys, string classname, string key)
        => formatKeys.Contains((classname.ToLowerInvariant(), key.ToLowerInvariant()));

    /// <summary>Hashes everything about an entity that the comparison looks at, with numbers rounded.</summary>
    private static ulong Fingerprint(DiffEntity entity, MapDiffSource map, MapDiffOptions options, HashSet<(string Classname, string Key)> formatKeys)
    {
        var values = Values(entity.Entity, map)
            .Where(pair => pair.Value.Length > 0 && !IsDerived(pair.Key, options) && !IsFormatKey(formatKeys, entity.Classname, pair.Key))
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase);

        var hash = 0UL;

        foreach (var (key, value) in values)
        {
            hash = DiffHash.Combine(hash, DiffHash.String(key));

            foreach (var token in value.Split(ValueSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                // Float noise between compiles, like 180.000015 and 180.000046, must not tell entities apart
                var normalized = double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? (Math.Round(number, 2) + 0.0).ToString(CultureInfo.InvariantCulture)
                    : token;

                hash = DiffHash.Combine(hash, DiffHash.String(normalized));
            }
        }

        var connections = 0UL;

        foreach (var connection in entity.Entity.Connections ?? [])
        {
            connections += DiffHash.String(Describe(connection));
        }

        return DiffHash.Combine(hash, connections);
    }

    private static readonly char[] ValueSeparators = [' ', ',', '[', ']', '\t', '\n', '\r'];

    private static Dictionary<string, DiffEntity> UniqueIds(List<DiffEntity> entities)
    {
        var byId = new Dictionary<string, DiffEntity>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            if (entity.UniqueId is { Length: > 0 } id && !byId.TryAdd(id, entity))
            {
                duplicates.Add(id);
            }
        }

        // Ids repeated by templates cannot tell the copies apart
        foreach (var id in duplicates)
        {
            byId.Remove(id);
        }

        return byId;
    }

    private static MapDiffEntry? CompareEntities(MapDiffSource oldMap, DiffEntity oldEntity, MapDiffSource newMap, DiffEntity newEntity, MapDiffOptions options,
        HashSet<(string Classname, string Key)> formatKeys)
    {
        var oldValues = Values(oldEntity.Entity, oldMap);
        var newValues = Values(newEntity.Entity, newMap);
        var changes = new List<MapDiffPropertyChange>();
        var significant = new List<string>();
        var distance = Vector3.Distance(oldEntity.Origin, newEntity.Origin);
        var moved = false;
        var rotated = false;

        foreach (var key in oldValues.Keys.Concat(newValues.Keys.Where(key => !oldValues.ContainsKey(key))))
        {
            if (IsHidden(key, options))
            {
                continue;
            }

            var oldValue = oldValues.GetValueOrDefault(key);
            var newValue = newValues.GetValueOrDefault(key);

            // An empty value is written by some compilers where others leave the key out
            if (string.IsNullOrEmpty(oldValue) && string.IsNullOrEmpty(newValue))
            {
                continue;
            }

            if ((oldValue == null || newValue == null) && IsFormatKey(formatKeys, newEntity.Classname, key))
            {
                continue;
            }

            // Compared by what they contain below, where a change is reported as geometry or collision
            if (key == "model" && IsCompiledWithMap(oldEntity.Model) && IsCompiledWithMap(newEntity.Model))
            {
                continue;
            }

            if (key == "origin")
            {
                // Compared where the entity ends up, so a template that moved moves its children too
                if (distance <= options.PositionTolerance)
                {
                    continue;
                }

                moved = true;
            }
            else if (key == "angles")
            {
                if (!AnglesDiffer(oldValue, newValue, options.AngleTolerance))
                {
                    continue;
                }

                rotated = true;
            }
            else if (ValuesEqual(oldValue, newValue, options.NumberTolerance))
            {
                continue;
            }

            changes.Add(new MapDiffPropertyChange(key, oldValue, newValue));

            if (!IsDerived(key, options))
            {
                significant.Add(key);
            }
        }

        foreach (var (description, isOld) in DiffConnections(oldEntity.Entity, newEntity.Entity))
        {
            changes.Add(new MapDiffPropertyChange("output", isOld ? description : null, isOld ? null : description));
            significant.Add("outputs");
        }

        // A brush entity's model is compiled with the map, and can change without any of its keys changing
        if (oldEntity.ModelKey.Length > 0 && oldEntity.ModelKey == newEntity.ModelKey && IsCompiledWithMap(oldEntity.Model)
            && oldMap.Geometry.Get(oldEntity.Model) is { } oldModel && newMap.Geometry.Get(newEntity.Model) is { } newModel)
        {
            if (oldModel.ContentHash != newModel.ContentHash)
            {
                changes.Add(new MapDiffPropertyChange("model geometry", Triangles(oldModel), Triangles(newModel)));
                significant.Add("model geometry");
            }

            // Clips and triggers are nothing but collision, which compilers store with enough float noise to need a
            // closer look than the hash
            if (oldModel.CollisionHash != newModel.CollisionHash
                && !SameSurface(oldModel.CollisionTriangles, newModel.CollisionTriangles, options.PositionTolerance))
            {
                changes.Add(new MapDiffPropertyChange("model collision",
                    string.Create(CultureInfo.InvariantCulture, $"{oldModel.CollisionTriangleCount} triangles"),
                    string.Create(CultureInfo.InvariantCulture, $"{newModel.CollisionTriangleCount} triangles")));
                significant.Add("model collision");
            }
        }

        if (significant.Count == 0)
        {
            return null;
        }

        var onlyPlacement = significant.All(static key => key is "origin" or "angles");
        var kind = onlyPlacement ? MapDiffKind.Moved : MapDiffKind.Modified;

        var detail = onlyPlacement
            ? (moved, rotated) switch
            {
                (true, true) => $"moved {distance:0} units and rotated",
                (true, false) => $"moved {distance:0} units",
                _ => "rotated",
            }
            : significant.Distinct().Count() == 1
                ? $"{significant[0]} changed"
                : $"{significant.Distinct().Count()} changes";

        return EntityEntry(kind, oldEntity, oldMap, newEntity, newMap, detail, changes);
    }

    /// <summary>Whether every triangle of each set lies on the surface of the other, as far as a few points on it show.</summary>
    private static bool SameSurface(Vector3[] a, Vector3[] b, float tolerance)
    {
        // Big enough to be worth comparing by the hash alone
        if ((long)a.Length * b.Length > 9_000_000)
        {
            return false;
        }

        var toleranceSquared = tolerance * tolerance;

        bool OnSurface(Vector3 point, Vector3[] triangles)
        {
            for (var i = 0; i + 2 < triangles.Length; i += 3)
            {
                if (Vector3.DistanceSquared(point, MathUtils.ClosestPointOnTriangle(point, triangles[i], triangles[i + 1], triangles[i + 2])) <= toleranceSquared)
                {
                    return true;
                }
            }

            return false;
        }

        bool Covered(Vector3[] triangles, Vector3[] by)
        {
            for (var i = 0; i + 2 < triangles.Length; i += 3)
            {
                var center = (triangles[i] + triangles[i + 1] + triangles[i + 2]) / 3f;

                if (!OnSurface(center, by)
                    || !OnSurface(Vector3.Lerp(triangles[i], center, 0.05f), by)
                    || !OnSurface(Vector3.Lerp(triangles[i + 1], center, 0.05f), by)
                    || !OnSurface(Vector3.Lerp(triangles[i + 2], center, 0.05f), by))
                {
                    return false;
                }
            }

            return true;
        }

        return Covered(a, b) && Covered(b, a);
    }

    private static string Triangles(DiffModel model) => $"{model.Draws.Sum(static draw => draw.TriangleCount)} triangles";

    private static MapDiffEntry EntityEntry(MapDiffKind kind, DiffEntity? oldEntity, MapDiffSource? oldMap, DiffEntity? newEntity, MapDiffSource? newMap,
        string detail, List<MapDiffPropertyChange> changes)
    {
        var entity = newEntity ?? oldEntity!;
        var name = entity.TargetName.Length > 0
            ? entity.TargetName
            : entity.Model.Length > 0 ? Path.GetFileNameWithoutExtension(entity.Model) : string.Empty;

        return new MapDiffEntry
        {
            Kind = kind,
            Category = MapDiffCategory.Entity,
            Type = entity.Classname,
            Name = name,
            Detail = detail,
            OldBounds = oldEntity != null ? EntityBounds(oldEntity, oldMap!) : null,
            NewBounds = newEntity != null ? EntityBounds(newEntity, newMap!) : null,
            OldEntity = oldEntity?.Entity,
            NewEntity = newEntity?.Entity,
            Changes = changes,
        };
    }

    private static AABB EntityBounds(DiffEntity entity, MapDiffSource map)
    {
        // Models from the base game would all have to be read for this, the viewer has them loaded anyway
        if (IsCompiledWithMap(entity.Model) && map.Geometry.Get(entity.Model) is { } model)
        {
            if (model.Draws.Count > 0)
            {
                return model.Bounds.Transform(entity.Transform);
            }

            if (model.CollisionBounds is { } collisionBounds)
            {
                return collisionBounds.Transform(entity.Transform);
            }
        }

        return new AABB(entity.Origin - PointHalfExtents, entity.Origin + PointHalfExtents);
    }

    private static bool IsCompiledWithMap(string model) => model.StartsWith("maps/", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> Values(EntityLump.Entity entity, MapDiffSource map)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in entity.Children)
        {
            var value = child.Value.ValueType == KVValueType.String ? child.Value.ToString(CultureInfo.InvariantCulture) : EntityLump.StringifyValue(child.Value);

            // Older compilers write booleans as words and paths with either slash
            value = value.Replace('\\', '/') switch
            {
                var text when text.Equals("true", StringComparison.OrdinalIgnoreCase) => "1",
                var text when text.Equals("false", StringComparison.OrdinalIgnoreCase) => "0",
                var text => text,
            };

            values.TryAdd(child.Key, map.Normalize(value));
        }

        // A brush's model is compiled with the map and named after its Hammer id, which gets renumbered, so the
        // same brush is known by what its model contains instead
        if (entity.GetStringProperty("model")?.Replace('\\', '/') is { } model && IsCompiledWithMap(model) && values.ContainsKey("model"))
        {
            values["model"] = map.Geometry.Get(model) is { } compiled
                ? string.Create(CultureInfo.InvariantCulture, $"compiled {DiffHash.Combine(compiled.ContentHash, compiled.CollisionHash):x16}")
                : "compiled";
        }

        return values;
    }

    private static IEnumerable<MapDiffPropertyChange> AllValues(EntityLump.Entity entity, MapDiffSource map, MapDiffOptions options, bool isOld)
    {
        foreach (var (key, value) in Values(entity, map))
        {
            if (!IsHidden(key, options))
            {
                yield return new MapDiffPropertyChange(key, isOld ? value : null, isOld ? null : value);
            }
        }

        foreach (var connection in entity.Connections ?? [])
        {
            var description = Describe(connection);
            yield return new MapDiffPropertyChange("output", isOld ? description : null, isOld ? null : description);
        }
    }

    // Bookkeeping the compiler writes, which says nothing about the map and is left out of the list entirely
    private static bool IsHidden(string key, MapDiffOptions options)
        => key is "hammeruniqueid" or "compile_source_id" && options.DerivedKeys.Contains(key);

    private static bool IsDerived(string key, MapDiffOptions options)
        => options.DerivedKeys.Contains(key) || options.DerivedKeyPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool ValuesEqual(string? a, string? b, float tolerance)
    {
        if (a == b)
        {
            return true;
        }

        if (a == null || b == null)
        {
            return false;
        }

        var tokensA = a.Split([' ', ',', '[', ']', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var tokensB = b.Split([' ', ',', '[', ']', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        if (tokensA.Length != tokensB.Length || tokensA.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < tokensA.Length; i++)
        {
            if (tokensA[i] == tokensB[i])
            {
                continue;
            }

            if (!double.TryParse(tokensA[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(tokensB[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                return false;
            }

            if (Math.Abs(x - y) > tolerance * Math.Max(1.0, Math.Max(Math.Abs(x), Math.Abs(y))))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnglesDiffer(string? a, string? b, float tolerance)
    {
        if (a == b)
        {
            return false;
        }

        // Older compilers write "p y r", newer ones [ p, y, r ]
        static bool TryParse(string? value, out Vector3 angles)
        {
            angles = default;
            var tokens = value?.Split(ValueSeparators, StringSplitOptions.RemoveEmptyEntries);

            return tokens is { Length: 3 }
                && float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out angles.X)
                && float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out angles.Y)
                && float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out angles.Z);
        }

        if (!TryParse(a, out var anglesA) || !TryParse(b, out var anglesB))
        {
            return true;
        }

        static float Difference(float x, float y) => MathF.Abs(MathUtils.Wrap(x - y + 180f, 0f, 360f) - 180f);

        return Difference(anglesA.X, anglesB.X) > tolerance
            || Difference(anglesA.Y, anglesB.Y) > tolerance
            || Difference(anglesA.Z, anglesB.Z) > tolerance;
    }

    private static IEnumerable<(string Description, bool IsOld)> DiffConnections(EntityLump.Entity oldEntity, EntityLump.Entity newEntity)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var connection in oldEntity.Connections ?? [])
        {
            var description = Describe(connection);
            remaining[description] = remaining.GetValueOrDefault(description) + 1;
        }

        foreach (var connection in newEntity.Connections ?? [])
        {
            var description = Describe(connection);

            if (remaining.GetValueOrDefault(description) > 0)
            {
                remaining[description]--;
            }
            else
            {
                yield return (description, false);
            }
        }

        foreach (var (description, count) in remaining)
        {
            for (var i = 0; i < count; i++)
            {
                yield return (description, true);
            }
        }
    }

    private static string Describe(EntityLump.Connection connection)
    {
        var parameter = string.IsNullOrEmpty(connection.OverrideParam) ? string.Empty : $"({connection.OverrideParam})";
        var delay = connection.Delay > 0 ? string.Create(CultureInfo.InvariantCulture, $" after {connection.Delay:0.##}s") : string.Empty;
        var times = connection.TimesToFire > 0 ? $" x{connection.TimesToFire}" : string.Empty;

        return $"{connection.OutputName} -> {connection.TargetName}.{connection.InputName}{parameter}{delay}{times}";
    }

    // Previous is set on a triangle only its tint or material changed on, to how it was drawn in the old build
    private readonly record struct ChangedTriangle(DiffPlacedDraw Draw, Vector3 A, Vector3 B, Vector3 C, bool IsNew, DiffPlacedDraw? Previous = null)
    {
        public AABB Bounds => new(Vector3.Min(A, Vector3.Min(B, C)), Vector3.Max(A, Vector3.Max(B, C)));

        public Vector3 Center => (A + B + C) / 3f;

        public float Area => Vector3.Cross(B - A, C - A).Length() * 0.5f;
    }

    private static void DiffGeometry(MapDiffSource oldMap, MapDiffSource newMap, MapDiffOptions options, List<MapDiffEntry> entries, CancellationToken cancellationToken)
    {
        var (oldLeft, newLeft) = UnmatchedDraws(oldMap, newMap);

        if (oldLeft.Count == 0 && newLeft.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var changed = UnmatchedTriangles(oldLeft, newLeft, options, cancellationToken);
        changed = CancelNearMatches(changed, options.PositionTolerance);

        // Slivers left over from clipping decals and splitting faces cannot be seen
        changed.RemoveAll(triangle => triangle.Area < options.MinimumTriangleArea);

        // Surfaces only cut up differently go first, or their triangles could be paired with a flush surface of
        // another collision group or material and reported as having changed it
        cancellationToken.ThrowIfCancellationRequested();
        changed = DropCoveredTriangles(changed, options.PositionTolerance);

        var restyled = PairRestyledTriangles(changed, 1f / options.VertexPrecision);

        foreach (var isCollision in (ReadOnlySpan<bool>)[false, true])
        {
            foreach (var cluster in Cluster([.. restyled.Where(triangle => triangle.Draw.Owner.IsCollision == isCollision)], options.ClusterRadius))
            {
                entries.Add(RestyledEntry(cluster));
            }
        }

        // Collision is grouped apart from what is drawn, so a clip placed over a wall is its own change
        var clusters = Cluster([.. changed.Where(static triangle => !triangle.Draw.Owner.IsCollision)], options.ClusterRadius)
            .Concat(Cluster([.. changed.Where(static triangle => triangle.Draw.Owner.IsCollision)], options.ClusterRadius));

        AddGeometryEntries([.. clusters.Where(cluster => cluster.Sum(static triangle => triangle.Area) >= options.MinimumChangeArea)], options, entries);
    }

    /// <summary>
    /// Reports the changed regions, pairing one only in the old build with one of the same shape only in the new
    /// build nearby as something that moved. This runs on what is left once identical triangles have cancelled out:
    /// the compiler regroups aggregated props between compiles, so whole objects cannot be matched up before that.
    /// </summary>
    private static void AddGeometryEntries(List<List<ChangedTriangle>> clusters, MapDiffOptions options, List<MapDiffEntry> entries)
    {
        var inversePrecision = 1f / options.VertexPrecision;
        var pairs = new List<(List<ChangedTriangle> Old, List<ChangedTriangle> New)>();

        MatchNearest(
            [.. clusters.Where(static cluster => cluster.TrueForAll(static triangle => !triangle.IsNew))],
            [.. clusters.Where(static cluster => cluster.TrueForAll(static triangle => triangle.IsNew))],
            cluster => ShapeOf(cluster, inversePrecision),
            static cluster => BoundsOf(cluster).Center,
            options.RematchRadius,
            pairs, [], []);

        var moved = new HashSet<List<ChangedTriangle>>(ReferenceEqualityComparer.Instance);

        foreach (var (oldCluster, newCluster) in pairs)
        {
            moved.Add(oldCluster);
            moved.Add(newCluster);
            entries.Add(MovedEntry(oldCluster, newCluster));
        }

        foreach (var cluster in clusters)
        {
            if (moved.Contains(cluster))
            {
                continue;
            }

            // Moved by less than the grouping radius, where it was and where it is are one region
            List<ChangedTriangle> before = [.. cluster.Where(static triangle => !triangle.IsNew)];
            List<ChangedTriangle> after = [.. cluster.Where(static triangle => triangle.IsNew)];

            if (before.Count > 0 && after.Count > 0 && ShapeOf(before, inversePrecision) == ShapeOf(after, inversePrecision))
            {
                entries.Add(MovedEntry(before, after));
                continue;
            }

            entries.Add(GeometryEntry(cluster));
        }
    }

    /// <summary>Hashes a region's triangles and how they look, relative to the corner of its bounds so wherever it is.</summary>
    private static ulong ShapeOf(List<ChangedTriangle> cluster, float inversePrecision)
    {
        var min = BoundsOf(cluster).Min;
        var hash = 0UL;

        foreach (var triangle in cluster)
        {
            hash += GridPoint.Triangle(
                GridPoint.From(triangle.A - min, inversePrecision),
                GridPoint.From(triangle.B - min, inversePrecision),
                GridPoint.From(triangle.C - min, inversePrecision),
                triangle.Draw.AppearanceSeed);
        }

        return DiffHash.Combine(hash, cluster.Count);
    }

    private static AABB BoundsOf(List<ChangedTriangle> cluster)
    {
        var bounds = cluster[0].Bounds;

        foreach (var triangle in cluster)
        {
            bounds = bounds.Union(triangle.Bounds);
        }

        return bounds;
    }

    private static MapDiffEntry MovedEntry(List<ChangedTriangle> oldCluster, List<ChangedTriangle> newCluster)
    {
        var oldBounds = BoundsOf(oldCluster);
        var newBounds = BoundsOf(newCluster);
        var draw = newCluster[0].Draw;
        var isCollision = draw.Owner.IsCollision;
        var distance = Vector3.Distance(oldBounds.Center, newBounds.Center);

        return new MapDiffEntry
        {
            Kind = MapDiffKind.Moved,
            Category = isCollision ? MapDiffCategory.Collision : MapDiffCategory.Geometry,
            Type = isCollision ? "Collision" : GeometryType(draw.Owner.Model),
            Name = isCollision ? draw.Material.Trim() : Path.GetFileNameWithoutExtension(draw.Material),
            Detail = string.Create(CultureInfo.InvariantCulture, $"moved {distance:0} units"),
            OldBounds = oldBounds,
            NewBounds = newBounds,
            Changes =
            [
                new("position", FormatVector(oldBounds.Center), FormatVector(newBounds.Center)),
                new("triangles", oldCluster.Count.ToString(CultureInfo.InvariantCulture), newCluster.Count.ToString(CultureInfo.InvariantCulture)),
            ],
        };
    }

    /// <summary>The placed draws of each build that have no identical draw in the other.</summary>
    private static (List<DiffPlacedDraw> Old, List<DiffPlacedDraw> New) UnmatchedDraws(MapDiffSource oldMap, MapDiffSource newMap)
    {
        var remaining = new Dictionary<ulong, int>();

        foreach (var draw in oldMap.Objects.SelectMany(static obj => obj.Draws))
        {
            remaining[draw.Fingerprint] = remaining.GetValueOrDefault(draw.Fingerprint) + 1;
        }

        var newLeft = new List<DiffPlacedDraw>();

        foreach (var draw in newMap.Objects.SelectMany(static obj => obj.Draws))
        {
            if (remaining.GetValueOrDefault(draw.Fingerprint) > 0)
            {
                remaining[draw.Fingerprint]--;
            }
            else
            {
                newLeft.Add(draw);
            }
        }

        var oldLeft = new List<DiffPlacedDraw>();

        foreach (var draw in oldMap.Objects.SelectMany(static obj => obj.Draws))
        {
            if (remaining.GetValueOrDefault(draw.Fingerprint) > 0)
            {
                remaining[draw.Fingerprint]--;
                oldLeft.Add(draw);
            }
        }

        return (oldLeft, newLeft);
    }

    private static string GeometryType(string model) => IsCompiledWithMap(model) ? "World geometry" : "Prop";

    private static string FormatVector(Vector3 vector) => string.Create(CultureInfo.InvariantCulture, $"{vector.X:0.##} {vector.Y:0.##} {vector.Z:0.##}");

    /// <summary>The triangles of the unmatched draws that are not in the other build's unmatched draws, in world space.</summary>
    private static List<ChangedTriangle> UnmatchedTriangles(List<DiffPlacedDraw> oldLeft, List<DiffPlacedDraw> newLeft, MapDiffOptions options, CancellationToken cancellationToken)
    {
        var inversePrecision = 1f / options.VertexPrecision;
        var remaining = new Dictionary<ulong, int>();

        foreach (var draw in oldLeft)
        {
            foreach (var (hash, _, _, _) in WorldTriangles(draw, inversePrecision))
            {
                remaining[hash] = remaining.GetValueOrDefault(hash) + 1;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var changed = new List<ChangedTriangle>();
        var matched = new Dictionary<ulong, int>();

        foreach (var draw in newLeft)
        {
            foreach (var (hash, a, b, c) in WorldTriangles(draw, inversePrecision))
            {
                if (remaining.GetValueOrDefault(hash) > 0)
                {
                    remaining[hash]--;
                    matched[hash] = matched.GetValueOrDefault(hash) + 1;
                }
                else
                {
                    changed.Add(new ChangedTriangle(draw, a, b, c, IsNew: true));
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var draw in oldLeft)
        {
            foreach (var (hash, a, b, c) in WorldTriangles(draw, inversePrecision))
            {
                if (matched.GetValueOrDefault(hash) > 0)
                {
                    matched[hash]--;
                }
                else
                {
                    changed.Add(new ChangedTriangle(draw, a, b, c, IsNew: false));
                }
            }
        }

        return changed;
    }

    private static IEnumerable<(ulong Hash, Vector3 A, Vector3 B, Vector3 C)> WorldTriangles(DiffPlacedDraw placed, float inversePrecision)
    {
        var draw = placed.Draw;
        var transform = placed.Owner.Transform;
        var positions = draw.Positions;
        var indices = draw.Indices;

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var a = Vector3.Transform(positions[indices[i]], transform);
            var b = Vector3.Transform(positions[indices[i + 1]], transform);
            var c = Vector3.Transform(positions[indices[i + 2]], transform);

            var hash = GridPoint.Triangle(
                GridPoint.From(a, inversePrecision),
                GridPoint.From(b, inversePrecision),
                GridPoint.From(c, inversePrecision),
                placed.AppearanceSeed);

            yield return (hash, a, b, c);
        }
    }

    /// <summary>
    /// Drops pairs of a removed and an added triangle whose corners are all within <paramref name="tolerance"/> of
    /// each other. A recompile moves vertices by float noise, which sends one on the edge of the grid cell they
    /// are compared in to the neighbouring cell.
    /// </summary>
    private static List<ChangedTriangle> CancelNearMatches(List<ChangedTriangle> changed, float tolerance)
    {
        var cellSize = Math.Max(tolerance * 2f, 1f);
        var removedByCell = new Dictionary<GridPoint, List<int>>();

        GridPoint CellOf(Vector3 point)
        {
            var scaled = point / cellSize;
            return new GridPoint((int)MathF.Floor(scaled.X), (int)MathF.Floor(scaled.Y), (int)MathF.Floor(scaled.Z));
        }

        for (var i = 0; i < changed.Count; i++)
        {
            if (changed[i].IsNew)
            {
                continue;
            }

            var cell = CellOf(changed[i].Center);

            if (!removedByCell.TryGetValue(cell, out var list))
            {
                list = [];
                removedByCell.Add(cell, list);
            }

            list.Add(i);
        }

        var cancelled = new bool[changed.Count];
        var toleranceSquared = tolerance * tolerance;

        bool CornersNear(in ChangedTriangle x, in ChangedTriangle y)
        {
            foreach (var corner in (ReadOnlySpan<Vector3>)[x.A, x.B, x.C])
            {
                if (Vector3.DistanceSquared(corner, y.A) > toleranceSquared
                    && Vector3.DistanceSquared(corner, y.B) > toleranceSquared
                    && Vector3.DistanceSquared(corner, y.C) > toleranceSquared)
                {
                    return false;
                }
            }

            return true;
        }

        int FindNear(in ChangedTriangle added)
        {
            var cell = CellOf(added.Center);

            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    for (var z = -1; z <= 1; z++)
                    {
                        if (!removedByCell.TryGetValue(new GridPoint(cell.X + x, cell.Y + y, cell.Z + z), out var candidates))
                        {
                            continue;
                        }

                        foreach (var candidate in candidates)
                        {
                            var removed = changed[candidate];

                            if (!cancelled[candidate] && removed.Draw.AppearanceSeed == added.Draw.AppearanceSeed
                                && CornersNear(added, removed) && CornersNear(removed, added))
                            {
                                return candidate;
                            }
                        }
                    }
                }
            }

            return -1;
        }

        for (var i = 0; i < changed.Count; i++)
        {
            if (!changed[i].IsNew)
            {
                continue;
            }

            var found = FindNear(changed[i]);

            if (found >= 0)
            {
                cancelled[found] = true;
                cancelled[i] = true;
            }
        }

        return [.. changed.Where((_, index) => !cancelled[index])];
    }

    /// <summary>
    /// Drops the triangles whose whole surface the other build also has, with the same appearance. The compiler cuts
    /// the same surface into different triangles from one compile to the next, whole maps of collision at once, and
    /// decals are cut again when the geometry under them is.
    /// </summary>
    private static List<ChangedTriangle> DropCoveredTriangles(List<ChangedTriangle> changed, float tolerance)
    {
        const float CellSize = 128f;
        const int MaxCellsPerAxis = 128;

        var removed = new Dictionary<GridPoint, List<int>>();
        var added = new Dictionary<GridPoint, List<int>>();

        static GridPoint CellOf(Vector3 point)
            => new((int)MathF.Floor(point.X / CellSize), (int)MathF.Floor(point.Y / CellSize), (int)MathF.Floor(point.Z / CellSize));

        for (var i = 0; i < changed.Count; i++)
        {
            var index = changed[i].IsNew ? added : removed;
            var bounds = changed[i].Bounds;
            var from = CellOf(bounds.Min - new Vector3(tolerance));
            var to = CellOf(bounds.Max + new Vector3(tolerance));

            for (var x = from.X; x <= Math.Min(to.X, from.X + MaxCellsPerAxis); x++)
            {
                for (var y = from.Y; y <= Math.Min(to.Y, from.Y + MaxCellsPerAxis); y++)
                {
                    for (var z = from.Z; z <= Math.Min(to.Z, from.Z + MaxCellsPerAxis); z++)
                    {
                        var cell = new GridPoint(x, y, z);

                        if (!index.TryGetValue(cell, out var list))
                        {
                            list = [];
                            index.Add(cell, list);
                        }

                        list.Add(i);
                    }
                }
            }
        }

        var toleranceSquared = tolerance * tolerance;

        bool IsOnSurface(Vector3 point, ulong appearance, Dictionary<GridPoint, List<int>> other)
        {
            if (!other.TryGetValue(CellOf(point), out var candidates))
            {
                return false;
            }

            foreach (var candidate in candidates)
            {
                var triangle = changed[candidate];

                if (triangle.Draw.AppearanceSeed == appearance
                    && Vector3.DistanceSquared(point, MathUtils.ClosestPointOnTriangle(point, triangle.A, triangle.B, triangle.C)) <= toleranceSquared)
                {
                    return true;
                }
            }

            return false;
        }

        bool IsCovered(in ChangedTriangle triangle)
        {
            var other = triangle.IsNew ? removed : added;
            var appearance = triangle.Draw.AppearanceSeed;
            var center = triangle.Center;

            // The centre, and points just inside each corner and edge, so a surface only partly covered is kept
            ReadOnlySpan<Vector3> samples =
            [
                center,
                Vector3.Lerp(triangle.A, center, 0.05f),
                Vector3.Lerp(triangle.B, center, 0.05f),
                Vector3.Lerp(triangle.C, center, 0.05f),
                Vector3.Lerp((triangle.A + triangle.B) * 0.5f, center, 0.05f),
                Vector3.Lerp((triangle.B + triangle.C) * 0.5f, center, 0.05f),
                Vector3.Lerp((triangle.C + triangle.A) * 0.5f, center, 0.05f),
            ];

            foreach (var sample in samples)
            {
                if (!IsOnSurface(sample, appearance, other))
                {
                    return false;
                }
            }

            return true;
        }

        return [.. changed.Where(triangle => !IsCovered(triangle))];
    }

    /// <summary>
    /// Takes the pairs of a removed and an added triangle in the same place out of <paramref name="changed"/>: the
    /// same surface, drawn with another tint or material. Returns the added ones, each with how it was drawn before.
    /// </summary>
    private static List<ChangedTriangle> PairRestyledTriangles(List<ChangedTriangle> changed, float inversePrecision)
    {
        static ulong Place(in ChangedTriangle triangle, float inversePrecision)
            => GridPoint.Triangle(GridPoint.From(triangle.A, inversePrecision), GridPoint.From(triangle.B, inversePrecision), GridPoint.From(triangle.C, inversePrecision),
                triangle.Draw.Owner.IsCollision ? 1UL : 0UL);

        var removedByPlace = new Dictionary<ulong, Stack<int>>();

        for (var i = 0; i < changed.Count; i++)
        {
            if (changed[i].IsNew)
            {
                continue;
            }

            var place = Place(changed[i], inversePrecision);

            if (!removedByPlace.TryGetValue(place, out var stack))
            {
                stack = new Stack<int>();
                removedByPlace.Add(place, stack);
            }

            stack.Push(i);
        }

        var paired = new bool[changed.Count];
        var restyled = new List<ChangedTriangle>();

        for (var i = 0; i < changed.Count; i++)
        {
            if (!changed[i].IsNew || !removedByPlace.TryGetValue(Place(changed[i], inversePrecision), out var stack) || !stack.TryPop(out var removed))
            {
                continue;
            }

            paired[i] = true;
            paired[removed] = true;

            var before = changed[removed].Draw;
            var after = changed[i].Draw;

            var sameLook = before.Material.Equals(after.Material, StringComparison.OrdinalIgnoreCase) && SameTint(before.Tint, after.Tint);

            if (!sameLook)
            {
                restyled.Add(changed[i] with { Previous = before });
            }
        }

        var remaining = changed.Where((_, index) => !paired[index]).ToList();
        changed.Clear();
        changed.AddRange(remaining);

        return restyled;
    }

    /// <summary>
    /// Whether two tints are the same one stored differently. Tints stored in different spaces round a step apart,
    /// and newer compilers store decal tints and alphas as the square root of what older ones did.
    /// </summary>
    private static bool SameTint(Vector4 a, Vector4 b)
    {
        const float Step = 4f / 255f;

        var rounding = true;
        var squareRoot = true;

        for (var i = 0; i < 4; i++)
        {
            var x = Math.Clamp(a[i], 0f, 1f);
            var y = Math.Clamp(b[i], 0f, 1f);

            rounding &= MathF.Abs(x - y) < Step;
            squareRoot &= MathF.Abs(MathF.Sqrt(x) - y) < Step || MathF.Abs(MathF.Sqrt(y) - x) < Step;
        }

        return rounding || squareRoot;
    }

    private static string FormatTint(Vector4 tint)
        => string.Create(CultureInfo.InvariantCulture, $"{MathF.Round(tint.X * 255f)} {MathF.Round(tint.Y * 255f)} {MathF.Round(tint.Z * 255f)} {MathF.Round(tint.W * 255f)}");

    private static MapDiffEntry RestyledEntry(List<ChangedTriangle> cluster)
    {
        var materials = new SortedSet<(string Old, string New)>();
        var tints = new SortedSet<(string Old, string New)>();
        var bounds = cluster[0].Bounds;

        foreach (var triangle in cluster)
        {
            var previous = triangle.Previous!;

            if (!previous.Material.Equals(triangle.Draw.Material, StringComparison.OrdinalIgnoreCase))
            {
                materials.Add((previous.Material, triangle.Draw.Material));
            }

            var oldTint = FormatTint(previous.Tint);
            var newTint = FormatTint(triangle.Draw.Tint);

            if (oldTint != newTint)
            {
                tints.Add((oldTint, newTint));
            }

            bounds = bounds.Union(triangle.Bounds);
        }

        var changes = new List<MapDiffPropertyChange>();
        changes.AddRange(materials.Select(pair => new MapDiffPropertyChange(cluster[0].Draw.Owner.IsCollision ? "group" : "material", pair.Old.Trim(), pair.New.Trim())));
        changes.AddRange(tints.Select(static pair => new MapDiffPropertyChange("tint", pair.Old, pair.New)));
        changes.Add(new MapDiffPropertyChange("triangles", null, cluster.Count.ToString(CultureInfo.InvariantCulture)));

        var owner = cluster[0].Draw.Owner;
        var material = cluster[0].Draw.Material;

        return new MapDiffEntry
        {
            Kind = MapDiffKind.Modified,
            Category = owner.IsCollision ? MapDiffCategory.Collision : MapDiffCategory.Geometry,
            Type = owner.IsCollision ? "Collision" : GeometryType(owner.Model),
            Name = owner.IsCollision ? material.Trim() : Path.GetFileNameWithoutExtension(material),
            Detail = (materials.Count > 0, tints.Count > 0) switch
            {
                (true, true) => "material and tint changed",
                (true, false) => owner.IsCollision ? "collision group changed" : "material changed",
                _ => "tint changed",
            },
            OldBounds = bounds,
            NewBounds = bounds,
            Changes = changes,
        };
    }

    /// <summary>Groups triangles that are within <paramref name="radius"/> of each other, through a grid of that size.</summary>
    private static Dictionary<int, List<ChangedTriangle>>.ValueCollection Cluster(List<ChangedTriangle> triangles, float radius)
    {
        // Each triangle joins every cell its bounds cover, so a long one, like a clip brush face, links everything
        // along it rather than only what is near its middle
        const int MaxCellsPerAxis = 64;

        var cells = new Dictionary<GridPoint, int>();
        var firstCellOfTriangle = new int[triangles.Count];
        var links = new List<(int A, int B)>();
        var inverseRadius = 1f / radius;

        int CellIndex(GridPoint cell)
        {
            if (!cells.TryGetValue(cell, out var index))
            {
                index = cells.Count;
                cells.Add(cell, index);
            }

            return index;
        }

        for (var i = 0; i < triangles.Count; i++)
        {
            var bounds = triangles[i].Bounds;
            var min = bounds.Min * inverseRadius;
            var max = bounds.Max * inverseRadius;
            var from = new GridPoint((int)MathF.Floor(min.X), (int)MathF.Floor(min.Y), (int)MathF.Floor(min.Z));
            var to = new GridPoint(
                Math.Min((int)MathF.Floor(max.X), from.X + MaxCellsPerAxis),
                Math.Min((int)MathF.Floor(max.Y), from.Y + MaxCellsPerAxis),
                Math.Min((int)MathF.Floor(max.Z), from.Z + MaxCellsPerAxis));

            var first = CellIndex(from);
            firstCellOfTriangle[i] = first;

            for (var x = from.X; x <= to.X; x++)
            {
                for (var y = from.Y; y <= to.Y; y++)
                {
                    for (var z = from.Z; z <= to.Z; z++)
                    {
                        var index = CellIndex(new GridPoint(x, y, z));

                        if (index != first)
                        {
                            links.Add((first, index));
                        }
                    }
                }
            }
        }

        var parents = Enumerable.Range(0, cells.Count).ToArray();

        int Find(int x)
        {
            while (parents[x] != x)
            {
                parents[x] = parents[parents[x]];
                x = parents[x];
            }

            return x;
        }

        foreach (var (a, b) in links)
        {
            parents[Find(a)] = Find(b);
        }

        foreach (var (cell, index) in cells)
        {
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    for (var z = -1; z <= 1; z++)
                    {
                        if (cells.TryGetValue(new GridPoint(cell.X + x, cell.Y + y, cell.Z + z), out var neighbour))
                        {
                            parents[Find(neighbour)] = Find(index);
                        }
                    }
                }
            }
        }

        var clusters = new Dictionary<int, List<ChangedTriangle>>();

        for (var i = 0; i < triangles.Count; i++)
        {
            var root = Find(firstCellOfTriangle[i]);

            if (!clusters.TryGetValue(root, out var cluster))
            {
                cluster = [];
                clusters.Add(root, cluster);
            }

            cluster.Add(triangles[i]);
        }

        return clusters.Values;
    }

    private static MapDiffEntry GeometryEntry(List<ChangedTriangle> cluster)
    {
        AABB? oldBounds = null;
        AABB? newBounds = null;
        var added = 0;
        var removed = 0;
        var materials = new Dictionary<string, (int Removed, int Added)>(StringComparer.OrdinalIgnoreCase);
        var oldModels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var newModels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var triangle in cluster)
        {
            var counts = materials.GetValueOrDefault(triangle.Draw.Material);

            if (triangle.IsNew)
            {
                added++;
                newBounds = newBounds?.Union(triangle.Bounds) ?? triangle.Bounds;
                materials[triangle.Draw.Material] = counts with { Added = counts.Added + 1 };
                newModels.Add(triangle.Draw.Owner.Model);
            }
            else
            {
                removed++;
                oldBounds = oldBounds?.Union(triangle.Bounds) ?? triangle.Bounds;
                materials[triangle.Draw.Material] = counts with { Removed = counts.Removed + 1 };
                oldModels.Add(triangle.Draw.Owner.Model);
            }
        }

        var kind = (removed, added) switch
        {
            (0, _) => MapDiffKind.Added,
            (_, 0) => MapDiffKind.Removed,
            _ => MapDiffKind.Modified,
        };

        var isCollision = cluster[0].Draw.Owner.IsCollision;
        var byTriangles = materials.OrderByDescending(static pair => pair.Value.Added + pair.Value.Removed).ToList();
        var name = isCollision ? byTriangles[0].Key.Trim() : Path.GetFileNameWithoutExtension(byTriangles[0].Key);

        if (byTriangles.Count > 1)
        {
            name += $" (+{byTriangles.Count - 1} more)";
        }

        var models = newModels.Count > 0 ? newModels : oldModels;
        var changes = new List<MapDiffPropertyChange>
        {
            new("triangles", removed > 0 ? removed.ToString(CultureInfo.InvariantCulture) : null, added > 0 ? added.ToString(CultureInfo.InvariantCulture) : null),
        };

        foreach (var (material, counts) in byTriangles)
        {
            changes.Add(new MapDiffPropertyChange(isCollision ? $"group {material.Trim()}" : $"material {Path.GetFileNameWithoutExtension(material)}",
                counts.Removed > 0 ? $"{counts.Removed} triangles" : null,
                counts.Added > 0 ? $"{counts.Added} triangles" : null));
        }

        if (!isCollision)
        {
            changes.Add(new MapDiffPropertyChange("models", oldModels.Count > 0 ? string.Join(", ", oldModels) : null, newModels.Count > 0 ? string.Join(", ", newModels) : null));
        }

        return new MapDiffEntry
        {
            Kind = kind,
            Category = isCollision ? MapDiffCategory.Collision : MapDiffCategory.Geometry,
            Type = isCollision ? "Collision" : models.All(IsCompiledWithMap) ? "World geometry" : "Prop",
            Name = name,
            Detail = kind switch
            {
                MapDiffKind.Added => $"+{added} triangles",
                MapDiffKind.Removed => $"-{removed} triangles",
                _ => $"+{added} / -{removed} triangles",
            },
            OldBounds = oldBounds,
            NewBounds = newBounds,
            Changes = changes,
        };
    }

    /// <summary>
    /// Pairs items of the two builds that share a key, nearest first, up to <paramref name="radius"/> apart.
    /// </summary>
    private static void MatchNearest<T, TKey>(List<T> oldItems, List<T> newItems, Func<T, TKey> key, Func<T, Vector3> position, float radius,
        List<(T Old, T New)> pairs, List<T> unmatchedOld, List<T> unmatchedNew)
        where T : class
        where TKey : notnull
    {
        var newByKey = newItems.GroupBy(key).ToDictionary(static group => group.Key, static group => group.ToList());
        var candidates = new List<(float Distance, T Old, T New)>();

        foreach (var oldItem in oldItems)
        {
            if (!newByKey.TryGetValue(key(oldItem), out var sameKey))
            {
                continue;
            }

            var oldPosition = position(oldItem);

            foreach (var newItem in sameKey)
            {
                var distance = Vector3.Distance(oldPosition, position(newItem));

                if (distance <= radius)
                {
                    candidates.Add((distance, oldItem, newItem));
                }
            }
        }

        candidates.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        var used = new HashSet<T>(ReferenceEqualityComparer.Instance);

        foreach (var (_, oldItem, newItem) in candidates)
        {
            if (used.Contains(oldItem) || used.Contains(newItem))
            {
                continue;
            }

            used.Add(oldItem);
            used.Add(newItem);
            pairs.Add((oldItem, newItem));
        }

        unmatchedOld.AddRange(oldItems.Where(item => !used.Contains(item)));
        unmatchedNew.AddRange(newItems.Where(item => !used.Contains(item)));
    }
}
