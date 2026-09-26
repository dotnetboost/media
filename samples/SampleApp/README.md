# SampleApp

A minimal product catalogue with images, showing DotNetBoost.Media in an application rather than in
isolation.

```bash
dotnet run --project ../../aspire/DotNetBoost.Media.AppHost
```

See [docs/aspire.md](../../docs/aspire.md). To run it without any container, uncomment the SQLite
block in `Program.cs` and its package in `SampleApp.csproj`.

## What to look at

| | |
|---|---|
| `Domain/Product.cs` | An ordinary entity. **Nothing here mentions media** — no `ImageUrl`, no `Attachments` collection, no navigation. |
| `Data/AppDbContext.cs` | `modelBuilder.ApplyMedia()` at the end of `OnModelCreating`. That is the whole schema change. |
| `Program.cs` | `AddMedia().UseEntityFrameworkCore<AppDbContext>().UseFileSystem(...)`, and the endpoints below. |

## Endpoints

The library's own, from `MapMediaEndpoints()`:

```
POST   /api/media                 upload
GET    /api/media                 list
GET    /api/media/{id}            metadata
GET    /api/media/{id}/content    the bytes
PATCH  /api/media/{id}            change metadata
DELETE /api/media/{id}            delete
```

And this sample's, showing media attached to an entity:

```
GET    /products                  products, each with its images
GET    /products/{id}
POST   /products
POST   /products/{id}/images      upload an image for a product
DELETE /products/{id}             deletes the product AND its files
```

`DELETE /products/{id}` is the one worth reading. There is no foreign key from a media row to
`Products` — that is what lets one table serve every entity — so nothing cascades, and the handler
calls `media.For(product).DeleteAllAsync()` itself.

## Trying it

```bash
curl -X POST -H "Content-Type: application/json" \
     -d '{"sku":"LAMP-1","name":"Desk lamp","price":39.00}' \
     http://localhost:5180/products

curl -F "file=@lamp.png" http://localhost:5180/products/1/images
curl http://localhost:5180/products/1
```

OpenAPI at `/openapi/v1.json`, Scalar at `/scalar/v1`.

## Not production-ready

Two things this sample does that a real application should not:

- **`EnsureCreatedAsync()` at startup.** Use EF Core migrations.
- **`MapMediaEndpoints()` without authorization.** See
  [docs/validation-and-security.md](../../docs/validation-and-security.md#authorization).
