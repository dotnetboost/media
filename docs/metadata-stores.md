# Metadata stores

The store holds one row per uploaded file. Exactly one is configured.

## Entity Framework Core

```csharp
services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

services.AddMedia()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseFileSystem("./uploads")
    .Build();
```

```csharp
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IMediaDbContext
{
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.ApplyMedia();       // at the end
    }
}
```

Then generate a migration as you would for any other table:

```bash
dotnet ef migrations add AddMedia
```

**The rows live in your context**, which is what lets an upload and the entity it belongs to
commit together:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);

product.Name = request.Name;
await media.For(product).UploadAsync(upload, ct);
await db.SaveChangesAsync(ct);

await transaction.CommitAsync(ct);
```

If the transaction rolls back, the row goes with it. (The bytes do not — a blob store is not in
your transaction. See [limitations.md](limitations.md).)

Supported engines: SQL Server, PostgreSQL, SQLite. Timestamps are stored UTC and read back as
`DateTimeKind.Utc` through a value converter, because SQL Server and SQLite both otherwise return
`Unspecified` — which serializes to JSON without a `Z` and is read by every client as local time.

Rename the table if you need to:

```csharp
modelBuilder.ApplyMedia(o =>
{
    o.MediaFilesTable = "Uploads";
    o.Schema          = "media";
});
```

## Dapper

For applications without EF Core.

```csharp
services.AddMedia()
    .UseDapper(_ => new NpgsqlConnection(connectionString), migrateSchema: true)
    .UseFileSystem("./uploads")
    .Build();
```

The dialect comes from the connection's runtime type — SQL Server, PostgreSQL or SQLite — unless
`DapperOptions.Dialect` says otherwise, which is what to set when the connection is a wrapper
(a profiler, a resiliency decorator) whose type name no longer says which engine is behind it.

`migrateSchema: true` creates the table at startup if it is absent, in exactly the shape the
EF Core store maps, so a database serves either. It **only ever creates** — it will not alter a
table whose shape has changed. Leave it off when migrations own your schema, and take the DDL to
check in instead:

```csharp
File.WriteAllText("media.sql", DapperSchema.Script(SqlDialect.PostgreSql));
```

The connection is registered as `IDbConnection` in the scope, so a transaction the application
puts there is the one the store enlists in:

```csharp
using var transaction = connection.BeginTransaction();
var store = new DapperMediaStore(connection, options).WithTransaction(transaction);
// … write your entity and upload its files …
transaction.Commit();
```

Rows are read with a hand-written mapper rather than Dapper's automatic mapping, because the same
logical column arrives as a different CLR type per engine — SQLite has no GUID and no date type.
The usual fix, a global `SqlMapper.AddTypeHandler`, would change how `Guid` is read for the whole
application; a storage provider does not get to have that side effect.

## MongoDB

```csharp
services.AddSingleton<IMongoClient>(new MongoClient(connectionString));
services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase("shop"));

services.AddMedia()
    .UseMongoDb()
    .UseAmazonS3("my-app-media")
    .Build();
```

One collection, `mediaFiles`, with the file's own id as `_id` — so a lookup by id is a primary-key
read and needs no secondary index. The indexes the relational stores create are created here too,
at startup, unless `CreateIndexes` says otherwise.

There is no ambient transaction to join: MongoDB's are session-scoped and the driver does not hand
them out through DI. The engine's ordering still holds — bytes first, document second, and the
bytes removed again if the document cannot be written.

## What they have in common

All three pass the same contract suite (`StoreContractTests`), against SQLite locally and against
PostgreSQL, SQL Server and MongoDB in CI. A store that behaves differently is a failing test, not
a documented quirk.

| | EF Core | Dapper | MongoDB |
|---|---|---|---|
| Shares your transaction | ✓ | ✓ with `WithTransaction` | ✗ |
| Creates its own schema | via migrations | optionally at startup | indexes at startup |
| Engines | SQL Server, PostgreSQL, SQLite | the same three | MongoDB |

## Writing your own

Implement `IMediaStore` and register it:

```csharp
services.AddMedia()
    .UseCustomStore<MyStore>()
    .UseFileSystem("./uploads")
    .Build();
```

Stores are deliberately dumb: validation, checksums, deduplication, soft delete and the ordering of
the two writes all live in the engine. A store persists rows. Run `StoreContractTests` against it.
