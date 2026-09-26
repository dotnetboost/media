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
app.MapPost("/api/products/{id:int}/images", async (
    int id,
    IFormFile file,
    AppDbContext db,
    IMediaManager media,
    CancellationToken ct) =>
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
        Category    = "product-images"
    }, ct);

    return Results.Created($"/api/media/{uploaded.Id}", uploaded.Id);
}).DisableAntiforgery();

// Deleting a product takes its files with it. Nothing in the database refers to a media row — that
// is what lets one table serve every entity — so no cascade will do this for you.
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
    var files = await media.For(product).ListAsync("product-images", ct);

    return new ProductResponse(
        product.Id,
        product.Sku,
        product.Name,
        product.Price,
        [.. files.Select(f => new ProductImage(f.Id, f.FileName, f.ContentType, f.Size, $"/api/media/{f.Id}/content"))]);
}
