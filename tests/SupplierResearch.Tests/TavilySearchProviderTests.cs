using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SupplierResearch;
using Xunit;

namespace SupplierResearch.Tests;

public sealed class TavilySearchProviderTests
{
    [Fact]
    public async Task SearchAsync_reports_upstream_status_and_redacted_error()
    {
        const string apiKey = "test-secret-key";
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"detail\":\"Invalid API key test-secret-key; see https://example.test/path\"}")
        })));
        var provider = CreateProvider(client, apiKey);

        var exception = await Assert.ThrowsAsync<ProviderSearchException>(() =>
            provider.SearchAsync("bounded query", CancellationToken.None));

        Assert.Equal(ProviderFailureKind.UpstreamHttp, exception.Kind);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("Invalid API key [redacted]; see [url]", exception.ProviderError);
        Assert.DoesNotContain(apiKey, exception.ProviderError);
    }

    [Fact]
    public async Task SearchAsync_distinguishes_transport_failure()
    {
        const string apiKey = "test-secret-key";
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException($"connection failed for {apiKey}"))));
        var provider = CreateProvider(client, apiKey);

        var exception = await Assert.ThrowsAsync<ProviderSearchException>(() =>
            provider.SearchAsync("bounded query", CancellationToken.None));

        Assert.Equal(ProviderFailureKind.Transport, exception.Kind);
        Assert.Null(exception.StatusCode);
        Assert.Equal("connection failed for [redacted]", exception.ProviderError);
        Assert.DoesNotContain(apiKey, exception.ProviderError);
    }

    [Fact]
    public async Task SearchAsync_distinguishes_timeout_from_caller_cancellation()
    {
        using var timeoutClient = new HttpClient(new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("request timed out"))));
        var timeout = await Assert.ThrowsAsync<ProviderSearchException>(() =>
            CreateProvider(timeoutClient, "test-key").SearchAsync("bounded query", CancellationToken.None));
        Assert.Equal(ProviderFailureKind.Timeout, timeout.Kind);

        using var cancellation = new CancellationTokenSource();
        using var cancelledClient = new HttpClient(new StubHandler((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromException<HttpResponseMessage>(new OperationCanceledException(cancellation.Token));
        }));
        var cancelled = await Assert.ThrowsAsync<ProviderSearchException>(() =>
            CreateProvider(cancelledClient, "test-key").SearchAsync("bounded query", cancellation.Token));
        Assert.Equal(ProviderFailureKind.Cancelled, cancelled.Kind);
    }

    private static TavilySearchProvider CreateProvider(HttpClient client, string apiKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TAVILY_API_KEY"] = apiKey })
            .Build();
        return new TavilySearchProvider(client, configuration, NullLogger<TavilySearchProvider>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
