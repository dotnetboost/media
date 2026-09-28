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

/// <summary>
/// Declares one role an entity's files may be attached under, and whether that role holds one file
/// or many.
/// <code>
/// [MediaEntity("Product")]
/// [MediaRole("primary")]                    // one file
/// [MediaRole("gallery", Multiple = true)]   // many, in order
/// [MediaRole("seo")]
/// public class Product { public int Id { get; set; } }
/// </code>
/// <para>
/// <b>Entirely opt-in.</b> A type that declares no roles is not checked at all, and any role may be
/// used on it — which is what every application did before this existed, and what most will go on
/// doing. Declaring even one role turns checking on for that entity.
/// </para>
/// <para>
/// Declaring is what lets the engine object to the two mistakes the verbs alone cannot catch:
/// appending a second file to a single-valued role, and replacing a multi-valued one — which would
/// silently detach every other file in it. It also catches a misspelled role at the call rather
/// than in production.
/// </para>
/// <para>
/// The roles are read once, at <c>Build()</c>, and filed under the entity's stored name. Nothing is
/// read from this attribute at attach time, because half the ways of attaching have no type to read
/// it from — <c>media.For("Product", "42")</c> is two strings, and the library's own REST endpoints
/// have nothing else. See <c>DeclareRolesFrom</c>.
/// </para>
/// <para>
/// Inherited, like <see cref="MediaEntityAttribute"/>: a hierarchy attached under its base's name
/// is one entity as far as its media is concerned, so it has one set of roles.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class MediaRoleAttribute : Attribute
{
    /// <summary>Declares a role.</summary>
    /// <param name="name">
    /// Its name — <c>primary</c>, <c>gallery</c>, <c>seo</c>. Lower-cased when it is read, so
    /// <c>Primary</c> and <c>primary</c> declare the same role.
    /// </param>
    public MediaRoleAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The role's name, as written. Normalised when the declaration is read.</summary>
    public string Name { get; }

    /// <summary>
    /// Whether the role holds several files, in order — a gallery. Default: <c>false</c>, one file,
    /// which is the commoner case and the one worth having as the default.
    /// </summary>
    public bool Multiple { get; init; }
}
