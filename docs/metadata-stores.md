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
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.ApplyMedia();       // at the end
    }
}
```

`ApplyMedia()` is the whole opt-in. **There is no interface to implement and no `DbSet` to
declare**: the store reaches both tables through `context.Set<T>()`, which works on any context
whose model has them, and application code goes through `IMediaManager` rather than the tables.
Two tables in total, whatever you attach media to.

> `IMediaDbContext` is `[Obsolete]` and will be removed in the next major version. It never did
> anything — `UseEntityFrameworkCore<TContext>` constrains on `DbContext` alone and the store never
> read its properties — so delete it from your context along with the two `DbSet`s.

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

**An upload saves your context.** The bytes are written before the row, so the row has to be
durable before `UploadAsync` returns — which means the store calls `SaveChangesAsync` on the
context you gave it. That is *your* context, so anything else it is tracking is written too. In the
example above, `product.Name` is already written by the time the explicit `SaveChangesAsync` runs;
the transaction is what makes that safe, because nothing is committed until `CommitAsync` and a
rollback takes the name with it.

Without a transaction there is no such safety net, and an upload in the middle of a half-built
aggregate commits the half. Two ways out, and the first is usually right — wrap the unit of work,
as above, so the upload joins it; or finish your own writes first, so there is nothing pending to
flush:

```csharp
product.Name = request.Name;
await db.SaveChangesAsync(ct);                     // your work, committed on its own

await media.For(product).UploadAsync(upload, ct);  // now it has only the media row to save
```

This is the cost of the rows living in your context rather than a context of their own, and it buys
the paragraph above: one transaction over your entity and its media. A store that owned its own
context could not flush your work — and could not commit with it either.

Supported engines: SQL Server, PostgreSQL, SQLite. Timestamps are stored UTC and read back as
`DateTimeKind.Utc` through a value converter, because SQL Server and SQLite both otherwise return
`Unspecified` — which serializes to JSON without a `Z` and is read by every client as local time.

Rename the tables if you need to:

```csharp
modelBuilder.ApplyMedia(o =>
{
    o.MediaFilesTable       = "Uploads";
    o.MediaAttachmentsTable = "UploadLinks";
    o.Schema                = "media";
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

`migrateSchema: true` creates both tables at startup if they are absent, in exactly the shape the
EF Core store maps — same names, same lengths, same indexes — so a database serves either. It
**only ever creates** — it will not alter a table whose shape has changed. Leave it off when migrations own your schema, and take the DDL to
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

Two collections, `mediaFiles` and `mediaAttachments`, each document's own id as its `_id` — so a
lookup by id is a primary-key read and needs no secondary index. The indexes the relational stores
create are created here too, at startup, unless `CreateIndexes` says otherwise;
`MongoMediaIndexes.EnsureCreatedAsync` does the same from a deployment step.

The attachments are a collection rather than an array inside each file document, which is the more
obviously document-shaped choice and the wrong one: MongoDB sorts an array field by its smallest
element, not by the one a filter matched, so "this product's gallery in order" would have ordered by
the lowest position the file holds for *any* owner. Separate documents also keep the indexes the
same shape as the relational stores'. Without a join inside a `find`, an owner filter is resolved
first — a covered distinct turns it into a set of file ids — so it is two round trips rather than
one.

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

Two methods are worth reading twice, and for the same reason: both have an implementation that
passes the contract tests and is still wrong.

`UpdateAttachmentsAsync` is handed every link a reorder moved, and is meant to write them **in one
operation**. Looping over `UpdateAttachmentAsync` passes the tests, but it costs a round trip per
image and — because each call commits — it lets a failure halfway through leave a gallery in an
order nobody chose. The shipped stores use one batched `SaveChanges`, one `BulkWrite`, and one
statement on the caller's own connection.

`ListAttachmentsForOwnersAsync` is handed many owners at once and must answer in **one statement**,
with the file rows joined in. Looping `ListAttachmentsAsync` over the ids also passes, and also
defeats the entire point: that method exists only because reading a listing page one owner at a
time is what `IMediaBulkAccessor` was added to stop. The shipped stores use a join (EF Core and
Dapper) and a `$lookup` aggregation (MongoDB). Two details the contract pins down:

- The join is **inner** and filtered to live files, so a link whose file is soft-deleted returns
  nothing rather than half a row. It should not arise — a delete removes a file's links outright,
  tombstone or not — but a store must not produce it if it does.
- An owner with nothing attached is simply **absent**. Turning that into an empty entry needs the
  list of ids asked about, which is the engine's; the store answers about rows.

`StoreContractTests` covers both, including a fifty-owner read, so a store that expands ids into a
statement is checked at the size a real page actually is.
