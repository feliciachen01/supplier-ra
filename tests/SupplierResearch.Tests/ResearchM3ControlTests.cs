using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SupplierResearch;
using Xunit;

namespace SupplierResearch.Tests;

public sealed class ResearchM3ControlTests
{
    [Fact]
    public async Task PublicDemo_returns_not_found_for_unknown_preset_without_provider_calls()
    {
        var provider = new StubSearchProvider();
        var generator = new StubGenerator();
        var context = CreateContext();

        var result = await ResearchEndpoints.PublicDemoAsync("unknown", context.Request, context,
            CreateWorkflow(provider, generator), new LiveRequestGate(TimeProvider.System), Configuration(true),
            CancellationToken.None);

        Assert.IsType<NotFound>(result);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task PublicDemo_rejects_input_overrides_before_provider_calls()
    {
        var provider = new StubSearchProvider();
        var generator = new StubGenerator();
        var context = CreateContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"supplier\":\"attacker-value\"}"));

        var result = await ResearchEndpoints.PublicDemoAsync("tetra-pak-sustainability", context.Request, context,
            CreateWorkflow(provider, generator), new LiveRequestGate(TimeProvider.System), Configuration(true),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, generator.Calls);

        var queryContext = CreateContext();
        queryContext.Request.QueryString = new QueryString("?supplier=attacker-value");
        var queryResult = await ResearchEndpoints.PublicDemoAsync("tetra-pak-sustainability", queryContext.Request,
            queryContext, CreateWorkflow(provider, generator), new LiveRequestGate(TimeProvider.System), Configuration(true),
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(queryResult).StatusCode);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task PublicDemo_returns_unavailable_when_hosted_live_is_disabled()
    {
        var provider = new StubSearchProvider();
        var generator = new StubGenerator();
        var context = CreateContext();

        var result = await ResearchEndpoints.PublicDemoAsync("tetra-pak-sustainability", context.Request, context,
            CreateWorkflow(provider, generator), new LiveRequestGate(TimeProvider.System), Configuration(false),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task PublicDemo_uses_only_the_server_defined_preset()
    {
        var provider = new StubSearchProvider();
        var generator = new StubGenerator();
        var context = CreateContext();

        var result = await ResearchEndpoints.PublicDemoAsync("tetra-pak-sustainability", context.Request, context,
            CreateWorkflow(provider, generator), new LiveRequestGate(TimeProvider.System), Configuration(true),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("Tetra Pak Find public company and sustainability information", provider.LastQuery);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task ExecuteWithDeadline_maps_timeout_to_504_and_preserves_client_cancellation()
    {
        var context = CreateContext();
        var timeout = await ResearchEndpoints.ExecuteWithDeadlineAsync(context,
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            }, CancellationToken.None, TimeSpan.FromMilliseconds(20));
        Assert.Equal(StatusCodes.Status504GatewayTimeout, Assert.IsAssignableFrom<IStatusCodeHttpResult>(timeout).StatusCode);

        using var requestCancellation = new CancellationTokenSource();
        var cancelled = ResearchEndpoints.ExecuteWithDeadlineAsync(CreateContext(),
            async token =>
            {
                requestCancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            }, requestCancellation.Token, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task LocalResearch_returns_safe_service_error_for_auth_failure()
    {
        var provider = new StubSearchProvider(new ProviderSearchException(ProviderFailureKind.UpstreamHttp,
            HttpStatusCode.Unauthorized, "invalid key must not reach browser"));
        var context = CreateContext();
        var result = await ResearchEndpoints.LocalResearchAsync(
            new ResearchRequest("Northwind", "Find public sustainability information"), context,
            CreateWorkflow(provider, new StubGenerator()), new LiveRequestGate(TimeProvider.System), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Equal("Live search is temporarily unavailable.", problem.ProblemDetails!.Title);
        Assert.DoesNotContain("invalid key", problem.ProblemDetails.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalResearch_maps_search_timeout_to_gateway_timeout()
    {
        var provider = new StubSearchProvider(new ProviderSearchException(ProviderFailureKind.Timeout));
        var context = CreateContext();
        var result = await ResearchEndpoints.LocalResearchAsync(
            new ResearchRequest("Northwind", "Find public sustainability information"), context,
            CreateWorkflow(provider, new StubGenerator()), new LiveRequestGate(TimeProvider.System), CancellationToken.None);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public void LiveRequestGate_rejects_overlap_rate_excess_and_process_lifetime_excess()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
        var gate = new LiveRequestGate(clock);

        Assert.True(gate.TryAcquire(out var active, out _));
        Assert.False(gate.TryAcquire(out _, out var overlap));
        Assert.Equal(LiveAdmissionFailure.ConcurrentRequest, overlap);
        active!.Dispose();

        for (var request = 0; request < 2; request++)
        {
            Assert.True(gate.TryAcquire(out var lease, out _));
            lease!.Dispose();
        }
        Assert.False(gate.TryAcquire(out _, out var rateFailure));
        Assert.Equal(LiveAdmissionFailure.RateLimit, rateFailure);

        for (var request = 3; request < 20; request++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(gate.TryAcquire(out var lease, out _));
            lease!.Dispose();
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(gate.TryAcquire(out _, out var lifetimeFailure));
        Assert.Equal(LiveAdmissionFailure.LifetimeLimit, lifetimeFailure);
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream();
        return context;
    }

    private static IConfiguration Configuration(bool liveEnabled) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Hosting:LiveEnabled"] = liveEnabled.ToString() })
        .Build();

    private static ResearchWorkflow CreateWorkflow(StubSearchProvider provider, StubGenerator generator) =>
        new(new SupplierSearchService(provider), provider, generator, NullLogger<ResearchWorkflow>.Instance);

    private sealed class StubSearchProvider(Exception? failure = null) : ISupplierSearchProvider
    {
        public string Mode => "live";
        public int Calls { get; private set; }
        public string? LastQuery { get; private set; }

        public Task<IReadOnlyList<ProviderSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            Calls++;
            LastQuery = query;
            return failure is null
                ? Task.FromResult<IReadOnlyList<ProviderSearchResult>>(
                    [new("Company", "https://example.test/source", "A small source excerpt for deterministic endpoint testing.")])
                : Task.FromException<IReadOnlyList<ProviderSearchResult>>(failure);
        }
    }

    private sealed class StubGenerator : IResearchGenerator
    {
        public int Calls { get; private set; }
        public Task<GeneratedResearch> GenerateAsync(
            string supplier, string question, IReadOnlyList<ResearchSource> sources, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new GeneratedResearch(new GeneratedAnswer([], []), "test-model", null));
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan amount) => utcNow += amount;
    }
}
