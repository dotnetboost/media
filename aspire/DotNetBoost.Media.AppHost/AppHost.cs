// ─────────────────────────────────────────────────────────────────────────────
//  DotNetBoost.Media — Aspire AppHost
//
//  Starts the whole stack with one `dotnet run --project` here — no Aspire CLI required,
//  though `aspire run` does the same if you have it installed:
//
//    • the database container holding the sample's tables and MediaFiles  (PostgreSQL)
//    • the API       — samples/SampleApp, storing bytes on local disk
//    • the dashboard — clients/dashboard (Nuxt), pointed at the API
//
//  The sample's DATABASE and its STORAGE BACKEND are both swappable, and they are independent of
//  one another: any of the three metadata stores pairs with any of the three backends. Exactly one
//  of each is active below, each alternative written out and commented. To switch, comment the
//  active block, uncomment the one you want, uncomment its PackageReference in
//  DotNetBoost.Media.AppHost.csproj, and flip the matching blocks in samples/SampleApp/Program.cs
//  and samples/SampleApp/SampleApp.csproj.
// ─────────────────────────────────────────────────────────────────────────────

var builder = DistributedApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
//  Database — ACTIVE: PostgreSQL (Entity Framework Core)
//  Package: Aspire.Hosting.PostgreSQL
// ─────────────────────────────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
    // Named volume, so files uploaded in one session are still recorded in the next.
    .WithDataVolume("dotnetboost-media-pgdata")
    .WithPgAdmin();

var database = postgres.AddDatabase("media");

// ─────────────────────────────────────────────────────────────────────────────
//  Database — ALTERNATIVE: SQL Server (Entity Framework Core)
//  Package: Aspire.Hosting.SqlServer
// ─────────────────────────────────────────────────────────────────────────────
// var sqlServer = builder.AddSqlServer("sqlserver")
//     .WithDataVolume("dotnetboost-media-mssqldata");
//
// var database = sqlServer.AddDatabase("media");

// ─────────────────────────────────────────────────────────────────────────────
//  Database — ALTERNATIVE: MongoDB
//  Package: Aspire.Hosting.MongoDB
// ─────────────────────────────────────────────────────────────────────────────
// var mongo = builder.AddMongoDB("mongo")
//     .WithDataVolume("dotnetboost-media-mongodata");
//
// var database = mongo.AddDatabase("media");

// ─────────────────────────────────────────────────────────────────────────────
//  Storage backend — ALTERNATIVE: Azure Blob Storage, through Azurite
//  Package: Aspire.Hosting.Azure.Storage
//
//  The ACTIVE backend is local disk, which needs no resource here at all — that is rather the
//  point of it. Uncomment this to run the sample against a real Blob Storage API locally, and add
//  `.WithReference(blobs)` to the project below.
// ─────────────────────────────────────────────────────────────────────────────
// var blobs = builder.AddAzureStorage("storage")
//     .RunAsEmulator(emulator => emulator.WithDataVolume("dotnetboost-media-azurite"))
//     .AddBlobs("blobs");

// ─────────────────────────────────────────────────────────────────────────────
//  The API
// ─────────────────────────────────────────────────────────────────────────────
var api = builder.AddProject<Projects.SampleApp>("api")
    .WithReference(database)
    .WaitFor(database)
    // .WithReference(blobs).WaitFor(blobs)   // with the Azure backend active
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// ── Dashboard — clients/dashboard (Nuxt) ─────────────────────────────────────
// npm install runs automatically before the dev server starts.
builder.AddJavaScriptApp("dashboard", "../../clients/dashboard", "dev")
    .WithNpm()
    // Nuxt binds whatever PORT says; without this it picks its own and Aspire has no
    // endpoint to publish, so the dashboard never gets a clickable URL.
    .WithHttpEndpoint(env: "PORT")
    .WithReference(api)
    .WaitFor(api)
    // Consumed by server/api/[...path].ts, which proxies the browser's calls — so the dashboard
    // reaches the media endpoints and the sample's product endpoints through one origin.
    .WithEnvironment("NUXT_MEDIA_API_URL", api.GetEndpoint("http"))
    // The sidebar's "API reference" link. Read in the browser, so it needs the public URL.
    .WithEnvironment("NUXT_PUBLIC_API_REFERENCE_URL",
        ReferenceExpression.Create($"{api.GetEndpoint("http")}/scalar"))
    // Must match MediaOptions.MaxFileSizeBytes in samples/SampleApp/Program.cs: the upload form
    // refuses a larger file before sending it, rather than after a 413 comes back.
    .WithEnvironment("NUXT_PUBLIC_MAX_UPLOAD_BYTES", "26214400")
    .WithExternalHttpEndpoints();

builder.Build().Run();
