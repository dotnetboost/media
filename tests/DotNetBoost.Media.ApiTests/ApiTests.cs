using DotNetBoost.Media.API;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.TestSupport;
using Microsoft.AspNetCore.Mvc;
using System.Net;
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

        Assert.Equal("Product", file!.OwnerEntityName);
        Assert.Equal("42", file.OwnerEntityId);
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
    public async Task Patch_can_attach_and_detach()
    {
        await using var app = await TestApp.StartAsync();

        var created  = await app.Client.PostAsync(new Uri("/api/media", UriKind.Relative), TestApp.Multipart(TestMedia.Png()));
        var uploaded = (await created.Content.ReadFromJsonAsync<MediaFileResponse>())!;

        var attached = await (await app.Client.PatchAsJsonAsync(
            new Uri($"/api/media/{uploaded.Id}", UriKind.Relative),
            new UpdateMediaRequest { OwnerEntityName = "Product", OwnerEntityId = "7" }))
            .Content.ReadFromJsonAsync<MediaFileResponse>();

        Assert.Equal("Product", attached!.OwnerEntityName);

        var detached = await (await app.Client.PatchAsJsonAsync(
            new Uri($"/api/media/{uploaded.Id}", UriKind.Relative),
            new UpdateMediaRequest { ClearOwner = true }))
            .Content.ReadFromJsonAsync<MediaFileResponse>();

        Assert.Null(detached!.OwnerEntityName);
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
}
