using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Attributes;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.TestSupport;
using System.Globalization;

namespace DotNetBoost.Media.UnitTests;

/// <summary>
/// <c>ForMany</c>: the media attached to a page of entities, read once rather than once per row.
/// <para>
/// The cost is the reason it exists, so most of these tests assert on the store's own counter
/// rather than on the result. A method that returns the right answer in fifty round trips has not
/// done what this one promises.
/// </para>
/// </summary>
public class BulkOwnerReadTests
{
    // Its own stored name: another test file already declares roles for "Product", and one entity
    // name may only be declared once.
    [MediaEntity("BulkProduct")]
    [MediaRole("primary")]
    [MediaRole("gallery", Multiple = true)]
    private sealed class Product
    {
        public int Id { get; set; }
    }

    /// <summary>A second entity type, to prove a bulk read cannot reach past its own name.</summary>
    [MediaEntity("BulkCustomer")]
    private sealed class Customer
    {
        public int Id { get; set; }
    }

    private static Harness Declared() => Harness.Create(roles: Registry.Of<Product>());

    [Fact]
    public async Task ForMany_over_fifty_owners_issues_one_store_call()
    {
        var harness  = Harness.Create();
        var products = Enumerable.Range(1, 50).Select(i => new Product { Id = i }).ToArray();

        foreach (var product in products)
            await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(), $"{product.Id}.png"));

        harness.Store.ResetCounters();

        var byOwner = await harness.Media.ForMany(products).ListWithRolesAsync();

        // One read for fifty owners, and no file query beside it: the store joins the files in.
        // Through For(product) this page would have cost a hundred.
        Assert.Equal(1, harness.Store.BulkOwnerReads);
        Assert.Equal(0, harness.Store.Queries);
        Assert.Equal(50, byOwner.Count);
        Assert.All(byOwner.Values, files => Assert.Single(files));
    }

    [Fact]
    public async Task Each_bulk_method_is_one_store_call()
    {
        var harness  = Harness.Create();
        var products = Enumerable.Range(1, 5).Select(i => new Product { Id = i }).ToArray();

        foreach (var product in products)
            await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(), $"{product.Id}.png"));

        var bulk = harness.Media.ForMany(products);

        harness.Store.ResetCounters();
        await bulk.ListWithRolesAsync();
        Assert.Equal(1, harness.Store.BulkOwnerReads);

        harness.Store.ResetCounters();
        await bulk.GetByRoleAsync(MediaRoles.Default);
        Assert.Equal(1, harness.Store.BulkOwnerReads);

        harness.Store.ResetCounters();
        await bulk.WhichHaveAttachmentsAsync();
        Assert.Equal(1, harness.Store.BulkOwnerReads);
    }

    /// <summary>
    /// The one promise that keeps a caller from writing <c>GetValueOrDefault</c> everywhere: an
    /// owner with no images is in the dictionary with an empty list.
    /// </summary>
    [Fact]
    public async Task An_owner_with_nothing_attached_is_present_with_an_empty_list()
    {
        var harness = Harness.Create();
        var has     = new Product { Id = 1 };
        var hasNot  = new Product { Id = 2 };

        await harness.Media.For(has).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        var byOwner = await harness.Media.ForMany([has, hasNot]).ListWithRolesAsync();

        Assert.Equal(2, byOwner.Count);
        Assert.Single(byOwner["1"]);
        Assert.Empty(byOwner["2"]);
    }

    [Fact]
    public async Task An_owner_with_nothing_in_the_role_is_present_as_null()
    {
        var harness = Harness.Create();
        var has     = new Product { Id = 1 };
        var hasNot  = new Product { Id = 2 };

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await harness.Media.For(has).SetAsync("primary", file.Id);

        var heroes = await harness.Media.ForMany([has, hasNot]).GetByRoleAsync("primary");

        Assert.Equal(file.Id, heroes["1"]!.Id);
        Assert.Null(heroes["2"]);
    }

    /// <summary>
    /// The agreement that makes the bulk read a drop-in: for each owner it returns exactly what
    /// the single-owner accessor returns for that owner, in the same order.
    /// </summary>
    [Fact]
    public async Task ForMany_and_For_agree_on_ordering_and_on_what_a_row_is()
    {
        var harness = Declared();
        var one     = new Product { Id = 1 };
        var two     = new Product { Id = 2 };

        var hero   = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "hero.png"));
        var first  = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "first.png"));
        var second = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(48), "second.png"));

        await harness.Media.For(one).SetAsync("primary", hero.Id);
        await harness.Media.For(one).AddAsync(first.Id, "gallery");
        await harness.Media.For(one).AddAsync(second.Id, "gallery");
        await harness.Media.For(two).SetAsync("primary", second.Id);

        var bulk = await harness.Media.ForMany([one, two]).ListWithRolesAsync();

        foreach (var product in new[] { one, two })
        {
            var single = await harness.Media.For(product).ListWithRolesAsync();
            var key    = product.Id.ToString(CultureInfo.InvariantCulture);

            Assert.Equal(
                single.Select(a => (a.File.Id, a.Role, a.SortOrder)),
                bulk[key].Select(a => (a.File.Id, a.Role, a.SortOrder)));
        }
    }

    [Fact]
    public async Task ForMany_lists_a_file_once_per_role_as_the_single_owner_read_does()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(product).SetAsync("primary", file.Id);
        await harness.Media.For(product).SetAsync("seo", file.Id);

        // Per link, not per file — the same distinction ListWithRolesAsync draws for one entity.
        var rows = (await harness.Media.ForMany([product]).ListWithRolesAsync())["1"];

        Assert.Equal(["primary", "seo"], rows.Select(r => r.Role));
        Assert.All(rows, r => Assert.Equal(file.Id, r.File.Id));
    }

    [Fact]
    public async Task WhichHaveAttachments_names_only_the_owners_that_do()
    {
        var harness = Harness.Create();
        var has     = new Product { Id = 1 };
        var hasNot  = new Product { Id = 2 };

        await harness.Media.For(has).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        var found = await harness.Media.ForMany([has, hasNot]).WhichHaveAttachmentsAsync();

        Assert.Equal(["1"], found);
    }

    [Fact]
    public async Task WhichHaveAttachments_can_ask_about_one_role()
    {
        var harness = Declared();
        var hero    = new Product { Id = 1 };
        var gallery = new Product { Id = 2 };

        var one = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        var two = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png"));

        await harness.Media.For(hero).SetAsync("primary", one.Id);
        await harness.Media.For(gallery).AddAsync(two.Id, "gallery");

        Assert.Equal(["1"], await harness.Media.ForMany([hero, gallery]).WhichHaveAttachmentsAsync("primary"));
        Assert.Equal(["2"], await harness.Media.ForMany([hero, gallery]).WhichHaveAttachmentsAsync("gallery"));
    }

    [Fact]
    public async Task A_bulk_read_cannot_reach_another_entity_type()
    {
        var harness  = Harness.Create();
        var product  = new Product { Id = 1 };
        var customer = new Customer { Id = 1 };

        // Same key, different type — exactly the collision an owner name exists to prevent.
        await harness.Media.For(customer).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        var byOwner = await harness.Media.ForMany([product]).ListWithRolesAsync();

        Assert.Empty(byOwner["1"]);
    }

    [Fact]
    public async Task ForMany_refuses_a_mix_of_entity_types()
    {
        var harness = Harness.Create();

        // One read is one statement over one owner name. Reading both under the first name would
        // not fail, it would just answer about the wrong rows.
        var error = Assert.Throws<InvalidOperationException>(
            () => harness.Media.ForMany<object>([new Product { Id = 1 }, new Customer { Id = 2 }]));

        Assert.Contains("BulkProduct", error.Message, StringComparison.Ordinal);
        Assert.Contains("BulkCustomer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForMany_refuses_an_entity_whose_key_is_unset()
    {
        var harness = Harness.Create();

        var error = Assert.Throws<InvalidOperationException>(
            () => harness.Media.ForMany([new Product { Id = 0 }, new Product { Id = 1 }]));

        Assert.Contains("has no key yet", error.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ForMany_reads_a_repeated_id_once()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        var bulk = harness.Media.ForMany([product, product, product]);

        // Deduplicated, so a page that happens to hold one id three times does not get three
        // entries or three copies of the answer.
        Assert.Equal(["1"], bulk.EntityIds);
        Assert.Single(await bulk.ListWithRolesAsync());
    }

    [Fact]
    public async Task ForMany_over_no_owners_answers_without_touching_the_store()
    {
        var harness = Harness.Create();
        await harness.Media.For(new Product { Id = 1 }).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        harness.Store.ResetCounters();

        var bulk = harness.Media.ForMany(Array.Empty<Product>());

        Assert.Empty(await bulk.ListWithRolesAsync());
        Assert.Empty(await bulk.GetByRoleAsync("primary"));
        Assert.Empty(await bulk.WhichHaveAttachmentsAsync());

        // A round trip whose answer is already known is still a round trip.
        Assert.Equal(0, harness.Store.BulkOwnerReads);
    }

    [Fact]
    public async Task ForMany_takes_keys_as_well_as_entities()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 7 };

        var file = await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        // Keys read off a route rather than entities loaded from the database, formatted the same
        // way For(entity) formats them.
        var byOwner = await harness.Media.ForMany("BulkProduct", [7, 8]).ListWithRolesAsync();

        Assert.Equal(file.Id, Assert.Single(byOwner["7"]).File.Id);
        Assert.Empty(byOwner["8"]);
    }

    [Fact]
    public async Task ForMany_formats_a_guid_key_the_way_the_rows_hold_it()
    {
        var harness = Harness.Create();
        var id      = Guid.NewGuid();

        var file = await harness.Media.For("Order", id.ToString("D", CultureInfo.InvariantCulture))
            .UploadAsync(TestMedia.Upload(TestMedia.Png()));

        var byOwner = await harness.Media.ForMany("Order", [id]).ListWithRolesAsync();

        Assert.Equal(file.Id, Assert.Single(byOwner[id.ToString("D", CultureInfo.InvariantCulture)]).File.Id);
    }

    [Fact]
    public async Task GetByRole_refuses_a_role_the_entity_declared_as_multi_valued()
    {
        var harness = Declared();

        var error = await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.ForMany([new Product { Id = 1 }]).GetByRoleAsync("gallery"));

        // The same refusal the single-owner read makes, for the same reason: "the one file holding
        // a many-valued role" is not a question with an answer.
        Assert.Contains("multi-valued", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetByRole_refuses_a_role_several_files_actually_hold()
    {
        // Undeclared, so nothing stopped two files being appended to one role — and then there is
        // no single file to hand back. Refused rather than answered with the first, which is how a
        // product quietly loses the hero image somebody set.
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        var one = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        var two = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png"));

        await harness.Media.For(product).AddAsync(one.Id, "primary");
        await harness.Media.For(product).AddAsync(two.Id, "primary");

        var error = await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.ForMany([product]).GetByRoleAsync("primary"));

        Assert.Contains("not a single-valued role", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_role_the_entity_never_declared_is_refused_here_too()
    {
        var harness = Declared();

        var error = await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.ForMany([new Product { Id = 1 }]).GetByRoleAsync("galery"));

        Assert.Contains("has no media role 'galery'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bulk_read_reports_the_owners_it_was_asked_about()
    {
        var harness = Harness.Create();
        var bulk    = harness.Media.ForMany([new Product { Id = 2 }, new Product { Id = 1 }]);

        // In the order given, which is also the order a caller's own page is in.
        Assert.Equal("BulkProduct", bulk.EntityName);
        Assert.Equal(["2", "1"], bulk.EntityIds);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_link_to_a_soft_deleted_file_is_not_returned()
    {
        var harness = Harness.Create(o => o.SoftDelete = true);
        var product = new Product { Id = 1 };

        var file = await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await harness.Media.DeleteAsync(file.Id);

        // The delete removed the link as well, so there is nothing to pair — but the read also
        // refuses to pair a tombstone if a link to one ever survives.
        Assert.Empty((await harness.Media.ForMany([product]).ListWithRolesAsync())["1"]);
    }
}
