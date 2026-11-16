using DotNetBoost.Media.Core.Interfaces;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DotNetBoost.Media.Core.Services;

/// <summary>
/// How a files-deleted handler is named in the failure log, the failure counter's tag and the
/// startup line. One place, so the three always agree about which handler they mean.
/// </summary>
internal static class MediaHandlerNames
{
    /// <summary>The name of a resolved handler.</summary>
    internal static string Of(IMediaFilesDeletedHandler handler) =>
        handler is DelegateMediaFilesDeletedHandler inline ? inline.Name : handler.GetType().Name;

    /// <summary>
    /// The name of an inline handler: the one the application gave, else where it was written.
    /// <para>
    /// Without this every inline handler would report as <c>DelegateMediaFilesDeletedHandler</c>,
    /// so two of them failing would read as one. The ordinal keeps two unnamed lambdas in the same
    /// class apart; a name is still better, and the docs say so.
    /// </para>
    /// </summary>
    /// <param name="given">The name passed to <c>OnFilesDeleted</c>, if any.</param>
    /// <param name="handler">The delegate, for where it was written.</param>
    /// <param name="ordinal">Which inline handler this is on the builder, from 1.</param>
    internal static string ForDelegate(string? given, Delegate handler, int ordinal)
    {
        if (!string.IsNullOrWhiteSpace(given))
            return given.Trim();

        return $"inline handler #{ordinal} in {WrittenIn(handler.Method)}";
    }

    /// <summary>
    /// The class a lambda was written in. A lambda compiles into a nested, compiler-generated class
    /// (<c>&lt;&gt;c</c>, <c>&lt;&gt;c__DisplayClass0_0</c>); the class worth naming is the one
    /// that contains it.
    /// </summary>
    private static string WrittenIn(MethodInfo method)
    {
        var type = method.DeclaringType;

        while (type?.DeclaringType is not null && type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            type = type.DeclaringType;

        return type?.Name ?? "an unknown class";
    }
}
