using Janet.Mcp.Security;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Janet.Shared.Tests;

/// <summary>
/// The loopback guard's three decisions held to their cases: the union of what janet-mcp and
/// razorgraph-mcp each tested while they carried their own copies. MEASURED 2026-10-21 before the
/// guard: a forged Host and Origin got 200 from MCP initialize and a bearer token from /az/token.
/// The guard in a real pipeline is covered by McpSecurityPipelineTests; each server keeps its own
/// pipeline tests for the order of its endpoints.
/// </summary>
public class LoopbackGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:7717")]
    [InlineData("127.0.0.1:7718")]
    [InlineData("localhost")]
    [InlineData("localhost:7717")]
    [InlineData("LOCALHOST:7718")]
    [InlineData("LocalHost")]
    [InlineData("[::1]")]
    [InlineData("[::1]:7717")]
    public void Loopback_hosts_are_accepted(string host) =>
        Assert.True(LoopbackGuard.IsLoopbackHost(host));

    [Theory]
    [InlineData("rebind.evil.example")]
    [InlineData("rebind.evil.example:7718")]
    [InlineData("localhost.evil.example")]
    [InlineData("localhost.evil.example:7717")]
    [InlineData("127.0.0.1.nip.io")]
    [InlineData("127.0.0.1.nip.io:7717")]
    [InlineData("127.0.0.2")]
    [InlineData("127.0.0.2:7718")]
    [InlineData("0.0.0.0")]
    [InlineData("[::2]:7717")]
    [InlineData("::1")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("localhost:")]
    [InlineData("localhost:77a")]
    [InlineData("localhost:abc")]
    [InlineData("localhost:7718:1")]
    [InlineData("localhost/")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_not_a_loopback_host(string? host) =>
        Assert.False(LoopbackGuard.IsLoopbackHost(host));

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:7717")]
    [InlineData("HTTP://LOCALHOST")]
    [InlineData("HTTP://LOCALHOST:7718")]
    [InlineData("http://127.0.0.1:7717")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://127.0.0.1:7718")]
    [InlineData("https://localhost")]
    [InlineData("https://localhost:443")]
    [InlineData("http://[::1]:7717")]
    [InlineData("https://[::1]")]
    public void No_origin_or_a_loopback_origin_is_allowed(string? origin) =>
        Assert.True(LoopbackGuard.IsAllowedOrigin(origin));

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("http://rebind.evil.example")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://127.0.0.1.nip.io")]
    [InlineData("http://127.0.0.2")]
    [InlineData("http://127.0.0.2:7718")]
    [InlineData("http://localhost@evil.example")]
    [InlineData("http://evil.example@localhost")]
    [InlineData("http://user@localhost")]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost/path")]
    [InlineData("http://localhost?q=1")]
    [InlineData("http://localhost#f")]
    [InlineData("ws://localhost:7717")]
    [InlineData("file://")]
    [InlineData("file://localhost")]
    [InlineData("localhost")]
    [InlineData("http://localhost, http://rebind.evil.example")]
    public void A_cross_site_or_malformed_origin_is_refused(string origin) =>
        Assert.False(LoopbackGuard.IsAllowedOrigin(origin));

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON;charset=UTF-8")]
    [InlineData(" application/json ")]
    public void Json_content_types_are_accepted(string contentType) =>
        Assert.True(LoopbackGuard.IsJsonContentType(contentType));

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/jsonp")]
    [InlineData("text/plain; x=application/json")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_content_types_are_not_json(string? contentType) =>
        Assert.False(LoopbackGuard.IsJsonContentType(contentType));

    [Fact]
    public void A_forged_host_is_refused_with_400_naming_it()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check(
            "POST", "rebind.evil.example", new StringValues("http://rebind.evil.example"), "application/json");

        Assert.NotNull(refusal);
        Assert.Equal(400, refusal.Value.Status);
        Assert.Contains("rebind.evil.example", refusal.Value.Message);
    }

    [Fact]
    public void An_empty_host_is_refused_with_400() =>
        Assert.Equal(400, LoopbackGuard.Check("GET", "", StringValues.Empty, null)?.Status);

    [Fact]
    public void A_foreign_origin_is_refused_with_403_naming_it()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check(
            "POST", "127.0.0.1:7718", new StringValues("http://rebind.evil.example"), "application/json");

        Assert.NotNull(refusal);
        Assert.Equal(403, refusal.Value.Status);
        Assert.Contains("http://rebind.evil.example", refusal.Value.Message);
    }

    [Fact]
    public void A_null_origin_is_refused_with_403_naming_it()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check("GET", "127.0.0.1:7717", new StringValues("null"), null);

        Assert.NotNull(refusal);
        Assert.Equal(403, refusal.Value.Status);
        Assert.Contains("'null'", refusal.Value.Message);
    }

    [Fact]
    public void Two_origin_headers_are_refused_even_if_both_are_loopback()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check(
            "GET", "localhost", new StringValues(["http://localhost", "http://127.0.0.1"]), null);

        Assert.Equal(403, refusal?.Status);
    }

    [Fact]
    public void A_loopback_get_with_no_origin_and_no_content_type_is_let_through() =>
        Assert.Null(LoopbackGuard.Check("GET", "127.0.0.1:7717", StringValues.Empty, null));

    [Fact]
    public void An_https_loopback_origin_is_let_through() =>
        Assert.Null(LoopbackGuard.Check("POST", "localhost:7718", new StringValues("https://localhost:7718"), "application/json"));

    [Fact]
    public void A_post_with_no_content_type_is_refused_with_415() =>
        Assert.Equal(415, LoopbackGuard.Check("post", "127.0.0.1:7718", StringValues.Empty, null)?.Status);

    [Fact]
    public void A_text_plain_post_is_refused_with_415_naming_it()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check("post", "127.0.0.1:7717", StringValues.Empty, "text/plain");

        Assert.NotNull(refusal);
        Assert.Equal(415, refusal.Value.Status);
        Assert.Contains("text/plain", refusal.Value.Message);
    }

    [Fact]
    public void A_json_post_with_a_charset_and_no_origin_is_let_through() =>
        Assert.Null(LoopbackGuard.Check("POST", "[::1]:7717", StringValues.Empty, "application/json; charset=utf-8"));

    [Fact]
    public void The_host_is_judged_before_the_origin()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check(
            "POST", "rebind.evil.example", new StringValues("http://rebind.evil.example"), "text/plain");

        Assert.Equal(400, refusal?.Status);
    }

    [Fact]
    public void A_long_refused_value_is_echoed_capped_at_200_characters()
    {
        string host = new string('a', 500) + ".evil.example";

        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check("GET", host, StringValues.Empty, null);

        Assert.NotNull(refusal);
        Assert.Contains("'" + new string('a', 200) + "...'", refusal.Value.Message);
        Assert.DoesNotContain(new string('a', 201), refusal.Value.Message);
    }
}
