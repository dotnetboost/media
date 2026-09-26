# Running the sample with Aspire

```bash
dotnet run --project aspire/DotNetBoost.Media.AppHost
```

Docker is required. No Aspire CLI install is needed — the AppHost resolves the orchestrator and
the dashboard from the CLI bundle on NuGet — though `aspire run` does the same thing if you have
it.

It starts:

- **PostgreSQL**, holding the sample's `Products` and the `MediaFiles` table, on a named volume so
  files uploaded in one session are still recorded in the next
- **pgAdmin**, for looking at the rows
- **the API** — `samples/SampleApp` — storing bytes on local disk under its content root

The dashboard prints its URL on startup: logs, traces and metrics for everything above, through
the OpenTelemetry wiring in `DotNetBoost.Media.ServiceDefaults`.

## Trying it

```bash
# Upload
curl -F "file=@photo.png" -F "category=product-images" http://localhost:5180/api/media

# List
curl http://localhost:5180/api/media

# Download
curl -o out.png http://localhost:5180/api/media/{id}/content

# Attach to a product
curl -X POST -H "Content-Type: application/json" \
     -d '{"sku":"LAMP-1","name":"Desk lamp","price":39.00}' \
     http://localhost:5180/api/products

curl -F "file=@lamp.png" http://localhost:5180/api/products/1/images
curl http://localhost:5180/api/products/1
```

The OpenAPI document is at `/openapi/v1.json`, with Scalar at `/scalar/v1`.

## Swapping the pieces

The sample's **database** and its **storage backend** are independent: any of the three metadata
stores pairs with any of the three backends. Exactly one of each is active, and each alternative
is written out and commented in three places that have to agree:

| | |
|---|---|
| `aspire/DotNetBoost.Media.AppHost/AppHost.cs` | which containers start |
| `aspire/DotNetBoost.Media.AppHost/DotNetBoost.Media.AppHost.csproj` | the Aspire hosting package for them |
| `samples/SampleApp/Program.cs` | the `AddDbContext` and `AddMedia` chain |
| `samples/SampleApp/SampleApp.csproj` | the provider packages |

### Azure Blob Storage instead of local disk

1. Uncomment the `builder.AddAzureStorage(...)` block in `AppHost.cs` and the `.WithReference(blobs)`
   line on the project, and uncomment `Aspire.Hosting.Azure.Storage` in the AppHost's csproj —
   Aspire runs Azurite, Microsoft's Blob Storage emulator, in a container.
2. In `SampleApp.csproj`, comment the FileSystem project reference and uncomment the
   AzureBlobStorage one.
3. In `Program.cs`, comment `.UseFileSystem(...)` and uncomment `.UseAzureBlobStorage(...)`.

Nothing else changes — not the endpoints, not the entity, not a single call site. That is rather
the point.

### SQL Server or MongoDB instead of PostgreSQL

Same three places, following the commented blocks.

## Running without Aspire

The sample can run against SQLite and local disk with no container at all: uncomment the SQLite
block in `Program.cs` and its package in `SampleApp.csproj`, then

```bash
dotnet run --project samples/SampleApp
```
