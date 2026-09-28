using DotNetBoost.Media.Core.Attributes;
using DotNetBoost.Media.Core.Models;
using System.Reflection;

namespace DotNetBoost.Media.Core.Metadata;

/// <summary>One role an entity declares, and whether it holds one file or many.</summary>
/// <param name="Name">The role's name, normalised.</param>
/// <param name="Multiple">Whether it holds several files, in order.</param>
public readonly record struct MediaEntityRole(string Name, bool Multiple);

/// <summary>
/// What each entity declares its roles to be, looked up by the entity's <b>stored name</b>.
/// <para>
/// By name rather than by type, deliberately. Half the ways of attaching have no type to read an
/// attribute from — <c>media.For("Product", "42")</c> is two strings, and the library's own REST
/// endpoints never see the application's classes at all. Reading the attribute at attach time would
/// therefore catch a bad role one way and accept it the other, which is worse than not checking:
/// the caller would believe they were protected. The declarations are read once at startup and
/// filed under the name, so both paths ask the same question.
/// </para>
/// <para>
/// An entity that declares nothing is not checked. Every application that used ad-hoc roles before
/// this existed goes on working unchanged.
/// </para>
/// </summary>
public interface IMediaRoleRegistry
{
    /// <summary>Whether this entity declared any roles, and is therefore checked at all.</summary>
    bool Declares(string entityName);

    /// <summary>
    /// The roles this entity declared, in declaration order, or an empty list when it declared none.
    /// </summary>
    IReadOnlyList<MediaEntityRole> RolesFor(string entityName);

    /// <summary>The declaration of one role, or <c>null</c> when this entity did not declare it.</summary>
    MediaEntityRole? Find(string entityName, string role);
}

/// <summary>
/// The registry built at <c>MediaBuilder.Build()</c> from <see cref="MediaRoleAttribute"/>s and from
/// declarations made on the builder. Immutable once built, and a singleton: it is read on every
/// attach.
/// </summary>
public sealed class MediaRoleRegistry : IMediaRoleRegistry
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<MediaEntityRole>> _byEntity;

    /// <summary>A registry over declarations already keyed by entity name.</summary>
    public MediaRoleRegistry(IReadOnlyDictionary<string, IReadOnlyList<MediaEntityRole>> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        _byEntity = declarations;
    }

    /// <summary>A registry that declares nothing, so nothing is checked. The default.</summary>
    public static MediaRoleRegistry Empty { get; } =
        new(new Dictionary<string, IReadOnlyList<MediaEntityRole>>(StringComparer.Ordinal));

    /// <inheritdoc/>
    public bool Declares(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        return _byEntity.ContainsKey(entityName);
    }

    /// <inheritdoc/>
    public IReadOnlyList<MediaEntityRole> RolesFor(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        return _byEntity.TryGetValue(entityName, out var roles) ? roles : [];
    }

    /// <inheritdoc/>
    public MediaEntityRole? Find(string entityName, string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var normalized = MediaRoles.Normalize(role);

        foreach (var declared in RolesFor(entityName))
        {
            if (string.Equals(declared.Name, normalized, StringComparison.Ordinal))
                return declared;
        }

        return null;
    }
}

/// <summary>
/// Reads <see cref="MediaRoleAttribute"/>s off types. Used by the builder at startup; nothing reads
/// attributes after that.
/// </summary>
public static class MediaEntityRoles
{
    /// <summary>
    /// The roles <paramref name="type"/> declares, in the order they are written, or an empty list
    /// when it declares none.
    /// </summary>
    /// <exception cref="InvalidOperationException">It declares the same role twice.</exception>
    public static IReadOnlyList<MediaEntityRole> Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var attributes = type.GetCustomAttributes<MediaRoleAttribute>(inherit: true).ToArray();

        if (attributes.Length == 0)
            return [];

        var roles = new List<MediaEntityRole>(attributes.Length);
        var seen  = new HashSet<string>(StringComparer.Ordinal);

        foreach (var attribute in attributes)
        {
            var name = MediaRoles.Normalize(attribute.Name);

            if (!seen.Add(name))
            {
                throw new InvalidOperationException(
                    $"{type.FullName} declares the role '{name}' more than once. Roles are lower-cased, so " +
                    "two spellings of the same word are one role; declare it once and say whether it is " +
                    "Multiple there.");
            }

            roles.Add(new MediaEntityRole(name, attribute.Multiple));
        }

        return roles;
    }

    /// <summary>
    /// Every type in <paramref name="assembly"/> that declares roles, paired with the name its media
    /// is stored under.
    /// <para>
    /// Types that cannot be loaded are skipped rather than failing the scan: one unloadable type in
    /// an assembly — a missing optional dependency, typically — should not stop an application
    /// starting over media it was not going to attach anything to.
    /// </para>
    /// </summary>
    public static IEnumerable<(string EntityName, IReadOnlyList<MediaEntityRole> Roles)> Scan(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in Loadable(assembly))
        {
            if (!type.IsClass || type.IsAbstract)
                continue;

            // Declared on this type rather than inherited from a base: a base class that declares
            // roles is already reported under its own name, and reporting it again for every
            // subclass would file the same roles under several names.
            if (type.GetCustomAttributes<MediaRoleAttribute>(inherit: false).Any())
                yield return (MediaEntityNames.For(type), Of(type));
        }
    }

    private static IEnumerable<Type> Loadable(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }
}

/// <summary>
/// Collects an entity's roles when they are declared on the builder instead of on the class.
/// Handed to the callback of <c>DeclareRoles(name, …)</c>.
/// </summary>
public sealed class MediaRoleDeclaration
{
    private readonly List<MediaEntityRole> _roles = [];

    /// <summary>The roles declared so far, in order.</summary>
    public IReadOnlyList<MediaEntityRole> Roles => _roles;

    /// <summary>Declares a role that holds one file — a main image, a signed copy.</summary>
    public MediaRoleDeclaration One(string role) => Add(role, multiple: false);

    /// <summary>Declares a role that holds several files, in order — a gallery.</summary>
    public MediaRoleDeclaration Many(string role) => Add(role, multiple: true);

    private MediaRoleDeclaration Add(string role, bool multiple)
    {
        var name = MediaRoles.Normalize(role);

        if (_roles.Any(declared => string.Equals(declared.Name, name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The role '{name}' is declared twice. Roles are lower-cased, so two spellings of the same " +
                "word are one role.");
        }

        _roles.Add(new MediaEntityRole(name, multiple));
        return this;
    }
}
