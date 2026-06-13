using System.Net;
using System.Net.Http.Json;
using Clowd.Server.Api;
using Clowd.Server.Destinations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Clowd.Server.Tests;

public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "clowd-server-api-tests-" + Guid.NewGuid().ToString("N"));

    public FakeDestinationProvider Fake { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Clowd:CachePath", Path.Combine(_tmp, "cache"));
        builder.UseSetting("Clowd:RedirectsPath", Path.Combine(_tmp, "redirects"));
        builder.ConfigureServices(services => services.AddSingleton<IDestinationProvider>(Fake));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        { }
    }
}

public sealed class ApiTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    private static StartUploadRequest NewRequest() => new()
    {
        Provider = "fake",
        FileName = "hello.txt",
        ContentType = "text/plain",
        Credentials = new Dictionary<string, string> { ["apiKey"] = "secret" },
    };

    private HttpClient CreateClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task FullFlow_Start_Upload_ThenDownloadRedirectsToDestination()
    {
        var client = CreateClient();

        var startResponse = await client.PostAsJsonAsync("/api/v1/uploads", NewRequest());
        startResponse.EnsureSuccessStatusCode();
        var start = (await startResponse.Content.ReadFromJsonAsync<StartUploadResponse>())!;

        Assert.NotNull(start.UploadUrl);
        Assert.NotNull(start.DownloadUrl);
        Assert.StartsWith("https://final.example.com/", start.FinalUrl);
        Assert.NotNull(start.Delete?.DeleteUrl);

        var payload = "hello streaming world"u8.ToArray();
        var putResponse = await client.PutAsync(start.UploadUrl, new ByteArrayContent(payload));
        putResponse.EnsureSuccessStatusCode();
        var completed = (await putResponse.Content.ReadFromJsonAsync<UploadCompleteResponse>())!;
        Assert.Equal(start.FinalUrl, completed.FinalUrl);
        Assert.Equal(payload.Length, completed.Length);

        // completed upload: download is now a permanent redirect to the destination
        var download = await client.GetAsync(start.DownloadUrl);
        Assert.Equal(HttpStatusCode.MovedPermanently, download.StatusCode);
        Assert.Equal(start.FinalUrl, download.Headers.Location!.ToString());
    }

    [Fact]
    public async Task DownloadWorksBeforeAnyBytesAreUploaded()
    {
        var client = CreateClient();
        var start = (await (await client.PostAsJsonAsync("/api/v1/uploads", NewRequest()))
            .Content.ReadFromJsonAsync<StartUploadResponse>())!;

        // the link is shareable before the PUT even starts: response headers arrive immediately
        var pending = await client.GetAsync(start.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal("text/plain", pending.Content.Headers.ContentType!.MediaType);

        var payload = "late bytes"u8.ToArray();
        var put = await client.PutAsync(start.UploadUrl, new ByteArrayContent(payload));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var body = await pending.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, body);
    }

    [Fact]
    public async Task UploadWithWrongTokenIsRejected()
    {
        var client = CreateClient();
        var start = (await (await client.PostAsJsonAsync("/api/v1/uploads", NewRequest()))
            .Content.ReadFromJsonAsync<StartUploadResponse>())!;

        var url = start.UploadUrl.Split('?')[0] + "?token=wrong";
        var response = await client.PutAsync(url, new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UploadBodyCanOnlyBeSentOnce()
    {
        var client = CreateClient();
        var start = (await (await client.PostAsJsonAsync("/api/v1/uploads", NewRequest()))
            .Content.ReadFromJsonAsync<StartUploadResponse>())!;

        (await client.PutAsync(start.UploadUrl, new ByteArrayContent([1]))).EnsureSuccessStatusCode();
        var second = await client.PutAsync(start.UploadUrl, new ByteArrayContent([2]));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task UnknownDownloadIs404()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/d/does-not-exist-1234");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownProviderIs400()
    {
        var client = CreateClient();
        var request = NewRequest();
        request.Provider = "gopher";
        var response = await client.PostAsJsonAsync("/api/v1/uploads", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MissingCredentialsIs400()
    {
        var client = CreateClient();
        var request = NewRequest();
        request.Credentials = null;
        var response = await client.PostAsJsonAsync("/api/v1/uploads", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
