using SupplierResearch;
using Xunit;

namespace SupplierResearch.Tests;

public sealed class SupplierSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_filters_invalid_and_duplicate_urls_and_bounds_sources()
    {
        var provider = new StubSearchProvider(
        [
            new("One", "https://example.com/one", new string('a', 2_500)),
            new("Duplicate", "https://example.com/one", "duplicate"),
            new("Invalid scheme", "javascript:alert(1)", "unsafe"),
            new("URL credentials", "https://user:pass@example.com/", "unsafe"),
            new("Two", "https://example.com/two", "usable"),
            new("Three", "https://example.com/three", "usable"),
            new("Four", "https://example.com/four", "usable"),
            new("Five", "https://example.com/five", "usable"),
            new("Six", "https://example.com/six", "over limit")
        ]);
        var service = new SupplierSearchService(provider);

        var sources = await service.SearchAsync("Northwind", "Find public sustainability data", CancellationToken.None);

        Assert.Equal(5, sources.Count);
        Assert.Equal(2_000, sources[0].Excerpt.Length);
        Assert.Equal("S1", sources[0].Id);
        Assert.Equal("S5", sources[^1].Id);
        Assert.All(sources, source => Assert.Equal("provider_excerpt", source.Provenance));
        Assert.DoesNotContain(sources, source => source.Url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sources, source => new Uri(source.Url).UserInfo.Length != 0);
    }

    [Fact]
    public async Task SearchAsync_preserves_cancellation_for_provider()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new SupplierSearchService(new StubSearchProvider([]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchAsync("Northwind", "Find public sustainability data", cancellation.Token));
    }

    private sealed class StubSearchProvider(IReadOnlyList<ProviderSearchResult> results) : ISupplierSearchProvider
    {
        public string Mode => "live";

        public Task<IReadOnlyList<ProviderSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(results);
        }
    }
}
