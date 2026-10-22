using System.Text.Json;
using Janet.Coalescing.Testing;
using Janet.Mcp.Security;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Janet.Shared.Tests;

/// <summary>
/// The bearer decision on its own: each kind of header, the counters, and the throttled re-read
/// that picks up a rotated token. The clock is <see cref="FakeTime"/>, so no test sleeps; waits on
/// the refresh consumer are bounded by <see cref="Bound"/> so a lost answer fails rather than hangs.
/// </summary>
public sealed class BearerAuthenticatorTests : IDisposable
{
    private const string Token = "right-token-0123456789abcdefghijklmnopqrstu";
    private const string Rotated = "rotated-token-0123456789abcdefghijklmnopqrs";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly FakeTime clock = new();
    private readonly List<BearerAuthenticator> created = [];
    private string? current = Token;
    private int asked;

    // When set, the provider reports that it was entered and then blocks until the gate opens,
    // holding the consumer mid-batch so the test can queue requests behind it.
    private ManualResetEventSlim? gate;
    private TaskCompletionSource? entered;

    public void Dispose()
    {
        gate?.Set();
        foreach (BearerAuthenticator bearer in created)
        {
            bearer.Dispose();
        }
    }

    private BearerAuthenticator Create(BearerMode mode = BearerMode.Require, string? headerHint = null)
    {
        BearerAuthenticator bearer = new(mode, Provide, clock, headerHint);
        created.Add(bearer);
        return bearer;
    }

    private string? Provide()
    {
        asked++;
        if (gate is { } g)
        {
            entered?.TrySetResult();
            g.Wait(Bound);
        }

        return current;
    }

    private static Task<BearerOutcome> Judge(BearerAuthenticator bearer, StringValues header) =>
        bearer.AuthenticateAsync(header).AsTask().WaitAsync(Bound);

    [Fact]
    public async Task No_authorization_header_is_missing() =>
        Assert.Equal(BearerOutcome.Missing, await Judge(Create(), StringValues.Empty));

    [Fact]
    public async Task The_right_token_is_ok() =>
        Assert.Equal(BearerOutcome.Ok, await Judge(Create(), "Bearer " + Token));

    [Theory]
    [InlineData("bearer ")]
    [InlineData("BEARER ")]
    [InlineData("  Bearer   ")]
    public async Task The_scheme_is_case_insensitive_and_whitespace_is_ignored(string prefix) =>
        Assert.Equal(BearerOutcome.Ok, await Judge(Create(), prefix + Token + " "));

    [Theory]
    [InlineData("Basic " + Token)]
    [InlineData("Token " + Token)]
    [InlineData(Token)]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer wrong-token")]
    [InlineData("Bearer " + Token + "x")]
    [InlineData("Bearer " + "right-token")]
    public async Task Anything_but_the_bearer_token_is_wrong(string header) =>
        Assert.Equal(BearerOutcome.Wrong, await Judge(Create(), header));

    [Fact]
    public async Task Two_authorization_headers_are_wrong_even_if_one_is_right() =>
        Assert.Equal(
            BearerOutcome.Wrong,
            await Judge(Create(), new StringValues(["Bearer " + Token, "Bearer " + Token])));

    [Fact]
    public async Task A_provider_with_no_token_makes_every_request_unconfigured()
    {
        current = null;
        BearerAuthenticator bearer = Create();

        Assert.Equal(BearerOutcome.Unconfigured, await Judge(bearer, "Bearer " + Token));
        Assert.Equal(BearerOutcome.Unconfigured, await Judge(bearer, StringValues.Empty));
        Assert.Contains("no token configured", bearer.RefusalMessage(BearerOutcome.Unconfigured));
        Assert.False(bearer.Snapshot().TokenConfigured);
    }

    [Fact]
    public async Task A_provider_that_throws_is_unconfigured_and_says_why_in_the_snapshot()
    {
        using BearerAuthenticator bearer = new(
            BearerMode.Require, () => throw new InvalidOperationException("HKCU\\x\\McpToken is corrupt"), clock);

        Assert.Equal(BearerOutcome.Unconfigured, await Judge(bearer, "Bearer " + Token));
        BearerAuthSnapshot snapshot = bearer.Snapshot();
        Assert.False(snapshot.TokenConfigured);
        Assert.Equal("InvalidOperationException: HKCU\\x\\McpToken is corrupt", snapshot.ProviderError);
        Assert.Contains("GET /health says why", bearer.RefusalMessage(BearerOutcome.Unconfigured));
    }

    [Fact]
    public async Task Every_outcome_is_counted()
    {
        BearerAuthenticator bearer = Create(BearerMode.Report);

        await Judge(bearer, "Bearer " + Token);
        await Judge(bearer, "Bearer " + Token);
        await Judge(bearer, StringValues.Empty);
        await Judge(bearer, "Bearer nope");
        await Judge(bearer, "Basic abc");

        BearerAuthSnapshot snapshot = bearer.Snapshot();
        Assert.Equal("Report", snapshot.Mode);
        Assert.Equal((2L, 1L, 2L, 0L), (snapshot.Ok, snapshot.Missing, snapshot.Wrong, snapshot.Unconfigured));
        Assert.True(snapshot.TokenConfigured);
        Assert.Equal(clock.GetUtcNow(), snapshot.TokenReadAt);
    }

    [Fact]
    public async Task The_snapshot_and_the_refusals_never_carry_a_token()
    {
        BearerAuthenticator bearer = Create();
        await Judge(bearer, "Bearer " + Token);
        await Judge(bearer, "Bearer " + Rotated);

        string json = JsonSerializer.Serialize(bearer.Snapshot());
        string bodies = string.Join(
            "\n", new[] { BearerOutcome.Missing, BearerOutcome.Wrong, BearerOutcome.Unconfigured }.Select(bearer.RefusalMessage));

        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain(Rotated, json);
        Assert.DoesNotContain(Token, bodies);
        Assert.DoesNotContain("resource_metadata", bodies);
    }

    private static string[] Refusals(BearerAuthenticator bearer) =>
        new[] { BearerOutcome.Missing, BearerOutcome.Wrong, BearerOutcome.Unconfigured }
            .Select(bearer.RefusalMessage)
            .ToArray();

    [Fact]
    public void With_no_hint_every_refusal_ends_with_a_sentence_naming_no_tool()
    {
        string[] bodies = Refusals(Create());

        Assert.All(bodies, body => Assert.EndsWith("send the token this server is configured with.", body, StringComparison.OrdinalIgnoreCase));
        Assert.All(bodies, body => Assert.DoesNotContain("janet", body, StringComparison.OrdinalIgnoreCase));
        Assert.All(bodies, body => Assert.DoesNotContain("`", body));
        Assert.Equal("Send the token this server is configured with.", BearerAuthenticator.DefaultHeaderHint);
    }

    [Fact]
    public void A_supplied_hint_ends_every_refusal_in_place_of_the_default()
    {
        const string Hint = "Run `example-tool headers` to print the header to send.";
        string[] bodies = Refusals(Create(headerHint: "  " + Hint + "\n"));

        Assert.EndsWith(" " + Hint, bodies[0]);
        Assert.EndsWith(" " + Hint, bodies[1]);
        Assert.EndsWith(" Once the server has its token, run `example-tool headers` to print the header to send.", bodies[2]);
        Assert.All(bodies, body => Assert.DoesNotContain(BearerAuthenticator.DefaultHeaderHint, body, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_hint_is_refused_rather_than_sent(string hint) =>
        Assert.Throws<ArgumentException>("headerHint", () => new BearerAuthenticator(BearerMode.Off, Provide, clock, hint));

    [Fact]
    public async Task A_rotated_token_is_picked_up_once_the_throttle_allows_a_re_read()
    {
        BearerAuthenticator bearer = Create();
        current = Rotated;

        // Asked at construction, so the first 30 seconds judge against the cached token.
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(BearerOutcome.Wrong, await Judge(bearer, "Bearer " + Rotated));
        Assert.Equal(BearerOutcome.Ok, await Judge(bearer, "Bearer " + Token));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(BearerOutcome.Ok, await Judge(bearer, "Bearer " + Rotated));
        Assert.Equal(BearerOutcome.Wrong, await Judge(bearer, "Bearer " + Token));
    }

    [Fact]
    public async Task Wrong_guesses_ask_the_provider_at_most_once_per_30_seconds()
    {
        BearerAuthenticator bearer = Create();
        Assert.Equal(1, asked);

        clock.Advance(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(BearerOutcome.Wrong, await Judge(bearer, "Bearer guess-" + i));
        }

        Assert.Equal(2, asked);

        clock.Advance(TimeSpan.FromSeconds(29));
        await Judge(bearer, "Bearer guess");
        Assert.Equal(2, asked);

        clock.Advance(TimeSpan.FromSeconds(1));
        await Judge(bearer, "Bearer guess");
        Assert.Equal(3, asked);
    }

    [Fact]
    public async Task The_right_token_and_a_missing_header_never_ask_the_provider()
    {
        BearerAuthenticator bearer = Create();
        clock.Advance(TimeSpan.FromMinutes(5));

        await Judge(bearer, "Bearer " + Token);
        await Judge(bearer, StringValues.Empty);

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task A_token_created_after_startup_is_picked_up_on_the_throttle()
    {
        current = null;
        BearerAuthenticator bearer = Create();
        current = Token;

        Assert.Equal(BearerOutcome.Unconfigured, await Judge(bearer, "Bearer " + Token));

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(BearerOutcome.Ok, await Judge(bearer, "Bearer " + Token));
        Assert.True(bearer.Snapshot().TokenConfigured);
    }

    [Fact]
    public async Task Surrounding_whitespace_from_the_provider_is_ignored()
    {
        current = Token + "\r\n";

        Assert.Equal(BearerOutcome.Ok, await Judge(Create(), "Bearer " + Token));
    }

    [Fact]
    public async Task Off_never_asks_the_provider_and_judges_nothing()
    {
        BearerAuthenticator bearer = Create(BearerMode.Off);

        Assert.Equal(0, asked);
        Assert.Equal("Off", bearer.Snapshot().Mode);
        Assert.Null(bearer.Snapshot().TokenReadAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bearer.AuthenticateAsync("Bearer " + Token).AsTask());
    }

    [Fact]
    public async Task One_provider_call_answers_every_request_waiting_in_a_batch()
    {
        BearerAuthenticator bearer = Create();
        clock.Advance(TimeSpan.FromSeconds(30));
        gate = new ManualResetEventSlim(false);
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A wrong guess holds the consumer inside the provider...
        Task<BearerOutcome> first = bearer.AuthenticateAsync("Bearer guess").AsTask();
        await entered.Task.WaitAsync(Bound);
        Assert.Equal(2, asked);

        // ...so these queue behind it: each call writes its refresh request before it returns.
        current = Rotated;
        clock.Advance(TimeSpan.FromSeconds(30));
        Task<BearerOutcome>[] queued =
            [.. Enumerable.Range(0, 5).Select(_ => bearer.AuthenticateAsync("Bearer " + Rotated).AsTask())];

        gate.Set();
        BearerOutcome[] outcomes = await Task.WhenAll(queued).WaitAsync(Bound);

        Assert.Equal(BearerOutcome.Wrong, await first.WaitAsync(Bound));
        Assert.All(outcomes, o => Assert.Equal(BearerOutcome.Ok, o));
        Assert.Equal(3, asked);
    }

    [Fact]
    public async Task Dispose_faults_a_request_waiting_for_a_refresh_and_refuses_later_ones()
    {
        BearerAuthenticator bearer = Create();
        clock.Advance(TimeSpan.FromSeconds(30));
        gate = new ManualResetEventSlim(false);
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<BearerOutcome> inProvider = bearer.AuthenticateAsync("Bearer guess-1").AsTask();
        await entered.Task.WaitAsync(Bound);
        Task<BearerOutcome> waiting = bearer.AuthenticateAsync("Bearer guess-2").AsTask();

        bearer.Dispose();
        gate.Set();

        Assert.Equal(BearerOutcome.Wrong, await inProvider.WaitAsync(Bound));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(Bound));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bearer.AuthenticateAsync("Bearer guess-3").AsTask());
        Assert.Equal(BearerOutcome.Ok, await Judge(bearer, "Bearer " + Token));
    }
}
