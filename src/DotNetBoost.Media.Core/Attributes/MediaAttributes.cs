namespace DotNetBoost.Media.Core.Attributes;

/// <summary>
/// Names the entity in <see cref="Models.MediaAttachment.OwnerEntityName"/>, instead of using the type
/// name. Put it on an entity whose class you may rename, or on two unrelated types that would
/// otherwise share a name:
/// <code>
/// [MediaEntity("Product")]
/// public class ProductEntity { public int Id { get; set; } }
/// </code>
/// <para>
/// The stored name is what attaches a file to an entity, so changing it later strands every file
/// already attached under the old one. Pick it once.
/// </para>
/// <para>
/// Inherited, deliberately: a hierarchy attached under its base's name is one entity as far as
/// its media is concerned, which is what a TPH table already looks like.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class MediaEntityAttribute : Attribute
{
    /// <summary>Names the entity.</summary>
    /// <param name="name">1–128 characters. Validated when the type is first used.</param>
    public MediaEntityAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>The name files are attached under.</summary>
    public string Name { get; }
}
