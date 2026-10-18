using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SupplierResearch;

public sealed record ResearchRequest(string? Supplier, string? Question);
public sealed record ResearchResponse(string Supplier, string Question, string Mode, IReadOnlyList<ResearchSource> Sources);
public sealed record ResearchSource(string Id, string Title, string Url, string Excerpt, string Provenance);
public sealed record ProviderSearchResult(string Title, string Url, string Content);

public interface ISupplierSearchProvider
{
    string Mode { get; }
    Task<IReadOnlyList<ProviderSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
}

public sealed class SupplierSearchService(ISupplierSearchProvider provider)
{
    private const int MaxSources = 5;
    private const int MaxExcerptLength = 2_000;
    private const int MaxTotalExcerptLength = 10_000;

    public async Task<IReadOnlyList<ResearchSource>> SearchAsync(
        string supplier, string question, CancellationToken cancellationToken)
    {
        var results = await provider.SearchAsync($"{supplier} {question}", cancellationToken);
        var sources = new List<ResearchSource>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalLength = 0;

        foreach (var result in results)
        {
            if (sources.Count == MaxSources || totalLength == MaxTotalExcerptLength)
            {
                break;
            }

            if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.UserInfo.Length != 0 ||
                string.IsNullOrWhiteSpace(result.Title) || string.IsNullOrWhiteSpace(result.Content) ||
                !seenUrls.Add(uri.AbsoluteUri))
            {
                continue;
            }

            var excerpt = result.Content.Trim();
            var remaining = Math.Min(MaxExcerptLength, MaxTotalExcerptLength - totalLength);
            if (excerpt.Length > remaining)
            {
                excerpt = excerpt[..remaining];
            }

            if (excerpt.Length == 0)
            {
                continue;
            }

            var id = $"S{sources.Count + 1}";
            var provenance = provider.Mode == "live" ? "provider_excerpt" : "test_fixture";
            sources.Add(new ResearchSource(id, result.Title.Trim(), uri.AbsoluteUri, excerpt, provenance));
            totalLength += excerpt.Length;
        }

        return sources;
    }
}

public sealed class FixtureSearchProvider : ISupplierSearchProvider
{
    public string Mode => "fixture";

    public Task<IReadOnlyList<ProviderSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ProviderSearchResult> results =
        [
            new("Fixture result: Northwind Packaging", "https://example.test/northwind", "TEST FIXTURE ONLY. No real supplier research was performed."),
            new("Fixture result: Contoso Materials", "https://example.test/contoso", "TEST FIXTURE ONLY. This placeholder is not a verified supplier claim.")
        ];
        return Task.FromResult(results);
    }
}

public sealed class TavilySearchProvider(HttpClient client, IConfiguration configuration) : ISupplierSearchProvider
{
    public string Mode => "live";

    public async Task<IReadOnlyList<ProviderSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["TAVILY_API_KEY"]);
        request.Content = JsonContent.Create(new
        {
            query,
            search_depth = "basic",
            max_results = 8,
            chunks_per_source = 1,
            include_answer = false,
            include_raw_content = false,
            include_images = false,
            auto_parameters = false
        });

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Tavily search failed.", null, response.StatusCode);
        }

        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!body.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            throw new HttpRequestException("Tavily returned an invalid response.", null, HttpStatusCode.BadGateway);
        }

        var parsed = new List<ProviderSearchResult>();
        foreach (var result in results.EnumerateArray())
        {
            var title = result.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String
                ? titleValue.GetString() : null;
            var url = result.TryGetProperty("url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String
                ? urlValue.GetString() : null;
            var content = result.TryGetProperty("content", out var contentValue) && contentValue.ValueKind == JsonValueKind.String
                ? contentValue.GetString() : null;
            if (title is not null && url is not null && content is not null)
            {
                parsed.Add(new ProviderSearchResult(title, url, content));
            }
        }

        return parsed;
    }
}
