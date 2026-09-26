namespace SampleApp.Dtos;

/// <summary>A product as this sample's own endpoints return it, with its images alongside.</summary>
/// <param name="Id">The product.</param>
/// <param name="Sku">Its stock code.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Price">Its price.</param>
/// <param name="Images">Its media, newest first — ids the client fetches content for.</param>
public sealed record ProductResponse(int Id, string Sku, string Name, decimal Price, IReadOnlyList<ProductImage> Images);

/// <summary>One of a product's files, as far as a client needs to know about it.</summary>
/// <param name="Id">The media id.</param>
/// <param name="FileName">The name it was uploaded under.</param>
/// <param name="ContentType">What it is.</param>
/// <param name="Size">How big it is.</param>
/// <param name="ContentUrl">Where to fetch the bytes from, on this application.</param>
public sealed record ProductImage(Guid Id, string FileName, string ContentType, long Size, string ContentUrl);

/// <summary>Body of <c>POST /products</c>.</summary>
public sealed record CreateProductRequest(string Sku, string Name, decimal Price);
