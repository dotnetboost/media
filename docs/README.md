# DotNetBoost.Media documentation

Start with the [README](../README.md) for the 5-minute path. These pages go further.

## Understanding it

| | |
|---|---|
| [Architecture](architecture.md) | The two halves — a table of rows and a store of bytes — and why the ordering between them is what it is |
| [Limitations](limitations.md) | What this deliberately does not do, and what to reach for instead |

## Using it

| | |
|---|---|
| [Uploading and reading](uploading-and-reading.md) | `IMediaManager` in full |
| [Attaching media to entities](attaching-to-entities.md) | `For(entity)`, entity names, keys, and cleanup |
| [Querying](querying.md) | Filtering, paging, categories and tags |
| [Validation and security](validation-and-security.md) | Size limits, allow-lists, content sniffing, path safety, authorization |
| [REST API](rest-api.md) | Every endpoint and its status codes |

## Configuring it

| | |
|---|---|
| [Storage backends](storage-backends.md) | Local disk, Azure Blob Storage, Amazon S3 — and running several at once |
| [Metadata stores](metadata-stores.md) | EF Core, Dapper, MongoDB |
| [Configuration reference](configuration-reference.md) | Every option and builder method in one table |

## Extending and running it

| | |
|---|---|
| [Adding a backend](adding-a-backend.md) | Implementing `IMediaStorageProvider` |
| [Aspire](aspire.md) | Running the sample stack |
