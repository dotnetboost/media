using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace DotNetBoost.Media.Core.Services;

/// <summary>
/// Finds an entity's key by convention: a property marked <c>[Key]</c>, then <c>Id</c>, then
/// <c>{TypeName}Id</c> — the same order DotNetBoost.MultiLingual uses, so an application running
/// both libraries keys its entities one way.
/// <para>
/// Replaced by the EF Core provider, which reads the actual model and is therefore right about
/// keys this cannot see (a shadow key, or one configured fluently).
/// </para>
/// </summary>
public sealed class ConventionEntityKeyAccessor : IEntityKeyAccessor
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> Cache = new();

    /// <inheritdoc/>
    public string? GetKey(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var type     = entity.GetType();
        var property = Cache.GetOrAdd(type, static t => Discover(t));

        if (property is null)
        {
            throw new InvalidOperationException(
                $"Cannot find the key of {type.FullName}. Mark it with [Key], name it 'Id' or " +
                $"'{type.Name}Id', or register your own IEntityKeyAccessor with UseEntityKeyAccessor<T>().");
        }

        var value = property.GetValue(entity);

        // An unassigned store-generated key. Not an error here: the caller decides whether it can
        // wait (EF Core assigns it on save) or has to fail.
        return value is null || IsDefault(value) ? null : EntityKey.ToKeyString(value);
    }

    private static PropertyInfo? Discover(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        return properties.FirstOrDefault(p => p.GetCustomAttribute<KeyAttribute>() is not null)
               ?? properties.FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.Ordinal))
               ?? properties.FirstOrDefault(p => string.Equals(p.Name, type.Name + "Id", StringComparison.Ordinal));
    }

    private static bool IsDefault(object value) => value switch
    {
        int i    => i == 0,
        long l   => l == 0,
        short s  => s == 0,
        Guid g   => g == Guid.Empty,
        string s => s.Length == 0,
        _        => false
    };
}
