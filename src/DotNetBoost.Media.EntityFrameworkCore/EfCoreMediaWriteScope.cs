using DotNetBoost.Media.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Media.EntityFrameworkCore;

/// <summary>
/// Where an <see cref="IMediaWriteParticipant"/> enlists entities in a media write on the Entity
/// Framework Core store. Take it as a dependency; <c>UseEntityFrameworkCore()</c> registers it
/// scoped.
/// </summary>
/// <remarks>
/// <para>
/// It wraps the very context instance the store saves, so an entity added here goes out in the
/// store's own <c>SaveChangesAsync</c> and commits with the media rows, or not at all. That is why
/// a participant adds through this rather than through a context of its own: an entity tracked on
/// any other context would never be saved, and nothing would say so. An entity type that is not
/// mapped on the media context throws here instead, which aborts the write.
/// </para>
/// <para>
/// Add, never save, and add only from a participant. If the write does not commit, everything
/// added here is detached again, so it cannot be saved later by the application's next
/// <c>SaveChangesAsync</c> on the same context. Only the entity passed in is detached, so enlist
/// each entity of a graph — an audit entry and each of its change rows — rather than the root
/// alone.
/// </para>
/// </remarks>
/// <param name="context">The context the media store saves.</param>
public sealed class EfCoreMediaWriteScope(DbContext context)
{
    private readonly List<object> _enlisted = [];

    /// <summary>The context the media store saves.</summary>
    internal DbContext Context { get; } = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>
    /// Tracks <paramref name="entity"/> as added on the media store's context, to be saved with
    /// the current write.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The entity's type is not mapped on the media context.
    /// </exception>
    public void Add(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Context.Add(entity);
        _enlisted.Add(entity);
    }

    /// <summary>The write committed what was enlisted; forget it without detaching.</summary>
    internal void Committed() => _enlisted.Clear();

    /// <summary>The write will not commit; detach what was enlisted.</summary>
    internal void Discard()
    {
        foreach (var entity in _enlisted)
            Context.Entry(entity).State = EntityState.Detached;

        _enlisted.Clear();
    }
}
