using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace DotNetBoost.Media.Core.Services;

/// <summary>
/// Finds an entity's key by the key-discovery rule of <b>DotNetBoost Entity Addressing v1</b>: the
/// single property marked <c>[Key]</c>, else one named <c>Id</c>, else one named
/// <c>{TypeName}Id</c>, considering public instance properties that are readable and not indexers.
/// <para>
/// The rule is part of the versioned spec — see
/// <see cref="EntityKey.AddressingVersion"/> — so an application running another library that
/// implements the same version keys its entities one way, and
/// <c>DotNetBoost.Media.TestSupport.EntityAddressingConformance</c> asserts it rather than leaving
/// it to a comment.
/// </para>
/// <para>
/// Replaced by the EF Core provider, which reads the actual model and is therefore right about
/// keys this cannot see (a shadow key, or one configured fluently). Where the two disagree, the
/// model wins.
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
        // wait (EF Core assigns it on save) or has to fail. EntityKey.IsUnset rather than a local
        // rule, so "not assigned yet" means the same thing everywhere the spec reaches.
        return EntityKey.IsUnset(value) ? null : EntityKey.ToKeyString(value!);
    }

    private static PropertyInfo? Discover(Type type)
    {
        // Readable and not an indexer: an indexer has no single value to read, and a write-only
        // property cannot be read back. Both would otherwise be eligible on name alone.
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null)
            .ToArray();

        // inherit: true explicitly rather than by the overload's default, so this cannot drift
        // from the sibling implementations of the same spec if that default ever changes.
        var annotated = properties
            .Where(p => p.GetCustomAttribute<KeyAttribute>(inherit: true) is not null)
            .ToArray();

        if (annotated.Length > 1)
        {
            // Refused rather than resolved: Type.GetProperties() has no guaranteed order, so
            // picking the first would key the entity on whichever property this runtime happened
            // to return — a choice that can differ between builds and strand every attachment.
            throw new NotSupportedException(
                $"'{type.Name}' has a composite key ({string.Join(", ", annotated.Select(p => p.Name))}). " +
                "MediaAttachment.OwnerEntityId holds a single value; add a surrogate key before " +
                "attaching media to it.");
        }

        return annotated.FirstOrDefault()
               ?? properties.FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.Ordinal))
               ?? properties.FirstOrDefault(p => string.Equals(p.Name, type.Name + "Id", StringComparison.Ordinal));
    }
}
