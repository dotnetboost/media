using System.Globalization;
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
            Category         = category,
            Tags             = tags,
            CreatedAt        = Origin.AddMinutes(minutesLate)
        };

    protected static MediaAttachment Link(
        Guid mediaFileId,
        string owner = "Product",
        string ownerId = "1",
        string role = MediaRoles.Default,
        int sortOrder = 0,
        int minutesLate = 0) =>
        new()
        {
            Id              = Guid.NewGuid(),
            MediaFileId     = mediaFileId,
            OwnerEntityName = owner,
            OwnerEntityId   = ownerId,
            Role            = role,
            SortOrder       = sortOrder,
            CreatedAt       = Origin.AddMinutes(minutesLate)
        };

    [Fact]
    public async Task Add_then_Find_round_trips_every_column()
    {
        var row = Row("report.pdf", "invoices", MediaTags.Join(["paid", "2026"]), "abc123", 4096, contentType: "application/pdf");
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
        read.UpdatedAt        = Origin.AddHours(1);
        await Store.UpdateAsync(read);

        var after = await Store.FindAsync(row.Id);

        Assert.Equal("renamed.png", after!.FileName);
        Assert.Equal("archive", after.Category);
        Assert.Equal(",kept,", after.Tags);
        Assert.Equal("""{"a":"1"}""", after.Metadata);
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

        var rows = await Store.QueryAsync(new MediaQuery { Descending = false });

        Assert.Equal(["a.png", "b.png"], rows.Select(r => r.FileName));
    }

    [Fact]
    public async Task Query_pages_and_Count_ignores_the_page()
    {
        for (var i = 0; i < 5; i++)
            await Store.AddAsync(Row($"{i}.png", minutesLate: i));

        var page = await Store.QueryAsync(new MediaQuery { Skip = 1, Take = 2, Descending = false });

        Assert.Equal(["1.png", "2.png"], page.Select(r => r.FileName));
        Assert.Equal(5, await Store.CountAsync(new MediaQuery { Skip = 1, Take = 2 }));
    }

    [Fact]
    public async Task Query_filters_by_owner()
    {
        var a = Row("a.png");
        var b = Row("b.png");
        var c = Row("c.png");

        await Store.AddAsync(a);
        await Store.AddAsync(b);
        await Store.AddAsync(c);
        await Store.AddAsync(Row("d.png"));

        await Store.AddAttachmentAsync(Link(a.Id, "Product", "1"));
        await Store.AddAttachmentAsync(Link(b.Id, "Product", "2"));
        await Store.AddAttachmentAsync(Link(c.Id, "Customer", "1"));

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
    public async Task Query_searches_the_file_name_anywhere_in_it()
    {
        await Store.AddAsync(Row("quarterly-report.pdf"));
        await Store.AddAsync(Row("holiday-photo.png"));

        var rows = await Store.QueryAsync(new MediaQuery { Search = "report" });

        Assert.Equal("quarterly-report.pdf", Assert.Single(rows).FileName);
    }

    [Fact]
    public async Task Query_search_ignores_case()
    {
        // LIKE is case-sensitive on PostgreSQL and case-insensitive on SQL Server by default, so
        // without both sides lowered this passes on one engine and fails on the other.
        await Store.AddAsync(Row("Quarterly-REPORT.pdf"));

        var rows = await Store.QueryAsync(new MediaQuery { Search = "quarterly-report" });

        Assert.Single(rows);
    }

    [Fact]
    public async Task Query_search_treats_wildcards_as_literal_characters()
    {
        await Store.AddAsync(Row("report_final.pdf"));
        await Store.AddAsync(Row("reportxfinal.pdf"));

        // "_" is a single-character wildcard. Unescaped, this matches both.
        var rows = await Store.QueryAsync(new MediaQuery { Search = "report_final" });

        Assert.Equal("report_final.pdf", Assert.Single(rows).FileName);

        // And "%" must not match everything.
        Assert.Empty(await Store.QueryAsync(new MediaQuery { Search = "%" }));
    }

    [Fact]
    public async Task Query_search_combines_with_the_other_filters()
    {
        await Store.AddAsync(Row("report-a.pdf", category: "invoices"));
        await Store.AddAsync(Row("report-b.pdf", category: "manuals"));

        var rows = await Store.QueryAsync(new MediaQuery { Search = "report", Category = "invoices" });

        Assert.Equal("report-a.pdf", Assert.Single(rows).FileName);
    }

    [Fact]
    public async Task Query_sorts_by_file_name()
    {
        await Store.AddAsync(Row("charlie.png", minutesLate: 0));
        await Store.AddAsync(Row("alpha.png", minutesLate: 1));
        await Store.AddAsync(Row("bravo.png", minutesLate: 2));

        var ascending = await Store.QueryAsync(new MediaQuery { SortBy = MediaSortBy.FileName, Descending = false });
        Assert.Equal(["alpha.png", "bravo.png", "charlie.png"], ascending.Select(r => r.FileName));

        var descending = await Store.QueryAsync(new MediaQuery { SortBy = MediaSortBy.FileName });
        Assert.Equal(["charlie.png", "bravo.png", "alpha.png"], descending.Select(r => r.FileName));
    }

    [Fact]
    public async Task Query_sorts_by_size()
    {
        await Store.AddAsync(Row("medium.png", size: 500));
        await Store.AddAsync(Row("small.png", size: 10));
        await Store.AddAsync(Row("large.png", size: 9000));

        var ascending = await Store.QueryAsync(new MediaQuery { SortBy = MediaSortBy.Size, Descending = false });
        Assert.Equal(["small.png", "medium.png", "large.png"], ascending.Select(r => r.FileName));

        var descending = await Store.QueryAsync(new MediaQuery { SortBy = MediaSortBy.Size });
        Assert.Equal(["large.png", "medium.png", "small.png"], descending.Select(r => r.FileName));
    }

    [Fact]
    public async Task Sorting_pages_stably_when_rows_tie()
    {
        // Three files of identical size: the sort column cannot order them, so only the id
        // tiebreaker stops a page from repeating one row and skipping another.
        for (var i = 0; i < 6; i++)
            await Store.AddAsync(Row($"{i}.png", size: 100));

        var first  = await Store.QueryAsync(new MediaQuery { SortBy = MediaSortBy.Size, Take = 3 });
        var second = await Store.QueryAsync(new MediaQuery { SortBy = MediaSortBy.Size, Skip = 3, Take = 3 });

        var seen = first.Concat(second).Select(r => r.Id).ToList();
        Assert.Equal(6, seen.Distinct().Count());
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

    [Fact]
    public async Task Attachment_round_trips_every_column()
    {
        var row = Row();
        await Store.AddAsync(row);

        var link = Link(row.Id, "Product", "42", "gallery", sortOrder: 3);
        await Store.AddAttachmentAsync(link);

        var read = Assert.Single(await Store.ListAttachmentsOfFileAsync(row.Id));

        Assert.Equal(link.Id, read.Id);
        Assert.Equal(row.Id, read.MediaFileId);
        Assert.Equal("Product", read.OwnerEntityName);
        Assert.Equal("42", read.OwnerEntityId);
        Assert.Equal("gallery", read.Role);
        Assert.Equal(3, read.SortOrder);
        Assert.Equal(Origin, read.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, read.CreatedAt.Kind);
    }

    [Fact]
    public async Task One_file_can_be_attached_to_two_entities()
    {
        var row = Row();
        await Store.AddAsync(row);

        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1"));
        await Store.AddAttachmentAsync(Link(row.Id, "Product", "2"));

        Assert.Equal(2, (await Store.ListAttachmentsOfFileAsync(row.Id)).Count);

        Assert.Single(await Store.QueryAsync(
            new MediaQuery { OwnerEntityName = "Product", OwnerEntityId = "1" }));
        Assert.Single(await Store.QueryAsync(
            new MediaQuery { OwnerEntityName = "Product", OwnerEntityId = "2" }));
    }

    [Fact]
    public async Task A_file_attached_twice_to_one_entity_is_returned_once()
    {
        var row = Row();
        await Store.AddAsync(row);

        await Store.AddAttachmentAsync(Link(row.Id, role: "primary"));
        await Store.AddAttachmentAsync(Link(row.Id, role: "seo"));

        var rows = await Store.QueryAsync(new MediaQuery { OwnerEntityName = "Product", OwnerEntityId = "1" });

        Assert.Single(rows);
        Assert.Equal(1, await Store.CountAsync(new MediaQuery { OwnerEntityName = "Product", OwnerEntityId = "1" }));
    }

    [Fact]
    public async Task Attaching_the_same_file_twice_in_one_role_is_refused()
    {
        var row = Row();
        await Store.AddAsync(row);

        await Store.AddAttachmentAsync(Link(row.Id, role: "gallery"));

        await Assert.ThrowsAsync<MediaConflictException>(
            () => Store.AddAttachmentAsync(Link(row.Id, role: "gallery")));
    }

    [Fact]
    public async Task Query_filters_by_role()
    {
        var hero = Row("hero.png");
        var shot = Row("shot.png");

        await Store.AddAsync(hero);
        await Store.AddAsync(shot);

        await Store.AddAttachmentAsync(Link(hero.Id, role: "primary"));
        await Store.AddAttachmentAsync(Link(shot.Id, role: "gallery"));

        var rows = await Store.QueryAsync(new MediaQuery
        {
            OwnerEntityName = "Product",
            OwnerEntityId   = "1",
            Role            = "primary"
        });

        Assert.Equal("hero.png", Assert.Single(rows).FileName);
    }

    [Fact]
    public async Task Query_sorts_by_the_entitys_own_order()
    {
        var first  = Row("first.png");
        var second = Row("second.png");
        var third  = Row("third.png");

        // Added in an order that is neither the sort order nor its reverse, so a store that ignores
        // SortOrder and falls back to CreatedAt or the id cannot pass by accident.
        await Store.AddAsync(second);
        await Store.AddAsync(third);
        await Store.AddAsync(first);

        await Store.AddAttachmentAsync(Link(second.Id, role: "gallery", sortOrder: 1));
        await Store.AddAttachmentAsync(Link(third.Id, role: "gallery", sortOrder: 2));
        await Store.AddAttachmentAsync(Link(first.Id, role: "gallery", sortOrder: 0));

        var rows = await Store.QueryAsync(new MediaQuery
        {
            OwnerEntityName = "Product",
            OwnerEntityId   = "1",
            Role            = "gallery",
            SortBy          = MediaSortBy.SortOrder,
            Descending      = false
        });

        Assert.Equal(["first.png", "second.png", "third.png"], rows.Select(r => r.FileName));
    }

    [Fact]
    public async Task Query_finds_the_files_nothing_is_attached_to()
    {
        var loose = Row("loose.png");
        var used  = Row("used.png");

        await Store.AddAsync(loose);
        await Store.AddAsync(used);
        await Store.AddAttachmentAsync(Link(used.Id));

        var rows = await Store.QueryAsync(new MediaQuery { Unattached = true });

        Assert.Equal("loose.png", Assert.Single(rows).FileName);
    }

    [Fact]
    public async Task An_unfiltered_query_returns_attached_and_unattached_files_alike()
    {
        var loose = Row("loose.png");
        var used  = Row("used.png");

        await Store.AddAsync(loose);
        await Store.AddAsync(used);
        await Store.AddAttachmentAsync(Link(used.Id));

        Assert.Equal(2, await Store.CountAsync(new MediaQuery()));
    }

    [Fact]
    public async Task ListAttachments_returns_an_owners_links_by_role_then_position()
    {
        var a = Row("a.png");
        var b = Row("b.png");
        var c = Row("c.png");

        await Store.AddAsync(a);
        await Store.AddAsync(b);
        await Store.AddAsync(c);

        await Store.AddAttachmentAsync(Link(b.Id, role: "gallery", sortOrder: 1));
        await Store.AddAttachmentAsync(Link(c.Id, role: "primary", sortOrder: 0));
        await Store.AddAttachmentAsync(Link(a.Id, role: "gallery", sortOrder: 0));

        var links = await Store.ListAttachmentsAsync(new MediaOwner("Product", "1"));

        Assert.Equal([a.Id, b.Id, c.Id], links.Select(l => l.MediaFileId));

        var gallery = await Store.ListAttachmentsAsync(new MediaOwner("Product", "1"), "gallery");
        Assert.Equal([a.Id, b.Id], gallery.Select(l => l.MediaFileId));
    }

    [Fact]
    public async Task ListAttachments_does_not_reach_another_owner()
    {
        var row = Row();
        await Store.AddAsync(row);

        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1"));
        await Store.AddAttachmentAsync(Link(row.Id, "Product", "2"));

        Assert.Single(await Store.ListAttachmentsAsync(new MediaOwner("Product", "1")));
    }

    [Fact]
    public async Task UpdateAttachment_writes_the_new_position()
    {
        var row = Row();
        await Store.AddAsync(row);

        var link = Link(row.Id, role: "gallery");
        await Store.AddAttachmentAsync(link);

        var read = Assert.Single(await Store.ListAttachmentsAsync(new MediaOwner("Product", "1")));
        read.SortOrder = 7;
        await Store.UpdateAttachmentAsync(read);

        var after = Assert.Single(await Store.ListAttachmentsAsync(new MediaOwner("Product", "1")));
        Assert.Equal(7, after.SortOrder);
    }

    [Fact]
    public async Task UpdateAttachment_throws_for_a_link_that_is_not_there()
    {
        var row = Row();
        await Store.AddAsync(row);

        await Assert.ThrowsAsync<MediaNotFoundException>(() => Store.UpdateAttachmentAsync(Link(row.Id)));
    }

    /// <summary>
    /// What a reorder saves. Every row in one call, so a store can do it in one command and one
    /// transaction rather than one of each per image.
    /// </summary>
    [Fact]
    public async Task UpdateAttachments_writes_every_new_position_at_once()
    {
        var owner = new MediaOwner("Product", "1");
        var links = new List<MediaAttachment>();

        for (var i = 0; i < 5; i++)
        {
            var row = Row();
            await Store.AddAsync(row);

            var link = Link(row.Id, role: "gallery", sortOrder: i, minutesLate: i);
            await Store.AddAttachmentAsync(link);
            links.Add(link);
        }

        // Reversed, which is the case that matters: every row moves, and none of the new
        // positions is free, so a store writing them one at a time would pass through states
        // where two links share a position.
        var reordered = await Store.ListAttachmentsAsync(owner, "gallery");
        var wanted    = reordered.Reverse().ToArray();

        for (var i = 0; i < wanted.Length; i++)
            wanted[i].SortOrder = i;

        await Store.UpdateAttachmentsAsync(wanted);

        var after = await Store.ListAttachmentsAsync(owner, "gallery");

        Assert.Equal(
            links.Select(l => l.MediaFileId).Reverse(),
            after.Select(a => a.MediaFileId));

        Assert.Equal([0, 1, 2, 3, 4], after.Select(a => a.SortOrder));
    }

    [Fact]
    public async Task UpdateAttachments_does_nothing_when_given_nothing() =>
        await Store.UpdateAttachmentsAsync([]);

    /// <summary>
    /// A batch naming a link that has since been detached is reported, and reported as the
    /// missing link rather than as some generic write failure.
    /// </summary>
    [Fact]
    public async Task UpdateAttachments_throws_for_a_link_that_is_not_there()
    {
        var row = Row();
        await Store.AddAsync(row);

        var present = Link(row.Id, role: "gallery");
        await Store.AddAttachmentAsync(present);

        var absent = Link(row.Id, role: "gallery", sortOrder: 1);

        var thrown = await Assert.ThrowsAsync<MediaNotFoundException>(
            () => Store.UpdateAttachmentsAsync([present, absent]));

        Assert.Equal(absent.Id, thrown.Id);
    }

    [Fact]
    public async Task RemoveAttachment_removes_one_link_and_leaves_the_file()
    {
        var row = Row();
        await Store.AddAsync(row);

        var link = Link(row.Id);
        await Store.AddAttachmentAsync(link);

        Assert.True(await Store.RemoveAttachmentAsync(link.Id));
        Assert.False(await Store.RemoveAttachmentAsync(link.Id));

        Assert.Empty(await Store.ListAttachmentsOfFileAsync(row.Id));
        Assert.NotNull(await Store.FindAsync(row.Id));
    }

    [Fact]
    public async Task RemoveAttachments_takes_one_owner_or_one_role()
    {
        var row = Row();
        await Store.AddAsync(row);

        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1", "primary"));
        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1", "gallery"));
        await Store.AddAttachmentAsync(Link(row.Id, "Product", "2", "primary"));

        Assert.Equal(1, await Store.RemoveAttachmentsAsync(new MediaOwner("Product", "1"), "gallery"));
        Assert.Equal(1, await Store.RemoveAttachmentsAsync(new MediaOwner("Product", "1")));

        // Product 2 was never in scope.
        Assert.Single(await Store.ListAttachmentsOfFileAsync(row.Id));
        Assert.NotNull(await Store.FindAsync(row.Id));
    }

    [Fact]
    public async Task RemoveAttachmentsOfFile_clears_every_owner()
    {
        var row = Row();
        await Store.AddAsync(row);

        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1"));
        await Store.AddAttachmentAsync(Link(row.Id, "Product", "2"));
        await Store.AddAttachmentAsync(Link(row.Id, "Customer", "1"));

        Assert.Equal(3, await Store.RemoveAttachmentsOfFileAsync(row.Id));
        Assert.Empty(await Store.ListAttachmentsOfFileAsync(row.Id));
    }

    [Fact]
    public async Task FindShared_names_the_files_another_owner_still_holds()
    {
        var shared = Row("shared.png");
        var mine   = Row("mine.png");

        await Store.AddAsync(shared);
        await Store.AddAsync(mine);

        await Store.AddAttachmentAsync(Link(shared.Id, "Product", "1"));
        await Store.AddAttachmentAsync(Link(mine.Id, "Product", "1"));
        await Store.AddAttachmentAsync(Link(shared.Id, "Product", "2"));

        var found = await Store.FindSharedAsync([shared.Id, mine.Id], new MediaOwner("Product", "1"));

        Assert.Equal([shared.Id], found);
    }

    [Fact]
    public async Task FindShared_ignores_the_owners_own_other_roles()
    {
        var row = Row();
        await Store.AddAsync(row);

        // Attached twice, but to nobody else: this entity is still the only holder.
        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1", "primary"));
        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1", "seo"));

        Assert.Empty(await Store.FindSharedAsync([row.Id], new MediaOwner("Product", "1")));
    }

    [Fact]
    public async Task FindShared_of_nothing_is_nothing() =>
        Assert.Empty(await Store.FindSharedAsync([], new MediaOwner("Product", "1")));

    [Fact]
    public async Task ListAttachmentsForOwners_answers_about_every_owner_at_once()
    {
        var one = Row("one.png");
        var two = Row("two.png");

        await Store.AddAsync(one);
        await Store.AddAsync(two);

        await Store.AddAttachmentAsync(Link(one.Id, "Product", "1", "primary"));
        await Store.AddAttachmentAsync(Link(two.Id, "Product", "2", "primary"));
        // Not asked about, and a different entity type with the same key as one that is.
        await Store.AddAttachmentAsync(Link(one.Id, "Product", "3", "primary"));
        await Store.AddAttachmentAsync(Link(one.Id, "Customer", "1", "primary"));

        var rows = await Store.ListAttachmentsForOwnersAsync("Product", ["1", "2"]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["1", "2"], rows.Select(r => r.Attachment.OwnerEntityId));

        // The file is joined in, not left to a second read — that is the whole point of the method.
        Assert.Equal(["one.png", "two.png"], rows.Select(r => r.File.FileName));
    }

    [Fact]
    public async Task ListAttachmentsForOwners_orders_by_owner_then_role_then_position()
    {
        // Two files, because the same file cannot hold one role on one owner twice — that pair is
        // the uniqueness constraint, and a gallery of two needs two files.
        var first  = Row("first.png");
        var second = Row("second.png");

        await Store.AddAsync(first);
        await Store.AddAsync(second);

        await Store.AddAttachmentAsync(Link(first.Id, "Product", "2", "primary"));
        await Store.AddAttachmentAsync(Link(second.Id, "Product", "1", "gallery", sortOrder: 1));
        await Store.AddAttachmentAsync(Link(first.Id, "Product", "1", "gallery", sortOrder: 0));
        await Store.AddAttachmentAsync(Link(first.Id, "Product", "1", "primary"));

        var rows = await Store.ListAttachmentsForOwnersAsync("Product", ["1", "2"]);

        // Owner first, then role, then position within the role — the same order
        // ListAttachmentsAsync gives one owner, so a bulk read and a single read agree.
        Assert.Equal(
            [("1", "gallery", 0), ("1", "gallery", 1), ("1", "primary", 0), ("2", "primary", 0)],
            rows.Select(r => (r.Attachment.OwnerEntityId, r.Attachment.Role, r.Attachment.SortOrder)));
    }

    [Fact]
    public async Task ListAttachmentsForOwners_narrows_to_one_role()
    {
        var row = Row();
        await Store.AddAsync(row);

        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1", "primary"));
        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1", "gallery"));
        await Store.AddAttachmentAsync(Link(row.Id, "Product", "2", "gallery"));

        var rows = await Store.ListAttachmentsForOwnersAsync("Product", ["1", "2"], "gallery");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("gallery", r.Attachment.Role));
    }

    [Fact]
    public async Task ListAttachmentsForOwners_lists_a_file_once_per_role_it_holds()
    {
        var row = Row();
        await Store.AddAsync(row);

        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1", "primary"));
        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1", "seo"));

        // Per link, not per file: the question is what this entity uses the file FOR, and it uses
        // it for two things.
        var rows = await Store.ListAttachmentsForOwnersAsync("Product", ["1"]);

        Assert.Equal(["primary", "seo"], rows.Select(r => r.Attachment.Role));
        Assert.All(rows, r => Assert.Equal(row.Id, r.File.Id));
    }

    [Fact]
    public async Task ListAttachmentsForOwners_leaves_out_a_link_to_a_deleted_file()
    {
        var live = Row("live.png");
        var gone = Row("gone.png");

        await Store.AddAsync(live);
        await Store.AddAsync(gone);

        await Store.AddAttachmentAsync(Link(live.Id, "Product", "1"));
        await Store.AddAttachmentAsync(Link(gone.Id, "Product", "1", "gallery"));

        // Tombstoned directly, rather than through the engine, which would have removed the link
        // along with it. This is the pairing the join has to refuse: half a row is worse than none.
        gone.DeletedAt = new DateTime(2026, 9, 27, 11, 0, 0, DateTimeKind.Utc);
        await Store.UpdateAsync(gone);

        var rows = await Store.ListAttachmentsForOwnersAsync("Product", ["1"]);

        Assert.Equal(live.Id, Assert.Single(rows).File.Id);
    }

    [Fact]
    public async Task ListAttachmentsForOwners_says_nothing_about_an_owner_with_nothing_attached()
    {
        var row = Row();
        await Store.AddAsync(row);
        await Store.AddAttachmentAsync(Link(row.Id, "Product", "1"));

        // Absent rather than present-and-empty: the store answers about rows, and turning "no rows
        // for this id" into an empty entry needs the list of ids asked about, which is the
        // engine's. IMediaBulkAccessor is where that happens.
        var rows = await Store.ListAttachmentsForOwnersAsync("Product", ["1", "2"]);

        Assert.Equal("1", Assert.Single(rows).Attachment.OwnerEntityId);
    }

    [Fact]
    public async Task ListAttachmentsForOwners_of_no_owners_is_nothing() =>
        Assert.Empty(await Store.ListAttachmentsForOwnersAsync("Product", []));

    [Fact]
    public async Task ListAttachmentsForOwners_handles_more_owners_than_a_page_would_hold()
    {
        // Fifty owners, which is what a listing page actually asks about, and the reason this
        // method exists. A store that expands the ids into the statement has to still be able to
        // parse it at this size.
        var row = Row();
        await Store.AddAsync(row);

        var ids = Enumerable.Range(1, 50).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();

        foreach (var id in ids)
            await Store.AddAttachmentAsync(Link(row.Id, "Product", id));

        var rows = await Store.ListAttachmentsForOwnersAsync("Product", ids);

        Assert.Equal(50, rows.Count);
        Assert.Equal(50, rows.Select(r => r.Attachment.OwnerEntityId).Distinct().Count());
    }
}
