using DotNetBoost.Media.Core.Attributes;

namespace SampleApp.Domain;

/// <summary>
/// An ordinary entity. Note what is NOT here: no <c>ImageUrl</c>, no <c>Attachments</c>
/// collection, no navigation to a media table. Files point at a product; a product knows nothing
/// about its files, which is what lets the same one table serve every entity in the application.
/// </summary>
[MediaEntity("Product")]
public class Product
{
    public int Id { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }
}
