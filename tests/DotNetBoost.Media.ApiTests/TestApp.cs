global using Xunit;
using DotNetBoost.Media.API;
using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace DotNetBoost.Media.ApiTests;

/// <summary>
/// A real ASP.NET Core pipeline over an in-memory transport and the in-memory doubles, wired the
/// way an application wires it: <c>MapMediaEndpoints()</c> and the exception handler.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApp(WebApplication app, InMemoryMediaStore store, InMemoryStorageProvider storage)
    {
        _app    = app;
        Store   = store;
        Storage = storage;
        Client  = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public InMemoryMediaStore Store { get; }

    public InMemoryStorageProvider Storage { get; }

    /// <param name="configure">Further engine configuration, such as an allow-list or a size cap.</param>
    public static async Task<TestApp> StartAsync(Action<MediaBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var store   = new InMemoryMediaStore();
        var storage = new InMemoryStorageProvider();

        var media = builder.Services.AddMedia()
            .UseCustomStore(_ => store)
            .UseCustomStorage("InMemory", _ => storage)
            .DisableStartupValidation();

        configure?.Invoke(media);
        media.Build();

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<MediaExceptionHandler>();

        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapMediaEndpoints();

        await app.StartAsync();
        return new TestApp(app, store, storage);
    }

    /// <summary>A multipart body with one file in it, as a browser sends one.</summary>
    public static MultipartFormDataContent Multipart(
        byte[] content,
        string fileName = "photo.png",
        string contentType = "image/png",
        string? category = null,
        string? tags = null,
        string? ownerEntityName = null,
        string? ownerEntityId = null)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        var form = new MultipartFormDataContent { { file, "file", fileName } };

        if (category is not null) form.Add(new StringContent(category), "category");
        if (tags is not null) form.Add(new StringContent(tags), "tags");
        if (ownerEntityName is not null) form.Add(new StringContent(ownerEntityName), "ownerEntityName");
        if (ownerEntityId is not null) form.Add(new StringContent(ownerEntityId), "ownerEntityId");

        return form;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
