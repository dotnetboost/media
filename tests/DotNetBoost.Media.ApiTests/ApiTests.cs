using DotNetBoost.Media.API;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.TestSupport;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text;
using System.Net.Http.Json;

namespace DotNetBoost.Media.ApiTests;

public class UploadEndpointTests
{
    [Fact]
    public async Task Post_stores_the_file_and_returns_it_with_a_location()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.PostAsync(
            new Uri("/api/media", UriKind.Relative),
            TestApp.Multipart(TestMedia.Png(256), "holiday.png", category: "photos", tags: "beach,2026"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var file = await response.Content.ReadFromJsonAsync<MediaFileResponse>();

        Assert.NotNull(file);
        Assert.Equal("holiday.png", file.FileName);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(256, file.Size);
        Assert.Equal("photos", file.Category);
        Assert.Equal(["2026", "beach"], file.Tags);
        Assert.Equal($"/api/media/{file.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task The_response_never_says_where_the_bytes_live()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.PostAsync(new Uri("/api/media", UriKind.Relative), TestApp.Multipart(TestMedia.Png()));
        var json     = await response.Content.ReadAsStringAsync();

        // The storage layout is not the caller's business, and publishing it hands every client a
        // map of the storage account.
        Assert.DoesNotContain("storagePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageContainer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageProvider", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_attaches_the_file_to_an_entity_when_the_form_says_so()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.PostAsync(
            new Uri("/api/media", UriKind.Relative),
            TestApp.Multipart(TestMedia.Png(), ownerEntityName: "Product", ownerEntityId: "42"));

        var file = await response.Content.ReadFromJsonAsync<MediaFileResponse>();

        // The upload response carries no links — the read does — so ask for the file itself.
        var read = await app.Client.GetFromJsonAsync<MediaFileResponse>(
            new Uri($"/api/media/{file!.Id}", UriKind.Relative));

        var attachment = Assert.Single(read!.Attachments!);
        Assert.Equal("Product", attachment.OwnerEntityName);
        Assert.Equal("42", attachment.OwnerEntityId);
        Assert.Equal(MediaRoles.Default, attachment.Role);
    }

    [Fact]
    public async Task Post_without_a_file_is_a_bad_request()
    {
        await using var app = await TestApp.StartAsync();

        // An empty multipart body. It is also malformed as far as the form reader is concerned,
        // which is the other half of what this asserts: a client's bad body is a 400, not a 500.
        var response = await app.Client.PostAsync(
            new Uri("/api/media", UriKind.Relative),
            new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_with_a_form_that_has_no_file_field_is_a_bad_request()
    {
        await using var app = await TestApp.StartAsync();

        var form = new MultipartFormDataContent { { new StringContent("photos"), "category" } };

        var response = await app.Client.PostAsync(new Uri("/api/media", UriKind.Relative), form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_with_a_json_body_is_refused()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.PostAsJsonAsync(new Uri("/api/media", UriKind.Relative), new { file = "nope" });

        // The endpoint declares multipart/form-data, so ASP.NET Core refuses a JSON body before
        // the handler runs — 415, which is the right answer for the wrong content type.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task An_oversized_upload_is_a_413_with_the_limit_in_it()
    {
        await using var app = await TestApp.StartAsync(m => m.WithMaxFileSize(100));

        var response = await app.Client.PostAsync(
            new Uri("/api/media", UriKind.Relative),
            TestApp.Multipart(TestMedia.Png(500)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Media file too large", problem!.Title);
        Assert.Equal(100, ((System.Text.Json.JsonElement)problem.Extensions["limit"]!).GetInt64());
    }

    [Fact]
    public async Task A_disallowed_type_is_a_415()
    {
        await using var app = await TestApp.StartAsync(m => m.AllowContentTypes("image/*"));

        var response = await app.Client.PostAsync(
            new Uri("/api/media", UriKind.Relative),
            TestApp.Multipart(TestMedia.Unrecognised(), "notes.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task An_executable_renamed_to_an_image_is_a_415_that_says_what_it_really_is()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.PostAsync(
            new Uri("/api/media", UriKind.Relative),
            TestApp.Multipart(TestMedia.Executable(), "photo.png", "image/png"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Media content does not match its type", problem!.Title);
        Assert.Contains("portable-executable", problem.Extensions["detectedContentType"]!.ToString(), StringComparison.Ordinal);
    }
}

public class ReadEndpointTests
{
    private static async Task<MediaFileResponse> UploadAsync(TestApp app, byte[] content, string fileName = "photo.png")
    {
        var response = await app.Client.PostAsync(new Uri("/api/media", UriKind.Relative), TestApp.Multipart(content, fileName));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MediaFileResponse>())!;
    }

    [Fact]
    public async Task Get_returns_the_files_metadata()
    {
        await using var app = await TestApp.StartAsync();
        var uploaded = await UploadAsync(app, TestMedia.Png());

        var file = await app.Client.GetFromJsonAsync<MediaFileResponse>(new Uri($"/api/media/{uploaded.Id}", UriKind.Relative));

        Assert.Equal(uploaded.Id, file!.Id);
    }

    [Fact]
    public async Task Get_for_an_unknown_id_is_a_404_problem()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.GetAsync(new Uri($"/api/media/{Guid.NewGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Media file not found", problem!.Title);
    }

    [Fact]
    public async Task Content_returns_the_bytes_with_the_right_type()
    {
        await using var app = await TestApp.StartAsync();
        var content  = TestMedia.Png(512);
        var uploaded = await UploadAsync(app, content);

        var response = await app.Client.GetAsync(new Uri($"/api/media/{uploaded.Id}/content", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());

        // The checksum is a hash of the content, so it is a valid strong ETag — which is what
        // makes conditional requests and range-based seeking work.
        Assert.Equal($"\"{uploaded.Checksum}\"", response.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Content_displays_inline_unless_a_download_is_asked_for()
    {
        await using var app = await TestApp.StartAsync();
        var uploaded = await UploadAsync(app, TestMedia.Png(), "holiday.png");

        var inline = await app.Client.GetAsync(new Uri($"/api/media/{uploaded.Id}/content", UriKind.Relative));
        Assert.Null(inline.Content.Headers.ContentDisposition);

        var attachment = await app.Client.GetAsync(new Uri($"/api/media/{uploaded.Id}/content?download=true", UriKind.Relative));
        Assert.Equal("attachment", attachment.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("holiday.png", attachment.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Content_serves_a_range()
    {
        await using var app = await TestApp.StartAsync();
        var content  = TestMedia.Png(1000);
        var uploaded = await UploadAsync(app, content);

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/media/{uploaded.Id}/content", UriKind.Relative));
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);

        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(content[10..20], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Content_for_a_row_whose_bytes_have_gone_is_a_404()
    {
        await using var app = await TestApp.StartAsync();
        var uploaded = await UploadAsync(app, TestMedia.Png());

        app.Storage.Corrupt(app.Store.All.Single().StoragePath);

        var response = await app.Client.GetAsync(new Uri($"/api/media/{uploaded.Id}/content", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Url_is_a_204_for_a_backend_with_no_direct_link()
    {
        await using var app = await TestApp.StartAsync();
        var uploaded = await UploadAsync(app, TestMedia.Png());

        var response = await app.Client.GetAsync(new Uri($"/api/media/{uploaded.Id}/url", UriKind.Relative));

        // 204, not 404: the file exists, this backend simply has no direct link for it. A 404
        // would send the client looking for the wrong problem.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Url_returns_the_link_when_the_backend_has_one()
    {
        await using var app = await TestApp.StartAsync();
        app.Storage.Url = new Uri("https://cdn.example.com/a%20b.png");

        var uploaded = await UploadAsync(app, TestMedia.Png());
        var response = await app.Client.GetFromJsonAsync<MediaUrlResponse>(new Uri($"/api/media/{uploaded.Id}/url", UriKind.Relative));

        // Escaped as it was minted: un-escaping would corrupt a SAS or pre-signed signature.
        Assert.Equal("https://cdn.example.com/a%20b.png", response!.Url);
    }

    [Fact]
    public async Task List_pages_and_filters()
    {
        await using var app = await TestApp.StartAsync();

        await app.Client.PostAsync(new Uri("/api/media", UriKind.Relative), TestApp.Multipart(TestMedia.Png(), "a.png", category: "photos"));
        await app.Client.PostAsync(new Uri("/api/media", UriKind.Relative), TestApp.Multipart(TestMedia.Png(32), "b.png", category: "docs"));

        var all = await app.Client.GetFromJsonAsync<MediaPageResponse>(new Uri("/api/media", UriKind.Relative));
        Assert.Equal(2, all!.Total);

        var photos = await app.Client.GetFromJsonAsync<MediaPageResponse>(new Uri("/api/media?category=photos", UriKind.Relative));
        Assert.Equal("a.png", Assert.Single(photos!.Items).FileName);
    }

    [Fact]
    public async Task List_caps_an_outrageous_page_size()
    {
        await using var app = await TestApp.StartAsync();

        var page = await app.Client.GetFromJsonAsync<MediaPageResponse>(new Uri("/api/media?take=100000", UriKind.Relative));

        Assert.Equal(MediaQuery.MaxTake, page!.Take);
    }
}

public class WriteEndpointTests
{
    [Fact]
    public async Task Patch_changes_only_what_it_names()
    {
        await using var app = await TestApp.StartAsync();

        var created = await app.Client.PostAsync(
            new Uri("/api/media", UriKind.Relative),
            TestApp.Multipart(TestMedia.Png(), "old.png", category: "photos"));
        var uploaded = (await created.Content.ReadFromJsonAsync<MediaFileResponse>())!;

        var response = await app.Client.PatchAsJsonAsync(
            new Uri($"/api/media/{uploaded.Id}", UriKind.Relative),
            new UpdateMediaRequest { FileName = "new.png", Tags = ["archived"] });

        var updated = await response.Content.ReadFromJsonAsync<MediaFileResponse>();

        Assert.Equal("new.png", updated!.FileName);
        Assert.Equal(["archived"], updated.Tags);
        Assert.Equal("photos", updated.Category);
        Assert.Equal(uploaded.Size, updated.Size);
    }

    [Fact]
    public async Task Post_attachments_attaches_an_already_uploaded_file()
    {
        await using var app = await TestApp.StartAsync();

        var uploaded = await UploadAsync(app);

        var attached = await (await app.Client.PostAsJsonAsync(
            new Uri($"/api/media/{uploaded.Id}/attachments", UriKind.Relative),
            new AttachMediaRequest { OwnerEntityName = "Product", OwnerEntityId = "7" }))
            .Content.ReadFromJsonAsync<MediaFileResponse>();

        var attachment = Assert.Single(attached!.Attachments!);
        Assert.Equal("Product", attachment.OwnerEntityName);
        Assert.Equal("7", attachment.OwnerEntityId);
    }

    /// <summary>
    /// The behaviour the single owner column could not express: the second attach used to move the
    /// file off the first product.
    /// </summary>
    [Fact]
    public async Task Post_attachments_can_attach_one_file_to_two_entities()
    {
        await using var app = await TestApp.StartAsync();

        var uploaded = await UploadAsync(app);

        await AttachAsync(app, uploaded.Id, "Product", "1");
        var second = await AttachAsync(app, uploaded.Id, "Product", "2");

        Assert.Equal(2, second.Attachments!.Count);

        foreach (var entityId in new[] { "1", "2" })
        {
            var page = await app.Client.GetFromJsonAsync<MediaPageResponse>(
                new Uri($"/api/media?ownerEntityName=Product&ownerEntityId={entityId}", UriKind.Relative));

            Assert.Equal(uploaded.Id, Assert.Single(page!.Items).Id);
        }
    }

    [Fact]
    public async Task Post_attachments_with_replace_takes_over_the_role()
    {
        await using var app = await TestApp.StartAsync();

        var first  = await UploadAsync(app);
        var second = await UploadAsync(app);

        await AttachAsync(app, first.Id, "Product", "1", MediaRoles.Primary, replace: true);
        await AttachAsync(app, second.Id, "Product", "1", MediaRoles.Primary, replace: true);

        var page = await app.Client.GetFromJsonAsync<MediaPageResponse>(
            new Uri($"/api/media?ownerEntityName=Product&ownerEntityId=1&role={MediaRoles.Primary}", UriKind.Relative));

        Assert.Equal(second.Id, Assert.Single(page!.Items).Id);

        // Displaced, not deleted: it may well be in use elsewhere.
        var displaced = await app.Client.GetAsync(new Uri($"/api/media/{first.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, displaced.StatusCode);
    }

    [Fact]
    public async Task Post_attachments_without_an_owner_is_a_bad_request()
    {
        await using var app = await TestApp.StartAsync();

        var uploaded = await UploadAsync(app);

        var response = await app.Client.PostAsJsonAsync(
            new Uri($"/api/media/{uploaded.Id}/attachments", UriKind.Relative),
            new AttachMediaRequest { OwnerEntityName = "Product" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_attachments_unlinks_one_entity_and_keeps_the_file()
    {
        await using var app = await TestApp.StartAsync();

        var uploaded = await UploadAsync(app);

        await AttachAsync(app, uploaded.Id, "Product", "1");
        var both = await AttachAsync(app, uploaded.Id, "Product", "2");

        var link = both.Attachments!.Single(a => a.OwnerEntityId == "1");

        var detached = await (await app.Client.DeleteAsync(new Uri(
                $"/api/media/{uploaded.Id}/attachments/{link.Id}", UriKind.Relative)))
            .Content.ReadFromJsonAsync<MediaFileResponse>();

        // The file is still there, and product 2 still has it.
        var left = Assert.Single(detached!.Attachments!);
        Assert.Equal("2", left.OwnerEntityId);

        var content = await app.Client.GetAsync(new Uri($"/api/media/{uploaded.Id}/content", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
    }

    [Fact]
    public async Task Delete_attachments_is_not_found_when_there_was_no_such_link()
    {
        await using var app = await TestApp.StartAsync();

        var uploaded = await UploadAsync(app);

        var response = await app.Client.DeleteAsync(new Uri(
            $"/api/media/{uploaded.Id}/attachments/{Guid.NewGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The file id in the route is checked, not decorative. Without that, muddling two ids would
    /// quietly remove a link belonging to a file the caller never named.
    /// </summary>
    [Fact]
    public async Task Delete_attachments_refuses_a_link_belonging_to_another_file()
    {
        await using var app = await TestApp.StartAsync();

        var mine  = await UploadAsync(app, "mine.png");
        var other = await UploadAsync(app, "other.png");

        await AttachAsync(app, mine.Id, "Product", "1");
        var attached = await AttachAsync(app, other.Id, "Product", "2");

        var otherLink = Assert.Single(attached.Attachments!);

        var response = await app.Client.DeleteAsync(new Uri(
            $"/api/media/{mine.Id}/attachments/{otherLink.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And the other file's link is still there.
        var read = await app.Client.GetFromJsonAsync<MediaFileResponse>(
            new Uri($"/api/media/{other.Id}", UriKind.Relative));

        Assert.Single(read!.Attachments!);
    }

    [Fact]
    public async Task Delete_removes_the_file_from_every_entity_using_it()
    {
        await using var app = await TestApp.StartAsync();

        var uploaded = await UploadAsync(app);

        await AttachAsync(app, uploaded.Id, "Product", "1");
        await AttachAsync(app, uploaded.Id, "Product", "2");

        await app.Client.DeleteAsync(new Uri($"/api/media/{uploaded.Id}", UriKind.Relative));

        foreach (var entityId in new[] { "1", "2" })
        {
            var page = await app.Client.GetFromJsonAsync<MediaPageResponse>(
                new Uri($"/api/media?ownerEntityName=Product&ownerEntityId={entityId}", UriKind.Relative));

            Assert.Empty(page!.Items);
        }
    }

    [Fact]
    public async Task Put_attachments_order_reorders_a_role()
    {
        await using var app = await TestApp.StartAsync();

        var a = await UploadAsync(app, "a.png");
        var b = await UploadAsync(app, "b.png");
        var c = await UploadAsync(app, "c.png");

        foreach (var file in new[] { a, b, c })
            await AttachAsync(app, file.Id, "Product", "1", MediaRoles.Gallery);

        var response = await app.Client.PutAsJsonAsync(
            new Uri("/api/media/attachments/order", UriKind.Relative),
            new ReorderAttachmentsRequest
            {
                OwnerEntityName = "Product",
                OwnerEntityId   = "1",
                Role            = MediaRoles.Gallery,
                MediaFileIds    = [c.Id, a.Id, b.Id]
            });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var page = await app.Client.GetFromJsonAsync<MediaPageResponse>(new Uri(
            $"/api/media?ownerEntityName=Product&ownerEntityId=1&role={MediaRoles.Gallery}" +
            "&sortBy=sortOrder&descending=false",
            UriKind.Relative));

        Assert.Equal(["c.png", "a.png", "b.png"], page!.Items.Select(f => f.FileName));
    }

    [Fact]
    public async Task Put_attachments_order_refuses_a_partial_order()
    {
        await using var app = await TestApp.StartAsync();

        var a = await UploadAsync(app, "a.png");
        var b = await UploadAsync(app, "b.png");

        foreach (var file in new[] { a, b })
            await AttachAsync(app, file.Id, "Product", "1", MediaRoles.Gallery);

        var response = await app.Client.PutAsJsonAsync(
            new Uri("/api/media/attachments/order", UriKind.Relative),
            new ReorderAttachmentsRequest
            {
                OwnerEntityName = "Product",
                OwnerEntityId   = "1",
                Role            = MediaRoles.Gallery,
                MediaFileIds    = [a.Id]
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_finds_the_files_nothing_is_attached_to()
    {
        await using var app = await TestApp.StartAsync();

        var loose = await UploadAsync(app, "loose.png");
        var used  = await UploadAsync(app, "used.png");
        await AttachAsync(app, used.Id, "Product", "1");

        var page = await app.Client.GetFromJsonAsync<MediaPageResponse>(
            new Uri("/api/media?unattached=true", UriKind.Relative));

        Assert.Equal(loose.Id, Assert.Single(page!.Items).Id);
    }

    [Fact]
    public async Task List_refuses_the_entitys_own_order_without_an_owner()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.GetAsync(new Uri("/api/media?sortBy=sortOrder", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<MediaFileResponse> UploadAsync(TestApp app, string fileName = "photo.png")
    {
        var created = await app.Client.PostAsync(
            new Uri("/api/media", UriKind.Relative),
            TestApp.Multipart(TestMedia.Png(), fileName));

        return (await created.Content.ReadFromJsonAsync<MediaFileResponse>())!;
    }

    private static async Task<MediaFileResponse> AttachAsync(
        TestApp app, Guid id, string entityName, string entityId, string? role = null, bool replace = false)
    {
        var response = await app.Client.PostAsJsonAsync(
            new Uri($"/api/media/{id}/attachments", UriKind.Relative),
            new AttachMediaRequest
            {
                OwnerEntityName = entityName,
                OwnerEntityId   = entityId,
                Role            = role,
                Replace         = replace
            });

        return (await response.Content.ReadFromJsonAsync<MediaFileResponse>())!;
    }

    [Fact]
    public async Task Delete_removes_the_bytes_and_hides_the_file()
    {
        await using var app = await TestApp.StartAsync();

        var created  = await app.Client.PostAsync(new Uri("/api/media", UriKind.Relative), TestApp.Multipart(TestMedia.Png()));
        var uploaded = (await created.Content.ReadFromJsonAsync<MediaFileResponse>())!;

        var response = await app.Client.DeleteAsync(new Uri($"/api/media/{uploaded.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Empty(app.Storage.Paths);

        var after = await app.Client.GetAsync(new Uri($"/api/media/{uploaded.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Delete_for_an_unknown_id_is_a_404()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.DeleteAsync(new Uri($"/api/media/{Guid.NewGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_roles_returns_what_the_entity_declares()
    {
        await using var app = await TestApp.StartAsync(media => media
            .DeclareRoles("Product", r => r.One("primary").Many("gallery").One("seo")));

        var roles = await app.Client.GetFromJsonAsync<List<MediaRoleResponse>>(
            new Uri("/api/media/roles/Product", UriKind.Relative));

        Assert.Equal(
            [("primary", false), ("gallery", true), ("seo", false)],
            roles!.Select(r => (r.Name, r.Multiple)));
    }

    /// <summary>
    /// An entity that declares nothing is a real entity that accepts any role, so a client should
    /// get an empty list and offer a free-text box — not a 404 telling it the entity is unknown.
    /// </summary>
    [Fact]
    public async Task Get_roles_is_empty_rather_than_missing_for_an_undeclared_entity()
    {
        await using var app = await TestApp.StartAsync(media => media
            .DeclareRoles("Product", r => r.One("primary")));

        var response = await app.Client.GetAsync(new Uri("/api/media/roles/Invoice", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<MediaRoleResponse>>())!);
    }

    /// <summary>
    /// The reason the registry is keyed by entity NAME: this request never sees a Product class, so
    /// an attribute read at attach time could not have checked it.
    /// </summary>
    [Fact]
    public async Task Attaching_through_the_api_is_checked_against_the_declared_roles()
    {
        await using var app = await TestApp.StartAsync(media => media
            .DeclareRoles("Product", r => r.One("primary").Many("gallery")));

        var uploaded = await UploadAsync(app);

        var response = await app.Client.PostAsJsonAsync(
            new Uri($"/api/media/{uploaded.Id}/attachments", UriKind.Relative),
            new AttachMediaRequest { OwnerEntityName = "Product", OwnerEntityId = "1", Role = "galery" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("no media role 'galery'", problem!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attaching_a_second_file_to_a_single_valued_role_through_the_api_is_refused()
    {
        await using var app = await TestApp.StartAsync(media => media
            .DeclareRoles("Product", r => r.One("primary")));

        var first  = await UploadAsync(app, "a.png");
        var second = await UploadAsync(app, "b.png");

        await AttachAsync(app, first.Id, "Product", "1", "primary");

        var response = await app.Client.PostAsJsonAsync(
            new Uri($"/api/media/{second.Id}/attachments", UriKind.Relative),
            new AttachMediaRequest { OwnerEntityName = "Product", OwnerEntityId = "1", Role = "primary" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // replace: true is the supported way, and it works.
        var replaced = await AttachAsync(app, second.Id, "Product", "1", "primary", replace: true);
        Assert.Equal("1", Assert.Single(replaced.Attachments!).OwnerEntityId);
    }

    /// <summary>
    /// A body the binder cannot read is the client's mistake, and ASP.NET Core already says so:
    /// BadHttpRequestException carries StatusCode 400. Registering an exception handler must not
    /// throw that away and answer 500, which sends the caller looking for a fault on the server.
    /// </summary>
    [Fact]
    public async Task A_malformed_body_is_a_bad_request_not_a_server_error()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.PutAsync(
            new Uri("/api/media/attachments/order", UriKind.Relative),
            new StringContent(
                """{"ownerEntityName":"Product","ownerEntityId":"1","mediaFileIds":["not-a-guid"]}""",
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Body_that_is_not_json_at_all_is_a_bad_request()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.PutAsync(
            new Uri("/api/media/attachments/order", UriKind.Relative),
            new StringContent("{ this is not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
