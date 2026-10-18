using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SupplierResearch;

public sealed record ResearchRequest(string? Supplier, string? Question);
public sealed record ResearchSource(string Id, string Title, string Url, string Excerpt, string Provenance);
public sealed record ProviderSearchResult(string Title, string Url, string Content);

public enum ProviderFailureKind
{
    UpstreamHttp,
    Transport,
    Timeout,
    Cancelled
}

public sealed class ProviderSearchException(
    ProviderFailureKind kind,
    HttpStatusCode? statusCode = null,
    string? providerError = null) : Exception("Search provider request failed.")
{
    public ProviderFailureKind Kind { get; } = kind;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public string? ProviderError { get; } = providerError;
}

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

    public string Mode => provider.Mode;

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

public sealed class TavilySearchProvider(
    HttpClient client,
    IConfiguration configuration,
    ILogger<TavilySearchProvider> logger) : ISupplierSearchProvider
{
    private const int MaxErrorBodyBytes = 4_096;
    private const int MaxProviderErrorLength = 240;

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

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Search provider request cancelled: {FailureKind}", ProviderFailureKind.Cancelled);
            throw new ProviderSearchException(ProviderFailureKind.Cancelled);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Search provider request timed out: {FailureKind} {ExceptionType}",
                ProviderFailureKind.Timeout, "OperationCanceledException");
            throw new ProviderSearchException(ProviderFailureKind.Timeout, providerError: "Request timed out.");
        }
        catch (HttpRequestException ex)
        {
            var safeError = SanitizeProviderError(ex.GetBaseException().Message, configuration["TAVILY_API_KEY"]);
            logger.LogError("Search provider transport failed: {FailureKind} {ExceptionType} {TransportError}",
                ProviderFailureKind.Transport, ex.GetType().Name, safeError ?? "(no safe detail)");
            throw new ProviderSearchException(ProviderFailureKind.Transport, providerError: safeError);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var providerError = await ReadProviderErrorAsync(response, configuration["TAVILY_API_KEY"], cancellationToken);
                logger.LogWarning("Search provider returned HTTP {StatusCode}: {FailureKind} {ProviderError}",
                    (int)response.StatusCode, ProviderFailureKind.UpstreamHttp, providerError ?? "(no safe error detail)");
                throw new ProviderSearchException(ProviderFailureKind.UpstreamHttp, response.StatusCode, providerError);
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

    private static async Task<string?> ReadProviderErrorAsync(
        HttpResponseMessage response, string? apiKey, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxErrorBodyBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            var message = FindErrorMessage(document.RootElement);
            return SanitizeProviderError(message, apiKey);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindErrorMessage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "detail", "message", "error" })
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Object && FindErrorMessage(value) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static string? SanitizeProviderError(string? message, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var sanitized = message;
        if (!string.IsNullOrEmpty(apiKey))
        {
            sanitized = sanitized.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        }

        sanitized = Regex.Replace(sanitized, "(?i)Bearer\\s+\\S+", "Bearer [redacted]");
        sanitized = Regex.Replace(sanitized, "(?i)(api[_ -]?key|token|authorization)\\s*[:=]\\s*[^\\s,;]+", "$1=[redacted]");
        sanitized = Regex.Replace(sanitized, "https?://\\S+", "[url]");
        sanitized = Regex.Replace(sanitized, "[\\r\\n\\t]+", " ").Trim();
        return sanitized.Length <= MaxProviderErrorLength
            ? sanitized
            : sanitized[..MaxProviderErrorLength];
    }
}
