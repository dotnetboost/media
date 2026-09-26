# Contributing to DotNetBoost.Media

Thanks for considering a contribution! This guide covers everything you need to get started.

## Repository layout

```
dotnetboost-media/
├── src/                                     # Library projects (each ships as its own NuGet package)
│   ├── DotNetBoost.Media.Core               #   the engine and IMediaManager
│   ├── DotNetBoost.Media.EntityFrameworkCore  # ┐
│   ├── DotNetBoost.Media.Dapper             #   ├ metadata stores — the table
│   ├── DotNetBoost.Media.MongoDb            # ┘
│   ├── DotNetBoost.Media.FileSystem         # ┐
│   ├── DotNetBoost.Media.AzureBlobStorage   #   ├ storage backends — the bytes
│   ├── DotNetBoost.Media.AmazonS3           # ┘
│   └── DotNetBoost.Media.API                #   ASP.NET Core endpoints and problem details
├── tests/
│   ├── DotNetBoost.Media.UnitTests          # The engine, over in-memory doubles
│   ├── DotNetBoost.Media.ProviderTests      # Store contract suite (SQLite), local disk, DI wiring
│   ├── DotNetBoost.Media.ApiTests           # Endpoints over TestServer
│   ├── DotNetBoost.Media.IntegrationTests   # Testcontainers, needs Docker
│   └── DotNetBoost.Media.TestSupport        # In-memory doubles shared by the suites
├── samples/
│   └── SampleApp                            # EF Core + PostgreSQL + local disk, the reference demo
├── aspire/
│   ├── DotNetBoost.Media.AppHost            # Orchestrates the API and its containers
│   └── DotNetBoost.Media.ServiceDefaults    # OTel, health checks, service discovery
└── docs/                                    # Additional documentation
```

[docs/architecture.md](docs/architecture.md) explains how the pieces fit together.

## Prerequisites

- .NET 10 SDK
- .NET 8 runtime — the libraries and most test projects multi-target `net8.0`, so the net8.0
  test pass needs it.
- Docker — for `IntegrationTests`, which start real PostgreSQL, SQL Server and MongoDB servers
  plus Azurite and MinIO for the two cloud storage backends, and for running the sample through
  the Aspire AppHost.

## Building and testing

```bash
dotnet build
dotnet test tests/DotNetBoost.Media.UnitTests
dotnet test tests/DotNetBoost.Media.ProviderTests
dotnet test tests/DotNetBoost.Media.ApiTests
dotnet test tests/DotNetBoost.Media.IntegrationTests   # Docker required
```

To run the whole demo stack while you work on it:

```bash
dotnet run --project aspire/DotNetBoost.Media.AppHost
```

Warnings are errors (`TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`), and every
public member of a shipped package needs XML docs.

## Guidelines

- **Core stays provider-neutral.** No EF Core, ASP.NET Core, cloud SDK or database driver
  references in `DotNetBoost.Media.Core`. If a store or a backend needs something from the engine,
  add it to `IMediaStore` or `IMediaStorageProvider` in neutral terms. `IMediaManager` is the only
  service application code injects: keep it small, since every member added to it is one more
  thing users have to understand. The engine behind it (`MediaManager`, `MediaValidator`,
  `MediaStorageProviderRegistry`, the streaming helpers) is internal; the sibling packages and the
  tests see it through `InternalsVisibleTo` in `DotNetBoost.Media.Core.csproj`.
- **Stores and backends stay dumb.** Validation, checksums, deduplication, soft delete and the
  ordering of the two writes all live in the engine, so every combination gets them for free. A
  store persists rows; a backend moves bytes. Neither validates anything.
- **The ordering is the contract.** Bytes are written before the row, and removed again if the row
  fails; deletes go the other way. Changing that in one provider and not the others is how
  orphaned objects appear.
- **A store change needs a contract test.** Add it to `StoreContractTests`, so every store —
  including the in-memory double the other suites rely on — is held to it, on SQLite locally and
  on PostgreSQL, SQL Server and MongoDB in CI.
- **A backend change needs a contract test.** Add it to `StorageContractTests`, which runs against
  Azurite and MinIO. A mocked cloud SDK proves only that the mock matches the test.
- New backends: see [docs/adding-a-backend.md](docs/adding-a-backend.md).

## Documentation

The root [README.md](README.md) is packed into every NuGet package, so its links must be **absolute**
GitHub URLs — a relative one breaks on nuget.org. Pages under `docs/` link to each other relatively.

Keep the README short: it is the 5-minute path plus a map. Anything longer belongs on a page under
`docs/`, listed in [docs/README.md](docs/README.md).
