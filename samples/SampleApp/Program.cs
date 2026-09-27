using DotNetBoost.Media.API;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using Microsoft.EntityFrameworkCore;
using SampleApp.Data;
using SampleApp.Domain;
using SampleApp.Dtos;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, service discovery and resilient HttpClients.
builder.AddServiceDefaults();

// ─────────────────────────────────────────────────────────────────────────────
// Database. Exactly one block is active; it must match the active ItemGroup in
// SampleApp.csproj and the container started by the AppHost.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("media")
               ?? throw new InvalidOperationException(
                   "No 'media' connection string. Run this through aspire/DotNetBoost.Media.AppHost, "
                   + "which starts PostgreSQL and injects it.")));

// ALTERNATIVE: SQL Server
// builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("media")!));

// ALTERNATIVE: SQLite, for running this without any container at all
// builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=sample.db"));

// ─────────────────────────────────────────────────────────────────────────────
// Media. One table for the rows (the DbContext above), one backend for the bytes.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddMedia(options =>
    {
        options.MaxFileSizeBytes = 25 * 1024 * 1024;
    })
    .UseEntityFrameworkCore<AppDbContext>()
    // ACTIVE: local disk. Nothing to install, and nothing is reachable over HTTP except through
    // this application's own routes.
    .UseFileSystem(Path.Combine(builder.Environment.ContentRootPath, "uploads"))
    // ALTERNATIVE: Azure Blob Storage. The AppHost can start Azurite for this.
    // .UseAzureBlobStorage(builder.Configuration.GetConnectionString("blobs")!, "media")
    // ALTERNATIVE: Amazon S3, or any S3-compatible store.
    // .UseAmazonS3("my-app-media")
    .AllowContentTypes("image/*", "application/pdf")
    .Build();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<MediaExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

// Create the database on first run. A real application uses EF Core migrations.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

app.UseExceptionHandler();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// The library's own endpoints: upload, list, download, patch, delete.
// Anonymous, as every Map… in ASP.NET Core is — in a real application, put authorization in front:
//     app.MapMediaEndpoints().RequireAuthorization();
app.MapMediaEndpoints();

// ─────────────────────────────────────────────────────────────────────────────
// This sample's own endpoints, showing media attached to an entity.
//
// Under /api, like the library's own, so clients/dashboard can reach both through one proxy.
// ─────────────────────────────────────────────────────────────────────────────

app.MapGet("/api/products", async (AppDbContext db, IMediaManager media, CancellationToken ct) =>
{
    var products = await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);

    var responses = new List<ProductResponse>(products.Count);
    foreach (var product in products)
        responses.Add(await ToResponseAsync(product, media, ct));

    return responses;
});

app.MapGet("/api/products/{id:int}", async (int id, AppDbContext db, IMediaManager media, CancellationToken ct) =>
{
    var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    return product is null
        ? Results.NotFound()
        : Results.Ok(await ToResponseAsync(product, media, ct));
});

app.MapPost("/api/products", async (CreateProductRequest request, AppDbContext db, CancellationToken ct) =>
{
    var product = new Product { Sku = request.Sku, Name = request.Name, Price = request.Price };

    db.Products.Add(product);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/products/{product.Id}", product);
});

// Upload an image for a product. The two writes — the file and the product's own row — share the
// DbContext, so one transaction covers both.
//
// `role` says what the image is for. Note the two different words: the category is where the BYTES
// live, which is a property of the file and leads its storage path; the role is what THIS product
// uses it for, which is a property of the link and can differ for another product using the same
// image.
app.MapPost("/api/products/{id:int}/images", async (
    int id,
    IFormFile file,
    AppDbContext db,
    IMediaManager media,
    CancellationToken ct,
    string? role = null) =>
{
    var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
    if (product is null)
        return Results.NotFound();

    await using var content = file.OpenReadStream();

    var uploaded = await media.For(product).UploadAsync(new MediaUpload
    {
        Content     = content,
        FileName    = file.FileName,
        ContentType = file.ContentType,
        Category    = "product-images",
        Role        = role ?? MediaRoles.Gallery
    }, ct);

    return Results.Created($"/api/media/{uploaded.Id}", uploaded.Id);
}).DisableAntiforgery();

// Attach an image the library already holds — the draft-form case, and the one where an image is
// shared with another product. Nothing is copied: both products point at the same bytes.
app.MapPost("/api/products/{id:int}/images/{mediaId:guid}", async (
    int id,
    Guid mediaId,
    AppDbContext db,
    IMediaManager media,
    CancellationToken ct,
    string? role = null) =>
{
    var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
    if (product is null)
        return Results.NotFound();

    await media.For(product).AddAsync(mediaId, role ?? MediaRoles.Gallery, ct);

    return Results.NoContent();
});

// Make one image the product's primary. SetAsync rather than AddAsync, because a product has one
// primary image: it detaches whatever held the role, in one operation rather than two with a window
// in between where the product has two primaries or none.
//
// The displaced image is only detached. It stays in the library, and stays attached to every other
// product using it.
app.MapPut("/api/products/{id:int}/images/primary", async (
    int id,
    SetPrimaryImageRequest request,
    AppDbContext db,
    IMediaManager media,
    CancellationToken ct) =>
{
    var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
    if (product is null)
        return Results.NotFound();

    await media.For(product).SetAsync(MediaRoles.Primary, request.MediaFileId, ct);

    return Results.NoContent();
});

// Save the order a gallery was dragged into.
app.MapPut("/api/products/{id:int}/images/gallery/order", async (
    int id,
    ReorderGalleryRequest request,
    AppDbContext db,
    IMediaManager media,
    CancellationToken ct) =>
{
    var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
    if (product is null)
        return Results.NotFound();

    await media.For(product).ReorderAsync(MediaRoles.Gallery, request.MediaFileIds, ct);

    return Results.NoContent();
});

// Take an image off this product WITHOUT deleting it. This is what a product page's "remove" button
// does: the file stays in the library and stays attached to every other product using it. Deleting
// the file itself is DELETE /api/media/{id}, and that removes it from every product at once.
app.MapDelete("/api/products/{id:int}/images/{mediaId:guid}", async (
    int id,
    Guid mediaId,
    AppDbContext db,
    IMediaManager media,
    CancellationToken ct,
    string? role = null) =>
{
    var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
    if (product is null)
        return Results.NotFound();

    // No role given detaches it from every role on this product.
    return await media.For(product).DetachAsync(mediaId, role, ct)
        ? Results.NoContent()
        : Results.NotFound();
});

// Deleting a product takes its files with it — the ones it alone was using. An image another product
// also references is only unlinked, which is the point of the attachment table: one product going
// must not pull a shared image out of the others. Nothing in the database refers to a media row —
// that is what lets two tables serve every entity — so no cascade will do this for you.
app.MapDelete("/api/products/{id:int}", async (int id, AppDbContext db, IMediaManager media, CancellationToken ct) =>
{
    var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
    if (product is null)
        return Results.NotFound();

    await media.For(product).DeleteAllAsync(ct);

    db.Products.Remove(product);
    await db.SaveChangesAsync(ct);

    return Results.NoContent();
});

app.Run();

static async Task<ProductResponse> ToResponseAsync(Product product, IMediaManager media, CancellationToken ct)
{
    // ListWithRolesAsync rather than ListAsync: the client needs to know which image is the primary
    // one and what order the gallery is in, and that lives on the link, not on the file. Two reads
    // for the whole product, rather than one per role.
    var attached = await media.For(product).ListWithRolesAsync(ct);

    return new ProductResponse(
        product.Id,
        product.Sku,
        product.Name,
        product.Price,
        [.. attached.Select(a => new ProductImage(
            a.File.Id,
            a.File.FileName,
            a.File.ContentType,
            a.File.Size,
            $"/api/media/{a.File.Id}/content",
            a.Role,
            a.SortOrder))]);
}
