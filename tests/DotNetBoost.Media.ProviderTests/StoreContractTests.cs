using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;

namespace DotNetBoost.Media.ProviderTests;

/// <summary>
/// The behaviour every <see cref="IMediaStore"/> has to have, run against each of them. A store
/// that passes this is one the engine can be pointed at; one that does not is a bug in the store,
/// wherever it lives.
/// <para>
/// The in-memory double runs it too. That is the point of it being here: a test built on the
/// double is only meaningful if the double behaves like the real thing, and this is what makes
/// that true rather than assumed.
/// </para>
/// </summary>
public abstract class StoreContractTests : IAsyncLifetime
{
    private static readonly DateTime Origin = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The store under test. Created in <see cref="InitializeAsync"/>.</summary>
    protected IMediaStore Store { get; private set; } = null!;

    /// <summary>Creates a store over a database of this test's own.</summary>
    protected abstract Task<IMediaStore> CreateStoreAsync();

    /// <summary>Releases whatever <see cref="CreateStoreAsync"/> allocated.</summary>
    protected virtual Task CleanupAsync() => Task.CompletedTask;

    public async Task InitializeAsync() => Store = await CreateStoreAsync();

    public Task DisposeAsync() => CleanupAsync();

    protected static MediaFile Row(
        string fileName = "photo.png",
        string? category = null,
        string? tags = null,
        string? owner = null,
        string? ownerId = null,
        string? checksum = null,
        long size = 100,
        int minutesLate = 0,
        string contentType = "image/png") =>
        new()
        {
            Id               = Guid.NewGuid(),
            FileName         = fileName,
            ContentType      = contentType,
            Size             = size,
            StorageProvider  = "Test",
            StorageContainer = "test",
            StoragePath      = $"2026/09/27/{Guid.NewGuid():N}.png",
            Checksum         = checksum,
            OwnerEntityName  = owner,
            OwnerEntityId    = ownerId,
            Category         = category,
            Tags             = tags,
            CreatedAt        = Origin.AddMinutes(minutesLate)
        };

    [Fact]
    public async Task Add_then_Find_round_trips_every_column()
    {
        var row = Row("report.pdf", "invoices", MediaTags.Join(["paid", "2026"]), "Invoice", "77", "abc123", 4096, contentType: "application/pdf");
        row.Metadata   = """{"pages":"3"}""";
        row.UploadedBy = "alice@example.com";

        await Store.AddAsync(row);

        var read = await Store.FindAsync(row.Id);

        Assert.NotNull(read);
        Assert.Equal(row.FileName, read.FileName);
        Assert.Equal(row.ContentType, read.ContentType);
        Assert.Equal(row.Size, read.Size);
        Assert.Equal(row.StorageProvider, read.StorageProvider);
        Assert.Equal(row.StorageContainer, read.StorageContainer);
        Assert.Equal(row.StoragePath, read.StoragePath);
        Assert.Equal(row.Checksum, read.Checksum);
        Assert.Equal(row.OwnerEntityName, read.OwnerEntityName);
        Assert.Equal(row.OwnerEntityId, read.OwnerEntityId);
        Assert.Equal(row.Category, read.Category);
        Assert.Equal(row.Tags, read.Tags);
        Assert.Equal(row.Metadata, read.Metadata);
        Assert.Equal(row.UploadedBy, read.UploadedBy);
        Assert.Equal(row.CreatedAt, read.CreatedAt);
        Assert.Null(read.UpdatedAt);
        Assert.Null(read.DeletedAt);
    }

    [Fact]
    public async Task Find_returns_null_for_an_unknown_id() =>
        Assert.Null(await Store.FindAsync(Guid.NewGuid()));

    [Fact]
    public async Task Add_refuses_a_duplicate_id()
    {
        var row = Row();
        await Store.AddAsync(row);

        var clash = Row();
        clash.Id = row.Id;

        await Assert.ThrowsAsync<MediaConflictException>(() => Store.AddAsync(clash));
    }

    [Fact]
    public async Task Update_writes_every_mutable_column()
    {
        var row = Row();
        await Store.AddAsync(row);

        var read = await Store.FindAsync(row.Id);
        read!.FileName        = "renamed.png";
        read.Category         = "archive";
        read.Tags             = MediaTags.Join(["kept"]);
        read.Metadata         = """{"a":"1"}""";
        read.OwnerEntityName  = "Product";
        read.OwnerEntityId    = "9";
        read.UpdatedAt        = Origin.AddHours(1);
        await Store.UpdateAsync(read);

        var after = await Store.FindAsync(row.Id);

        Assert.Equal("renamed.png", after!.FileName);
        Assert.Equal("archive", after.Category);
        Assert.Equal(",kept,", after.Tags);
        Assert.Equal("""{"a":"1"}""", after.Metadata);
        Assert.Equal("Product", after.OwnerEntityName);
        Assert.Equal("9", after.OwnerEntityId);
        Assert.Equal(Origin.AddHours(1), after.UpdatedAt);
    }

    [Fact]
    public async Task Update_throws_for_a_row_that_is_not_there()
    {
        var row = Row();

        await Assert.ThrowsAsync<MediaNotFoundException>(() => Store.UpdateAsync(row));
    }

    [Fact]
    public async Task A_soft_deleted_row_is_hidden_unless_asked_for()
    {
        var row = Row();
        await Store.AddAsync(row);

        var read = await Store.FindAsync(row.Id);
        read!.DeletedAt = Origin.AddHours(1);
        await Store.UpdateAsync(read);

        Assert.Null(await Store.FindAsync(row.Id));
        Assert.NotNull(await Store.FindAsync(row.Id, includeDeleted: true));
        Assert.Equal(0, await Store.CountAsync(new MediaQuery()));
        Assert.Equal(1, await Store.CountAsync(new MediaQuery { IncludeDeleted = true }));
    }

    [Fact]
    public async Task Delete_removes_the_row_and_reports_whether_it_did()
    {
        var row = Row();
        await Store.AddAsync(row);

        Assert.True(await Store.DeleteAsync(row.Id));
        Assert.False(await Store.DeleteAsync(row.Id));
        Assert.Null(await Store.FindAsync(row.Id, includeDeleted: true));
    }

    [Fact]
    public async Task Query_orders_newest_first_by_default()
    {
        await Store.AddAsync(Row("a.png", minutesLate: 0));
        await Store.AddAsync(Row("b.png", minutesLate: 1));
        await Store.AddAsync(Row("c.png", minutesLate: 2));

        var rows = await Store.QueryAsync(new MediaQuery());

        Assert.Equal(["c.png", "b.png", "a.png"], rows.Select(r => r.FileName));
    }

    [Fact]
    public async Task Query_can_order_oldest_first()
    {
        await Store.AddAsync(Row("a.png", minutesLate: 0));
        await Store.AddAsync(Row("b.png", minutesLate: 1));

        var rows = await Store.QueryAsync(new MediaQuery { OldestFirst = true });

        Assert.Equal(["a.png", "b.png"], rows.Select(r => r.FileName));
    }

    [Fact]
    public async Task Query_pages_and_Count_ignores_the_page()
    {
        for (var i = 0; i < 5; i++)
            await Store.AddAsync(Row($"{i}.png", minutesLate: i));

        var page = await Store.QueryAsync(new MediaQuery { Skip = 1, Take = 2, OldestFirst = true });

        Assert.Equal(["1.png", "2.png"], page.Select(r => r.FileName));
        Assert.Equal(5, await Store.CountAsync(new MediaQuery { Skip = 1, Take = 2 }));
    }

    [Fact]
    public async Task Query_filters_by_owner()
    {
        await Store.AddAsync(Row("a.png", owner: "Product", ownerId: "1"));
        await Store.AddAsync(Row("b.png", owner: "Product", ownerId: "2"));
        await Store.AddAsync(Row("c.png", owner: "Customer", ownerId: "1"));
        await Store.AddAsync(Row("d.png"));

        var rows = await Store.QueryAsync(new MediaQuery { OwnerEntityName = "Product", OwnerEntityId = "1" });

        Assert.Equal("a.png", Assert.Single(rows).FileName);
    }

    [Fact]
    public async Task Query_filters_by_category()
    {
        await Store.AddAsync(Row("a.png", category: "avatars"));
        await Store.AddAsync(Row("b.png", category: "invoices"));

        var rows = await Store.QueryAsync(new MediaQuery { Category = "avatars" });

        Assert.Equal("a.png", Assert.Single(rows).FileName);
    }

    [Fact]
    public async Task Query_matches_a_whole_tag_not_a_prefix()
    {
        await Store.AddAsync(Row("a.png", tags: MediaTags.Join(["invoice"])));
        await Store.AddAsync(Row("b.png", tags: MediaTags.Join(["invoiced"])));
        await Store.AddAsync(Row("c.png", tags: MediaTags.Join(["paid", "invoice"])));

        var rows = await Store.QueryAsync(new MediaQuery { Tag = "invoice" });

        Assert.Equal(["a.png", "c.png"], rows.Select(r => r.FileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Query_filters_by_content_type_prefix()
    {
        await Store.AddAsync(Row("a.png", contentType: "image/png"));
        await Store.AddAsync(Row("b.jpg", contentType: "image/jpeg"));
        await Store.AddAsync(Row("c.pdf", contentType: "application/pdf"));

        var rows = await Store.QueryAsync(new MediaQuery { ContentTypePrefix = "image/" });

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Query_filters_by_a_date_range()
    {
        await Store.AddAsync(Row("a.png", minutesLate: 0));
        await Store.AddAsync(Row("b.png", minutesLate: 10));
        await Store.AddAsync(Row("c.png", minutesLate: 20));

        var rows = await Store.QueryAsync(new MediaQuery
        {
            CreatedAfter  = Origin.AddMinutes(5),
            CreatedBefore = Origin.AddMinutes(15)
        });

        Assert.Equal("b.png", Assert.Single(rows).FileName);
    }

    [Fact]
    public async Task FindByChecksum_matches_on_hash_and_size_together()
    {
        await Store.AddAsync(Row("a.png", checksum: "aaa", size: 100));

        Assert.NotNull(await Store.FindByChecksumAsync("aaa", 100));
        // A hash collision on files of different lengths must not serve one as the other.
        Assert.Null(await Store.FindByChecksumAsync("aaa", 200));
        Assert.Null(await Store.FindByChecksumAsync("bbb", 100));
    }

    [Fact]
    public async Task FindByChecksum_ignores_soft_deleted_rows()
    {
        var row = Row("a.png", checksum: "aaa", size: 100);
        await Store.AddAsync(row);

        var read = await Store.FindAsync(row.Id);
        read!.DeletedAt = Origin;
        await Store.UpdateAsync(read);

        Assert.Null(await Store.FindByChecksumAsync("aaa", 100));
    }

    [Fact]
    public async Task A_tag_containing_a_wildcard_does_not_widen_the_match()
    {
        await Store.AddAsync(Row("a.png", tags: MediaTags.Join(["alpha"])));

        // "%" is a LIKE wildcard. Unescaped it would match every tagged row.
        var rows = await Store.QueryAsync(new MediaQuery { Tag = "%" });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task A_tag_containing_an_underscore_still_matches_itself()
    {
        // The other half of escaping, and the half that is easy to get wrong: "_" is a
        // single-character wildcard, so it has to be escaped — but an escaped pattern only works
        // if the statement also declares an ESCAPE character. Without that the query matches
        // nothing and a perfectly ordinary tag becomes unsearchable.
        await Store.AddAsync(Row("a.png", tags: MediaTags.Join(["draft_v2"])));
        await Store.AddAsync(Row("b.png", tags: MediaTags.Join(["draftxv2"])));

        var rows = await Store.QueryAsync(new MediaQuery { Tag = "draft_v2" });

        Assert.Equal("a.png", Assert.Single(rows).FileName);
    }

    [Fact]
    public async Task A_content_type_prefix_containing_a_wildcard_is_escaped_too()
    {
        await Store.AddAsync(Row("a.png", contentType: "image/png"));

        var rows = await Store.QueryAsync(new MediaQuery { ContentTypePrefix = "image%" });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Timestamps_come_back_as_UTC()
    {
        var row = Row();
        row.UpdatedAt = Origin.AddHours(2);
        row.DeletedAt = Origin.AddHours(3);
        await Store.AddAsync(row);

        var read = await Store.FindAsync(row.Id, includeDeleted: true);

        // SQL Server and SQLite both drop the kind. A value that comes back Unspecified
        // serializes to JSON without a "Z" and is read by every client as local time.
        Assert.Equal(DateTimeKind.Utc, read!.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, read.UpdatedAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, read.DeletedAt!.Value.Kind);
        Assert.Equal(Origin, read.CreatedAt);
        Assert.Equal(Origin.AddHours(2), read.UpdatedAt);
    }
}
