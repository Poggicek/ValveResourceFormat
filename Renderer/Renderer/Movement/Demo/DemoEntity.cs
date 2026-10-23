namespace ValveResourceFormat.Renderer.Movement.Demo;

/// <summary>A networked class: its id in the demo and the serializer its entities are sent with.</summary>
internal sealed class ServerClass(int id, string name, Serializer serializer)
{
    private readonly Dictionary<string, FieldPath?> PathCache = [];

    public int Id { get; } = id;
    public string Name { get; } = name;
    public Serializer Serializer { get; } = serializer;

    /// <summary>Finds the path of a field by its dotted name, see <see cref="Serializer.FindPath"/>.</summary>
    public FieldPath? FindPath(string dottedName)
    {
        if (!PathCache.TryGetValue(dottedName, out var path))
        {
            path = Serializer.FindPath(dottedName);
            PathCache[dottedName] = path;
        }

        return path;
    }

    /// <summary>Gets the field a path leads to.</summary>
    public SendField Resolve(FieldPath path)
    {
        var field = Serializer.Fields[path[0]];

        for (var i = 1; i < path.Length; i++)
        {
            field = field.Child(path[i]);
        }

        return field;
    }
}

/// <summary>An entity as the demo has networked it so far.</summary>
internal sealed class DemoEntity(int index, int serial, ServerClass serverClass)
{
    /// <summary>The bits of a handle that hold the entity index.</summary>
    public const uint HandleIndexMask = 0x3FFF;

    public int Index { get; } = index;
    public int Serial { get; } = serial;
    public ServerClass Class { get; } = serverClass;

    /// <summary>Gets the values received, by field path.</summary>
    public Dictionary<FieldPath, object> Values { get; } = [];

    /// <summary>Gets a field's value by its dotted name.</summary>
    /// <returns>Whether the field exists, has been sent and is of type <typeparamref name="T"/>.</returns>
    public bool TryGet<T>(string dottedName, out T value)
    {
        if (Class.FindPath(dottedName) is { } path && Values.TryGetValue(path, out var boxed) && boxed is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Gets a field's value by its dotted name, or a fallback when it has not been sent.</summary>
    public T Get<T>(string dottedName, T fallback) => TryGet<T>(dottedName, out var value) ? value : fallback;
}
