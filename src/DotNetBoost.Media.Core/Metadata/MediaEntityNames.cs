using DotNetBoost.Media.Core.Attributes;
using System.Collections.Concurrent;
using System.Reflection;

namespace DotNetBoost.Media.Core.Metadata;

/// <summary>
/// Resolves the name an entity type's files are stored under: its
/// <see cref="MediaEntityAttribute"/> if it has one, otherwise the type name. Cached per type —
/// the lookup walks attributes and unwraps proxies, and it happens on every attach and every
/// listing.
/// </summary>
public static class MediaEntityNames
{
    /// <summary>The longest an entity name may be, matching the stored column.</summary>
    public const int MaxLength = 128;

    private static readonly ConcurrentDictionary<Type, string> Cache = new();

    /// <summary>The name <paramref name="type"/>'s files are stored under.</summary>
    /// <exception cref="InvalidOperationException">Its <c>[MediaEntity]</c> name is too long.</exception>
    public static string For(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Cache.GetOrAdd(type, static t => Resolve(t));
    }

    /// <summary>The name <paramref name="entity"/>'s files are stored under.</summary>
    public static string Of(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return For(entity.GetType());
    }

    private static string Resolve(Type type)
    {
        var declared = Unproxy(type);
        var name     = declared.GetCustomAttribute<MediaEntityAttribute>(inherit: true)?.Name ?? declared.Name;

        if (name.Length > MaxLength)
        {
            throw new InvalidOperationException(
                $"Entity name '{name}' on {declared.FullName} is {name.Length} characters; the column holds " +
                $"{MaxLength}. Shorten it with [MediaEntity(\"…\")].");
        }

        return name;
    }

    /// <summary>
    /// The application's own type behind a runtime-generated one. EF Core's lazy-loading and
    /// change-tracking proxies subclass the entity, and their generated names would attach a
    /// proxied instance's files under a name nothing else ever looks them up by.
    /// </summary>
    private static Type Unproxy(Type type)
    {
        // Castle's proxies — the ones EF Core uses — are named "<Entity>Proxy" and sit in a
        // dynamic assembly. Both conditions together, so a real application type that happens to
        // end in "Proxy" is left alone.
        while (type.BaseType is { } parent
               && parent != typeof(object)
               && type.Assembly.IsDynamic)
        {
            type = parent;
        }

        return type;
    }
}
