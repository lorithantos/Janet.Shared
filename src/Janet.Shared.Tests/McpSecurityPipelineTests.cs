using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Janet.Coalescing.Testing;
using Janet.Mcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Janet.Shared.Tests;

/// <summary>
/// <see cref="McpSecurityExtensions.UseMcpSecurity"/> in a real pipeline: Kestrel on an OS-chosen
/// 127.0.0.1 port, dialled over a socket, the way the servers' own guard tests run. The app maps
/// / (stands for every endpoint the check protects) and /health (which serializes the snapshot,
/// as the servers will).
/// </summary>
public sealed class McpSecurityPipelineTests : IAsyncDisposable
{
    private const string Token = "pipeline-token-0123456789abcdefghijklmnopq";

    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private WebApplication? app;
    private BearerAuthenticator? bearer;
    private Uri baseUri = null!;

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        bearer?.Dispose();
    }

    private async Task StartAsync(BearerMode mode, string? token = Token)
    {
        bearer = new BearerAuthenticator(mode, () => token, new FakeTime());
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        app = builder.Build();
        app.UseMcpSecurity(bearer);
        app.MapGet("/", () => "served");
        app.MapPost("/", () => "served");
        app.MapGet("/health", () => Results.Json(bearer.Snapshot()));
        await app.StartAsync();
        baseUri = new Uri(Assert.Single(app.Urls).TrimEnd('/') + "/");
    }

    private Task<HttpResponseMessage> GetAsync(string path, string? authorization = null, string? host = null)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(baseUri, path));
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        return client.SendAsync(request);
    }

    [Fact]
    public async Task Require_refuses_a_request_with_no_token_with_a_plain_text_401()
    {
        await StartAsync(BearerMode.Require);

        using HttpResponseMessage response = await GetAsync("");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.StartsWith("Refused: this server requires a bearer token", body);
        Assert.Contains("janet mcp-headers", body);
    }

    [Fact]
    public async Task Require_refuses_a_wrong_token_without_echoing_either_token()
    {
        await StartAsync(BearerMode.Require);

        using HttpResponseMessage response = await GetAsync("", "Bearer guessed-token-value");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("guessed-token-value", body);
        Assert.DoesNotContain(Token, body);
    }

    [Fact]
    public async Task Require_refuses_another_scheme()
    {
        await StartAsync(BearerMode.Require);

        using HttpResponseMessage response = await GetAsync("", "Basic " + Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Require_serves_the_right_token()
    {
        await StartAsync(BearerMode.Require);

        using HttpResponseMessage response = await GetAsync("", "Bearer " + Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("served", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Require_with_no_token_configured_refuses_everything_and_says_so()
    {
        await StartAsync(BearerMode.Require, token: null);

        using HttpResponseMessage response = await GetAsync("", "Bearer " + Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("no token configured", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_is_served_without_a_token_and_is_not_counted()
    {
        await StartAsync(BearerMode.Require);

        using HttpResponseMessage response = await GetAsync("health");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"mode\":\"Require\"", body);
        Assert.Contains("\"missing\":0", body);
    }

    [Fact]
    public async Task The_guard_runs_before_the_bearer_check()
    {
        await StartAsync(BearerMode.Require);

        // A forged Host and no token: the guard's 400, not the bearer check's 401, and nothing
        // reaches the counters.
        using HttpResponseMessage response = await GetAsync("", host: "rebind.evil.example");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("Refused: Host 'rebind.evil.example'", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, bearer!.Snapshot().Missing);
    }

    [Fact]
    public async Task The_guard_still_refuses_health_from_a_foreign_origin()
    {
        await StartAsync(BearerMode.Require);
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(baseUri, "health"));
        request.Headers.TryAddWithoutValidation("Origin", "http://rebind.evil.example");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task The_guard_refuses_a_text_plain_post_before_the_bearer_check()
    {
        await StartAsync(BearerMode.Require);

        using HttpResponseMessage response = await client.PostAsync(
            baseUri, new StringContent("{}", new MediaTypeHeaderValue("text/plain")));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Report_serves_everything_and_counts_each_outcome()
    {
        await StartAsync(BearerMode.Report);

        using HttpResponseMessage none = await GetAsync("");
        using HttpResponseMessage wrong = await GetAsync("", "Bearer nope");
        using HttpResponseMessage right = await GetAsync("", "Bearer " + Token);

        Assert.All([none, wrong, right], r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        BearerAuthSnapshot snapshot = bearer!.Snapshot();
        Assert.Equal((1L, 1L, 1L), (snapshot.Ok, snapshot.Missing, snapshot.Wrong));
    }

    [Fact]
    public async Task Off_serves_everything_and_counts_nothing()
    {
        await StartAsync(BearerMode.Off);

        using HttpResponseMessage response = await GetAsync("", "Bearer nope");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        BearerAuthSnapshot snapshot = bearer!.Snapshot();
        Assert.Equal((0L, 0L, 0L, 0L), (snapshot.Ok, snapshot.Missing, snapshot.Wrong, snapshot.Unconfigured));
    }
}
