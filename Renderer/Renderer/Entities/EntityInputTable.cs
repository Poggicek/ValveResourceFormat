using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// The input handlers each entity class declares with <see cref="EntityInputAttribute"/>, Source's entity
/// data description tables. Built once per class when the class registers with <see cref="EntityFactory"/>,
/// so firing an input is a dictionary lookup and a delegate call rather than a chain of name comparisons.
/// </summary>
internal static class EntityInputTable
{
    private static readonly Dictionary<Type, FrozenDictionary<string, Action<BaseEntity, EntityInputData>>> Tables = [];

    /// <summary>
    /// Builds the input table for an entity class. The <see cref="DynamicallyAccessedMembersAttribute"/> is
    /// what keeps the handlers alive under trimming: registering a class is also what declares that its
    /// methods are reached by reflection.
    /// </summary>
    /// <typeparam name="T">The entity class to scan.</typeparam>
    public static void Bind<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] T>()
        where T : BaseEntity
    {
        if (Tables.ContainsKey(typeof(T)))
        {
            return;
        }

        var handlers = new Dictionary<string, Action<BaseEntity, EntityInputData>>(StringComparer.OrdinalIgnoreCase);

        // Instance methods here include the ones inherited from a base class, so an entity keeps every
        // input its bases declared without restating them. Private ones are the exception: reflection
        // returns a type's own private methods but never a base type's, so a handler a subclass must
        // inherit has to be protected. Walking the base chain instead would mean reflecting on types the
        // trimmer was never told to keep, which is what the annotation on T exists to avoid.
        foreach (var method in typeof(T).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var attribute = method.GetCustomAttribute<EntityInputAttribute>();

            if (attribute == null)
            {
                continue;
            }

            // Caught here rather than discovered as an input that silently does nothing on a subclass
            if (method.IsPrivate && !typeof(T).IsSealed)
            {
                throw new InvalidOperationException(
                    $"'{typeof(T).Name}.{method.Name}' handles the '{attribute.Name}' input but is private, "
                    + $"and '{typeof(T).Name}' can be derived from. A subclass would not inherit it: make it protected.");
            }

            var handler = method.CreateDelegate<Action<T, EntityInputData>>();

            // Two handlers claiming one input name is a mistake worth failing on
            handlers.Add(attribute.Name, (entity, data) => handler((T)entity, data));
        }

        Tables[typeof(T)] = handlers.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs the handler an entity declared for an input.
    /// </summary>
    /// <param name="entity">The entity receiving the input.</param>
    /// <param name="inputName">The input's name, matched case-insensitively.</param>
    /// <param name="data">The parameter and the entities that sent it.</param>
    /// <returns><see langword="true"/> when a handler ran.</returns>
    public static bool TryDispatch(BaseEntity entity, string inputName, EntityInputData data)
    {
        // A class's table already carries the inputs it inherited, so its own entry is the whole answer
        if (!Tables.TryGetValue(entity.GetType(), out var table) || !table.TryGetValue(inputName, out var handler))
        {
            return false;
        }

        handler(entity, data);
        return true;
    }
}
