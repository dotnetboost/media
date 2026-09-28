namespace SampleApp.Dtos;

/// <summary>A product as this sample's own endpoints return it, with its images alongside.</summary>
/// <param name="Id">The product.</param>
/// <param name="Sku">Its stock code.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Price">Its price.</param>
/// <param name="Images">
/// Its media, by role and then by the position the product put them in — so a client can take the
/// one <c>primary</c> image, draw the <c>gallery</c> in order, and find the <c>seo</c> picture,
/// without a request per role.
/// </param>
public sealed record ProductResponse(int Id, string Sku, string Name, decimal Price, IReadOnlyList<ProductImage> Images);

/// <summary>One of a product's files, as far as a client needs to know about it.</summary>
/// <param name="Id">The media id.</param>
/// <param name="FileName">The name it was uploaded under.</param>
/// <param name="ContentType">What it is.</param>
/// <param name="Size">How big it is.</param>
/// <param name="ContentUrl">Where to fetch the bytes from, on this application.</param>
/// <param name="Role">
/// What this product uses it for — <c>primary</c>, <c>gallery</c>, <c>seo</c>. A property of the
/// link, not of the file: the same image can be another product's gallery shot.
/// </param>
/// <param name="SortOrder">Its position within that role.</param>
public sealed record ProductImage(
    Guid Id,
    string FileName,
    string ContentType,
    long Size,
    string ContentUrl,
    string Role,
    int SortOrder);

/// <summary>Body of <c>POST /products</c>.</summary>
public sealed record CreateProductRequest(string Sku, string Name, decimal Price);

/// <summary>Body of <c>PUT /products/{id}</c>.</summary>
/// <remarks>
/// Its own columns only. A product's media is not in here and never will be: the files are
/// attachment rows pointing at the product, so saving its name cannot disturb them and attaching
/// an image does not mean saving the product.
/// </remarks>
public sealed record UpdateProductRequest(string Sku, string Name, decimal Price);

/// <summary>Body of <c>PUT /products/{id}/images/primary</c>.</summary>
/// <param name="MediaFileId">The image to become this product's one primary image.</param>
public sealed record SetPrimaryImageRequest(Guid MediaFileId);

/// <summary>Body of <c>PUT /products/{id}/images/gallery/order</c>.</summary>
/// <param name="MediaFileIds">
/// Every image in the product's gallery, in the order wanted. All of them — the ones left out of a
/// partial list would have no defined position.
/// </param>
public sealed record ReorderGalleryRequest(IReadOnlyList<Guid> MediaFileIds);
