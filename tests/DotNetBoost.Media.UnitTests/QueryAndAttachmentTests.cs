using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Attributes;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.TestSupport;

namespace DotNetBoost.Media.UnitTests;

public class QueryTests
{
    [Fact]
    public async Task List_returns_newest_first_with_a_total()
    {
        var harness = Harness.Create();

        for (var i = 0; i < 3; i++)
        {
            await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), $"{i}.png"));
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var page = await harness.Media.ListAsync(new MediaQuery { Take = 2 });

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("2.png", page.Items[0].FileName);
        Assert.Equal("1.png", page.Items[1].FileName);
    }

    [Fact]
    public async Task List_can_return_oldest_first()
    {
        var harness = Harness.Create();

        for (var i = 0; i < 3; i++)
        {
            await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), $"{i}.png"));
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var page = await harness.Media.ListAsync(new MediaQuery { Descending = false });

        Assert.Equal("0.png", page.Items[0].FileName);
    }

    [Fact]
    public async Task List_pages_with_skip()
    {
        var harness = Harness.Create();

        for (var i = 0; i < 5; i++)
        {
            await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), $"{i}.png"));
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var page = await harness.Media.ListAsync(new MediaQuery { Skip = 2, Take = 2, Descending = false });

        Assert.Equal(5, page.Total);
        Assert.Equal(["2.png", "3.png"], page.Items.Select(f => f.FileName));
    }

    [Fact]
    public async Task List_filters_by_category()
    {
        var harness = Harness.Create();
        await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png", category: "avatars"));
        await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "b.png", category: "invoices"));

        var page = await harness.Media.ListAsync(new MediaQuery { Category = "avatars" });

        Assert.Equal("a.png", Assert.Single(page.Items).FileName);
    }

    [Fact]
    public async Task List_matches_a_whole_tag_not_a_prefix()
    {
        var harness = Harness.Create();
        await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png", tags: ["invoice"]));
        await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "b.png", tags: ["invoiced"]));

        var page = await harness.Media.ListAsync(new MediaQuery { Tag = "invoice" });

        Assert.Equal("a.png", Assert.Single(page.Items).FileName);
    }

    [Fact]
    public async Task List_filters_by_content_type_family()
    {
        var harness = Harness.Create();
        await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Unrecognised(), "b.txt", "text/plain"));

        var page = await harness.Media.ListAsync(new MediaQuery { ContentTypePrefix = "image/" });

        Assert.Equal("a.png", Assert.Single(page.Items).FileName);
    }

    [Fact]
    public async Task List_caps_the_page_size()
    {
        var query = new MediaQuery { Take = 10_000 };

        Assert.Equal(MediaQuery.MaxTake, query.Take);
    }

    [Fact]
    public async Task List_refuses_an_owner_key_without_an_owner_type()
    {
        var harness = Harness.Create();

        await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.ListAsync(new MediaQuery { OwnerEntityId = "1" }));
    }
}

/// <summary>Attaching, and the entity-scoped accessor that does it.</summary>
public class AttachmentTests
{
    private sealed class Product
    {
        public int Id { get; set; }
    }

    [MediaEntity("Customer")]
    private sealed class CustomerEntity
    {
        public Guid Id { get; set; }
    }

    [Fact]
    public async Task For_attaches_an_upload_to_the_entity()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 42 };

        var file = await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        var attachment = Assert.Single(harness.Store.Attachments);
        Assert.Equal(file.Id, attachment.MediaFileId);
        Assert.Equal("Product", attachment.OwnerEntityName);
        Assert.Equal("42", attachment.OwnerEntityId);
        Assert.Equal(MediaRoles.Default, attachment.Role);
    }

    [Fact]
    public async Task For_uses_the_MediaEntity_name_when_there_is_one()
    {
        var harness  = Harness.Create();
        var customer = new CustomerEntity { Id = Guid.NewGuid() };

        await harness.Media.For(customer).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        var attachment = Assert.Single(harness.Store.Attachments);
        Assert.Equal("Customer", attachment.OwnerEntityName);
        Assert.Equal(customer.Id.ToString("D"), attachment.OwnerEntityId);
    }

    [Fact]
    public void For_refuses_an_entity_whose_key_is_unassigned()
    {
        var harness = Harness.Create();

        var error = Assert.Throws<InvalidOperationException>(() => harness.Media.For(new Product()));

        Assert.Contains("no key yet", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task For_lists_only_that_entitys_files()
    {
        var harness = Harness.Create();

        await harness.Media.For(new Product { Id = 1 }).UploadAsync(TestMedia.Upload(TestMedia.Png(), "one.png"));
        await harness.Media.For(new Product { Id = 2 }).UploadAsync(TestMedia.Upload(TestMedia.Png(), "two.png"));

        var files = await harness.Media.For(new Product { Id = 1 }).ListAsync();

        Assert.Equal("one.png", Assert.Single(files).FileName);
    }

    [Fact]
    public async Task For_lists_by_category()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png", category: "gallery"));
        await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(), "b.png", category: "manuals"));

        var files = await harness.Media.For(product).ListByCategoryAsync("gallery");

        Assert.Equal("a.png", Assert.Single(files).FileName);
    }

    [Fact]
    public async Task Add_adopts_a_file_uploaded_before_the_entity_existed()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.Empty(harness.Store.Attachments);

        await harness.Media.For(new Product { Id = 7 }).AddAsync(file.Id);

        var attachment = Assert.Single(harness.Store.Attachments);
        Assert.Equal("7", attachment.OwnerEntityId);
    }

    /// <summary>
    /// The whole point of the attachment table. Under the single owner column this was impossible:
    /// attaching to the second product silently detached the first.
    /// </summary>
    [Fact]
    public async Task One_file_can_be_attached_to_two_entities_at_once()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "shared.png"));

        await harness.Media.For(new Product { Id = 1 }).AddAsync(file.Id);
        await harness.Media.For(new Product { Id = 2 }).AddAsync(file.Id);

        var first  = await harness.Media.For(new Product { Id = 1 }).ListAsync();
        var second = await harness.Media.For(new Product { Id = 2 }).ListAsync();

        Assert.Equal(file.Id, Assert.Single(first).Id);
        Assert.Equal(file.Id, Assert.Single(second).Id);
        Assert.Equal(1, harness.Store.Count);
        Assert.Single(harness.Storage.Paths);
    }

    [Fact]
    public async Task Add_twice_in_the_same_role_changes_nothing()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(product).AddAsync(file.Id, "gallery");
        await harness.Media.For(product).AddAsync(file.Id, "gallery");

        Assert.Single(harness.Store.Attachments);
    }

    [Fact]
    public async Task One_file_can_hold_two_roles_on_the_same_entity()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(product).SetAsync("primary", file.Id);
        await harness.Media.For(product).SetAsync("seo", file.Id);

        Assert.Equal(2, harness.Store.Attachments.Count);

        // Two links, but the file is one row and appears once in the entity's listing.
        var files = await harness.Media.For(product).ListAsync();
        Assert.Equal(file.Id, Assert.Single(files).Id);
    }

    [Fact]
    public async Task Add_refuses_a_file_that_does_not_exist()
    {
        var harness = Harness.Create();

        await Assert.ThrowsAsync<MediaNotFoundException>(
            () => harness.Media.For(new Product { Id = 1 }).AddAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetAttachments_reports_every_entity_using_a_file()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(new Product { Id = 1 }).AddAsync(file.Id);
        await harness.Media.For(new Product { Id = 2 }).AddAsync(file.Id);

        var attachments = await harness.Media.GetAttachmentsAsync(file.Id);

        Assert.Equal(2, attachments.Count);
        Assert.Equal(["1", "2"], attachments.Select(a => a.Owner.EntityId).Order());
    }

    [Fact]
    public async Task GetAttachments_refuses_a_file_that_does_not_exist()
    {
        var harness = Harness.Create();

        await Assert.ThrowsAsync<MediaNotFoundException>(() => harness.Media.GetAttachmentsAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// Deduplication used to return the existing file and drop the owner on the floor, so this
    /// upload reported success and attached nothing. With the link in its own row there is somewhere
    /// for the second entity to go.
    /// </summary>
    [Fact]
    public async Task A_deduplicated_upload_still_attaches_to_the_second_entity()
    {
        var harness = Harness.Create(o =>
        {
            o.ComputeChecksum       = true;
            o.DeduplicateByChecksum = true;
        });

        var first  = await harness.Media.For(new Product { Id = 1 })
            .UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        var second = await harness.Media.For(new Product { Id = 2 })
            .UploadAsync(TestMedia.Upload(TestMedia.Png(), "b.png"));

        // Same bytes, so one file and one stored object...
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, harness.Store.Count);
        Assert.Single(harness.Storage.Paths);

        // ...but both products have it.
        Assert.Single(await harness.Media.For(new Product { Id = 1 }).ListAsync());
        Assert.Single(await harness.Media.For(new Product { Id = 2 }).ListAsync());
    }
}

/// <summary>Roles, and the cardinality the verbs carry rather than the schema.</summary>
public class RoleTests
{
    private sealed class Product
    {
        public int Id { get; set; }
    }

    [Fact]
    public async Task Set_replaces_the_file_already_holding_the_role()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        var old = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "old.png"));
        var new_ = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "new.png"));

        await harness.Media.For(product).SetAsync("primary", old.Id);
        await harness.Media.For(product).SetAsync("primary", new_.Id);

        var primary = await harness.Media.For(product).GetByRoleAsync("primary");
        Assert.Equal(new_.Id, primary?.Id);
    }

    [Fact]
    public async Task Set_only_detaches_the_file_it_displaces()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        var old = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "old.png"));
        var new_ = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "new.png"));

        await harness.Media.For(product).SetAsync("primary", old.Id);
        await harness.Media.For(product).SetAsync("primary", new_.Id);

        // The displaced file is still in the library, bytes and all — it may well be in use
        // elsewhere, and "stop being the hero image" is not "be deleted".
        Assert.NotNull(await harness.Media.FindAsync(old.Id));
        Assert.Equal(2, harness.Storage.Paths.Count);
    }

    [Fact]
    public async Task Set_leaves_the_link_alone_when_it_already_holds_the_role()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(product).SetAsync("primary", file.Id);
        var before = Assert.Single(harness.Store.Attachments).Id;

        harness.Clock.Advance(TimeSpan.FromHours(1));
        await harness.Media.For(product).SetAsync("primary", file.Id);

        // The same row, not a removed-and-rewritten one: re-saving the hero image should not move
        // when it was attached.
        Assert.Equal(before, Assert.Single(harness.Store.Attachments).Id);
    }

    [Fact]
    public async Task GetByRole_is_null_when_nothing_holds_it()
    {
        var harness = Harness.Create();

        Assert.Null(await harness.Media.For(new Product { Id = 1 }).GetByRoleAsync("primary"));
    }

    [Fact]
    public async Task GetByRole_refuses_a_role_several_files_hold()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await harness.Media.For(product).UploadAsync(
            TestMedia.Upload(TestMedia.Png(), "a.png", role: "gallery"));
        await harness.Media.For(product).UploadAsync(
            TestMedia.Upload(TestMedia.Png(32), "b.png", role: "gallery"));

        var error = await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(product).GetByRoleAsync("gallery"));

        Assert.Contains("not a single-valued role", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Roles_are_lower_cased_so_Primary_and_primary_are_one_role()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(product).SetAsync("Primary", file.Id);

        Assert.Equal("primary", Assert.Single(harness.Store.Attachments).Role);
        Assert.NotNull(await harness.Media.For(product).GetByRoleAsync("PRIMARY"));
    }

    [Fact]
    public async Task ListByRole_returns_only_that_role()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await harness.Media.For(product).UploadAsync(
            TestMedia.Upload(TestMedia.Png(), "hero.png", role: "primary"));
        await harness.Media.For(product).UploadAsync(
            TestMedia.Upload(TestMedia.Png(32), "shot.png", role: "gallery"));

        var gallery = await harness.Media.For(product).ListByRoleAsync("gallery");

        Assert.Equal("shot.png", Assert.Single(gallery).FileName);
    }

    [Fact]
    public async Task ListWithRoles_pairs_each_file_with_what_it_is_used_for()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        var hero = await harness.Media.For(product).UploadAsync(
            TestMedia.Upload(TestMedia.Png(), "hero.png", role: "primary"));
        var one  = await harness.Media.For(product).UploadAsync(
            TestMedia.Upload(TestMedia.Png(32), "one.png", role: "gallery"));
        var two  = await harness.Media.For(product).UploadAsync(
            TestMedia.Upload(TestMedia.Png(48), "two.png", role: "gallery"));

        var attached = await harness.Media.For(product).ListWithRolesAsync();

        // Role order first, then position within the role.
        Assert.Equal(
            [(one.Id, "gallery", 0), (two.Id, "gallery", 1), (hero.Id, "primary", 0)],
            attached.Select(a => (a.File.Id, a.Role, a.SortOrder)));
    }

    [Fact]
    public async Task ListWithRoles_lists_a_file_once_per_role_it_holds()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(product).SetAsync("primary", file.Id);
        await harness.Media.For(product).SetAsync("seo", file.Id);

        // The question is "what does this entity use, and for what", so one file under two roles is
        // two answers — unlike ListAsync, which asks which files it uses and returns each once.
        var attached = await harness.Media.For(product).ListWithRolesAsync();

        Assert.Equal(["primary", "seo"], attached.Select(a => a.Role));
        Assert.Single(await harness.Media.For(product).ListAsync());
    }

    [Fact]
    public async Task ListWithRoles_skips_a_link_whose_file_was_soft_deleted()
    {
        var harness = Harness.Create(o => o.SoftDelete = true);
        var product = new Product { Id = 1 };

        var kept    = await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(), "kept.png"));
        var deleted = await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(32), "gone.png"));

        await harness.Media.DeleteAsync(deleted.Id);

        var attached = await harness.Media.For(product).ListWithRolesAsync();

        Assert.Equal(kept.Id, Assert.Single(attached).File.Id);
    }

    [Fact]
    public async Task A_role_longer_than_the_limit_is_refused()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Media.For(new Product { Id = 1 })
                .AddAsync(file.Id, new string('x', MediaRoles.MaxRoleLength + 1)));
    }
}

/// <summary>Ordering within a role — the gallery order a product page shows.</summary>
public class AttachmentOrderTests
{
    private sealed class Product
    {
        public int Id { get; set; }
    }

    private static async Task<Guid[]> GalleryOf(Harness harness, Product product, params string[] names)
    {
        var ids = new List<Guid>();

        foreach (var name in names)
        {
            var file = await harness.Media.For(product).UploadAsync(
                TestMedia.Upload(TestMedia.Png(8 + ids.Count), name, role: "gallery"));
            ids.Add(file.Id);
        }

        return [.. ids];
    }

    [Fact]
    public async Task Add_appends_and_the_listing_keeps_that_order()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await GalleryOf(harness, product, "a.png", "b.png", "c.png");

        var gallery = await harness.Media.For(product).ListByRoleAsync("gallery");

        Assert.Equal(["a.png", "b.png", "c.png"], gallery.Select(f => f.FileName));
        Assert.Equal([0, 1, 2], harness.Store.Attachments.OrderBy(a => a.SortOrder).Select(a => a.SortOrder));
    }

    [Fact]
    public async Task Reorder_renumbers_the_role()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        var ids = await GalleryOf(harness, product, "a.png", "b.png", "c.png");

        await harness.Media.For(product).ReorderAsync("gallery", [ids[2], ids[0], ids[1]]);

        var gallery = await harness.Media.For(product).ListByRoleAsync("gallery");
        Assert.Equal(["c.png", "a.png", "b.png"], gallery.Select(f => f.FileName));
    }

    [Fact]
    public async Task Reorder_refuses_a_partial_order()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        var ids = await GalleryOf(harness, product, "a.png", "b.png", "c.png");

        var error = await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(product).ReorderAsync("gallery", [ids[0], ids[1]]));

        Assert.Contains("no meaning", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reorder_refuses_a_file_that_is_not_in_the_role()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        var ids     = await GalleryOf(harness, product, "a.png", "b.png");
        var outside = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(48), "z.png"));

        await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(product).ReorderAsync("gallery", [ids[0], outside.Id]));
    }

    [Fact]
    public async Task Detaching_from_the_middle_does_not_collide_with_the_next_append()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        var ids = await GalleryOf(harness, product, "a.png", "b.png", "c.png");

        await harness.Media.For(product).DetachAsync(ids[1], "gallery");
        await GalleryOf(harness, product, "d.png");

        // The next position comes from the rows, not from a count — which is why removing the
        // middle of a gallery does not hand the newcomer an index that is already taken.
        var positions = harness.Store.Attachments.Select(a => a.SortOrder).Order().ToArray();
        Assert.Equal(positions.Distinct(), positions);

        var gallery = await harness.Media.For(product).ListByRoleAsync("gallery");
        Assert.Equal(["a.png", "c.png", "d.png"], gallery.Select(f => f.FileName));
    }
}

/// <summary>
/// Detaching versus deleting — the distinction the whole design turns on, and the one a later
/// refactor is most likely to blur.
/// </summary>
public class DetachAndDeleteTests
{
    private sealed class Product
    {
        public int Id { get; set; }
    }

    [Fact]
    public async Task Detach_removes_the_link_and_leaves_the_file_and_its_bytes()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        var file = await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.True(await harness.Media.For(product).DetachAsync(file.Id));

        Assert.Empty(harness.Store.Attachments);
        Assert.NotNull(await harness.Media.FindAsync(file.Id));
        Assert.Single(harness.Storage.Paths);
    }

    [Fact]
    public async Task Detach_from_one_entity_leaves_the_other_entity_alone()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(new Product { Id = 1 }).AddAsync(file.Id);
        await harness.Media.For(new Product { Id = 2 }).AddAsync(file.Id);

        await harness.Media.For(new Product { Id = 1 }).DetachAsync(file.Id);

        Assert.Empty(await harness.Media.For(new Product { Id = 1 }).ListAsync());
        Assert.Single(await harness.Media.For(new Product { Id = 2 }).ListAsync());
    }

    [Fact]
    public async Task Detach_can_take_one_role_or_all_of_them()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(product).SetAsync("primary", file.Id);
        await harness.Media.For(product).SetAsync("seo", file.Id);

        await harness.Media.For(product).DetachAsync(file.Id, "seo");
        Assert.Equal("primary", Assert.Single(harness.Store.Attachments).Role);

        await harness.Media.For(product).DetachAsync(file.Id);
        Assert.Empty(harness.Store.Attachments);
    }

    [Fact]
    public async Task Detach_reports_false_when_there_was_no_link()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.False(await harness.Media.For(new Product { Id = 1 }).DetachAsync(file.Id));
    }

    [Fact]
    public async Task DetachAll_clears_the_links_and_deletes_nothing()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png"));

        Assert.Equal(2, await harness.Media.For(product).DetachAllAsync());

        Assert.Empty(harness.Store.Attachments);
        Assert.Equal(2, harness.Store.Count);
        Assert.Equal(2, harness.Storage.Paths.Count);
    }

    [Fact]
    public async Task Deleting_a_file_removes_it_from_every_entity_using_it()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(new Product { Id = 1 }).AddAsync(file.Id);
        await harness.Media.For(new Product { Id = 2 }).AddAsync(file.Id);

        Assert.True(await harness.Media.DeleteAsync(file.Id));

        Assert.Empty(harness.Store.Attachments);
        Assert.Empty(await harness.Media.For(new Product { Id = 1 }).ListAsync());
        Assert.Empty(await harness.Media.For(new Product { Id = 2 }).ListAsync());
        Assert.Empty(harness.Storage.Paths);
    }

    [Fact]
    public async Task Soft_delete_still_removes_the_links()
    {
        var harness = Harness.Create(o => o.SoftDelete = true);
        var product = new Product { Id = 1 };

        var file = await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.DeleteAsync(file.Id);

        // The row survives as a record that the file existed; the link does not, because a
        // soft-deleted file that still read as attached to a live product would be a lie.
        Assert.Equal(1, harness.Store.Count);
        Assert.Empty(harness.Store.Attachments);
    }

    [Fact]
    public async Task DeleteAll_deletes_the_files_this_entity_alone_was_using()
    {
        var harness = Harness.Create();

        await harness.Media.For(new Product { Id = 1 }).UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        await harness.Media.For(new Product { Id = 1 }).UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png"));
        await harness.Media.For(new Product { Id = 2 }).UploadAsync(TestMedia.Upload(TestMedia.Png(48), "c.png"));

        var deleted = await harness.Media.For(new Product { Id = 1 }).DeleteAllAsync();

        Assert.Equal(2, deleted);
        Assert.Single(harness.Storage.Paths);

        var left = await harness.Media.ListAsync();
        Assert.Equal("c.png", Assert.Single(left.Items).FileName);
    }

    /// <summary>
    /// The rule that makes deleting a product safe: an image two products share survives, merely
    /// unlinked from the one that went.
    /// </summary>
    [Fact]
    public async Task DeleteAll_only_detaches_a_file_another_entity_still_uses()
    {
        var harness = Harness.Create();

        var shared = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "shared.png"));
        var mine   = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "mine.png"));

        await harness.Media.For(new Product { Id = 1 }).AddAsync(shared.Id);
        await harness.Media.For(new Product { Id = 1 }).AddAsync(mine.Id);
        await harness.Media.For(new Product { Id = 2 }).AddAsync(shared.Id);

        var deleted = await harness.Media.For(new Product { Id = 1 }).DeleteAllAsync();

        // Only the file nobody else held is counted, because only it was destroyed.
        Assert.Equal(1, deleted);

        Assert.Null(await harness.Media.FindAsync(mine.Id));
        Assert.NotNull(await harness.Media.FindAsync(shared.Id));

        // Product 2 still has it, and product 1 has nothing.
        Assert.Equal(shared.Id, Assert.Single(await harness.Media.For(new Product { Id = 2 }).ListAsync()).Id);
        Assert.Empty(await harness.Media.For(new Product { Id = 1 }).ListAsync());
    }

    [Fact]
    public async Task DeleteAll_counts_a_file_held_under_two_roles_once()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(product).SetAsync("primary", file.Id);
        await harness.Media.For(product).SetAsync("seo", file.Id);

        // One file deleted, not two, though it was attached twice.
        Assert.Equal(1, await harness.Media.For(product).DeleteAllAsync());

        Assert.Null(await harness.Media.FindAsync(file.Id));
        Assert.Empty(harness.Store.Attachments);
        Assert.Empty(harness.Storage.Paths);
    }

    [Fact]
    public async Task DeleteAll_on_an_entity_with_nothing_attached_does_nothing()
    {
        var harness = Harness.Create();
        await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.Equal(0, await harness.Media.For(new Product { Id = 9 }).DeleteAllAsync());
        Assert.Equal(1, harness.Store.Count);
    }
}

/// <summary>Querying across the join, and the filters that only make sense with an owner.</summary>
public class AttachmentQueryTests
{
    private sealed class Product
    {
        public int Id { get; set; }
    }

    [Fact]
    public async Task Unattached_files_stay_in_the_library_listing()
    {
        var harness = Harness.Create();

        await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "loose.png"));
        await harness.Media.For(new Product { Id = 1 })
            .UploadAsync(TestMedia.Upload(TestMedia.Png(32), "used.png"));

        var all = await harness.Media.ListAsync();

        // An unattached file is an ordinary file waiting to be attached, not a file in limbo.
        Assert.Equal(2, all.Total);
        Assert.Contains("loose.png", all.Items.Select(f => f.FileName));
    }

    [Fact]
    public async Task A_file_attached_many_times_appears_once_in_the_library_listing()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(new Product { Id = 1 }).AddAsync(file.Id);
        await harness.Media.For(new Product { Id = 2 }).AddAsync(file.Id);
        await harness.Media.For(new Product { Id = 3 }).AddAsync(file.Id);

        var all = await harness.Media.ListAsync();

        Assert.Equal(1, all.Total);
        Assert.Single(all.Items);
    }

    [Fact]
    public async Task Unattached_finds_the_files_nothing_is_using()
    {
        var harness = Harness.Create();

        var loose = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "loose.png"));
        await harness.Media.For(new Product { Id = 1 })
            .UploadAsync(TestMedia.Upload(TestMedia.Png(32), "used.png"));

        var found = await harness.Media.ListAsync(new MediaQuery { Unattached = true });

        Assert.Equal(loose.Id, Assert.Single(found.Items).Id);
    }

    [Fact]
    public async Task A_detached_file_becomes_unattached_again()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        var file = await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await harness.Media.For(product).DetachAsync(file.Id);

        var found = await harness.Media.ListAsync(new MediaQuery { Unattached = true });

        Assert.Equal(file.Id, Assert.Single(found.Items).Id);
    }

    [Fact]
    public async Task Query_by_role_returns_that_entitys_files_in_it()
    {
        var harness = Harness.Create();

        await harness.Media.For(new Product { Id = 1 })
            .UploadAsync(TestMedia.Upload(TestMedia.Png(), "hero.png", role: "primary"));
        await harness.Media.For(new Product { Id = 1 })
            .UploadAsync(TestMedia.Upload(TestMedia.Png(32), "shot.png", role: "gallery"));
        await harness.Media.For(new Product { Id = 2 })
            .UploadAsync(TestMedia.Upload(TestMedia.Png(48), "other.png", role: "primary"));

        var page = await harness.Media.ListAsync(new MediaQuery
        {
            OwnerEntityName = "Product",
            OwnerEntityId   = "1",
            Role            = "primary"
        });

        Assert.Equal("hero.png", Assert.Single(page.Items).FileName);
    }

    [Fact]
    public async Task Query_refuses_a_role_without_an_owner()
    {
        var harness = Harness.Create();

        await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.ListAsync(new MediaQuery { Role = "gallery" }));
    }

    [Fact]
    public async Task Query_refuses_the_entitys_own_order_without_an_owner()
    {
        var harness = Harness.Create();

        await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.ListAsync(new MediaQuery { SortBy = MediaSortBy.SortOrder }));
    }

    [Fact]
    public async Task Query_refuses_unattached_together_with_an_owner()
    {
        var harness = Harness.Create();

        await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.ListAsync(new MediaQuery
            {
                OwnerEntityName = "Product",
                OwnerEntityId   = "1",
                Unattached      = true
            }));
    }
}

/// <summary>
/// What an entity's own listings cost, and what they do when the answer will not fit in one.
/// </summary>
public class AccessorListingTests
{
    private sealed class Product
    {
        public int Id { get; set; }
    }

    private static async Task AttachAsync(Harness harness, Product product, int count, string? role = null)
    {
        for (var i = 0; i < count; i++)
            await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(), $"{i}.png", role: role));
    }

    /// <summary>
    /// The accessor discards the total, so running the aggregate that produces it doubles the cost
    /// of drawing a product's gallery for a number nobody reads.
    /// </summary>
    [Fact]
    public async Task Listing_an_entitys_files_costs_one_query_and_no_count()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await AttachAsync(harness, product, 3);
        harness.Store.ResetCounters();

        var files = await harness.Media.For(product).ListAsync();

        Assert.Equal(3, files.Count);
        Assert.Equal(1, harness.Store.Queries);
        Assert.Equal(0, harness.Store.Counts);
    }

    [Fact]
    public async Task Listing_a_role_and_a_category_cost_no_count_either()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await harness.Media.For(product).UploadAsync(
            TestMedia.Upload(TestMedia.Png(), category: "photos", role: "gallery"));

        harness.Store.ResetCounters();
        await harness.Media.For(product).ListByRoleAsync("gallery");
        await harness.Media.For(product).ListByCategoryAsync("photos");

        Assert.Equal(2, harness.Store.Queries);
        Assert.Equal(0, harness.Store.Counts);
    }

    /// <summary>
    /// The public listing still owes its caller a total, so it still pays for one. Removing the
    /// count from the accessor must not have removed it from here.
    /// </summary>
    [Fact]
    public async Task The_public_listing_still_reports_a_total()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await AttachAsync(harness, product, 3);
        harness.Store.ResetCounters();

        var page = await harness.Media.ListAsync(new MediaQuery { Take = 2 });

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(3, page.Total);
        Assert.Equal(1, harness.Store.Counts);
    }

    /// <summary>
    /// A full page and a truncated one look identical from the accessor, so it asks. One extra
    /// query, and only for an entity sitting exactly on the boundary.
    /// </summary>
    [Fact]
    public async Task An_entity_holding_exactly_one_page_is_answered_in_full()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await AttachAsync(harness, product, MediaQuery.MaxTake);
        harness.Store.ResetCounters();

        var files = await harness.Media.For(product).ListAsync();

        Assert.Equal(MediaQuery.MaxTake, files.Count);

        // The probe for a page beyond this one — and nothing more, since there was none.
        Assert.Equal(2, harness.Store.Queries);
        Assert.Equal(0, harness.Store.Counts);
    }

    /// <summary>
    /// The method promises every file attached to the entity. One query cannot return more than a
    /// page of them, and handing back the first page would be indistinguishable from a complete
    /// answer — a gallery quietly missing its tail.
    /// </summary>
    [Fact]
    public async Task An_entity_holding_more_than_one_page_is_refused_rather_than_truncated()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await AttachAsync(harness, product, MediaQuery.MaxTake + 1);

        var refused = await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(product).ListAsync());

        // The caller is left somewhere to go, rather than simply told no.
        Assert.Contains("ListAsync", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Skip", refused.Message, StringComparison.Ordinal);

        // And the paged listing it names does answer the question.
        var page = await harness.Media.ListAsync(new MediaQuery
        {
            OwnerEntityName = "Product",
            OwnerEntityId   = "1",
            Skip            = MediaQuery.MaxTake,
            Take            = 10
        });

        Assert.Equal(MediaQuery.MaxTake + 1, page.Total);
        Assert.Single(page.Items);
    }

    /// <summary>
    /// The role listing caps the same way and has to refuse the same way — it is the gallery that
    /// grows, not usually the entity as a whole.
    /// </summary>
    [Fact]
    public async Task A_role_holding_more_than_one_page_is_refused_too()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await AttachAsync(harness, product, MediaQuery.MaxTake + 1, role: "gallery");

        var refused = await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(product).ListByRoleAsync("gallery"));

        Assert.Contains("gallery", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ListWithRolesAsync reads the links whole and the files a page at a time, so a silently
    /// capped file listing would drop live links as though their files had been deleted.
    /// </summary>
    [Fact]
    public async Task Listing_with_roles_refuses_rather_than_reporting_live_files_as_gone()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await AttachAsync(harness, product, MediaQuery.MaxTake + 1);

        await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(product).ListWithRolesAsync());
    }
}
