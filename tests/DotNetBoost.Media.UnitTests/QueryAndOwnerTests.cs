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

        var page = await harness.Media.ListAsync(new MediaQuery { OldestFirst = true });

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

        var page = await harness.Media.ListAsync(new MediaQuery { Skip = 2, Take = 2, OldestFirst = true });

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

public class OwnerTests
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

        Assert.Equal("Product", file.OwnerEntityName);
        Assert.Equal("42", file.OwnerEntityId);
    }

    [Fact]
    public async Task For_uses_the_MediaEntity_name_when_there_is_one()
    {
        var harness  = Harness.Create();
        var customer = new CustomerEntity { Id = Guid.NewGuid() };

        var file = await harness.Media.For(customer).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.Equal("Customer", file.OwnerEntityName);
        Assert.Equal(customer.Id.ToString("D"), file.OwnerEntityId);
    }

    [Fact]
    public async Task For_overrides_an_owner_set_on_the_upload()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 42 };

        var file = await harness.Media.For(product).UploadAsync(
            TestMedia.Upload(TestMedia.Png()) with { Owner = new MediaOwner("SomethingElse", "999") });

        Assert.Equal("Product", file.OwnerEntityName);
        Assert.Equal("42", file.OwnerEntityId);
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

        var files = await harness.Media.For(product).ListAsync("gallery");

        Assert.Equal("a.png", Assert.Single(files).FileName);
    }

    [Fact]
    public async Task Attach_adopts_a_file_uploaded_before_the_entity_existed()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.Null(file.OwnerEntityName);

        var attached = await harness.Media.For(new Product { Id = 7 }).AttachAsync(file.Id);

        Assert.Equal("Product", attached.OwnerEntityName);
        Assert.Equal("7", attached.OwnerEntityId);
    }

    [Fact]
    public async Task DeleteAll_removes_that_entitys_files_and_leaves_the_others()
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

    [Fact]
    public async Task Update_can_detach_a_file()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.For(new Product { Id = 1 }).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        var detached = await harness.Media.UpdateAsync(file.Id, new MediaUpdate { ClearOwner = true });

        Assert.Null(detached.OwnerEntityName);
        Assert.Null(detached.OwnerEntityId);
    }
}
