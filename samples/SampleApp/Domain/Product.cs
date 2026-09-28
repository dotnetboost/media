using DotNetBoost.Media.Core.Attributes;
using DotNetBoost.Media.Core.Models;

namespace SampleApp.Domain;

/// <summary>
/// An ordinary entity. Note what is NOT here: no <c>ImageUrl</c>, no <c>Attachments</c>
/// collection, no navigation to a media table. Attachment rows point at a product; a product knows
/// nothing about its files, which is what lets two tables serve every entity in the application.
/// <para>
/// The roles say what this product uses a file <i>for</i>, and how many of each it may hold: one
/// main image, one SEO picture, and a gallery of as many as you like. Declaring them is what lets
/// the engine refuse a second <c>primary</c>, refuse a <c>SetAsync</c> that would empty the gallery,
/// and refuse a misspelled role — none of which it can know otherwise, because a role is free text.
/// </para>
/// <para>
/// They are read at startup by <c>DeclareRolesFrom</c> in Program.cs. Without that line these
/// attributes are documentation and nothing more.
/// </para>
/// </summary>
[MediaEntity("Product")]
[MediaRole(MediaRoles.Primary)]
[MediaRole(MediaRoles.Gallery, Multiple = true)]
[MediaRole("seo")]
public class Product
{
    public int Id { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }
}
