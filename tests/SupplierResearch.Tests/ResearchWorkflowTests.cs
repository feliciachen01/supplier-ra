using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SupplierResearch;
using Xunit;

namespace SupplierResearch.Tests;

public sealed class ResearchWorkflowTests
{
    [Fact]
    public async Task ResearchAsync_returns_findings_with_matching_source_spans()
    {
        var generator = new StubGenerator(new GeneratedResearch(
            new GeneratedAnswer(
                [new GeneratedFinding("The supplier reports recyclable packaging.",
                    [new GeneratedEvidence("S1", "recyclable packaging")])],
                ["Which packaging lines use recycled materials?"]),
            "gemini-test", new ProviderUsage(10, 12, 22)));

        var result = await CreateWorkflow([Source()], "live", generator)
            .ResearchAsync("Northwind", "Find packaging sustainability data", CancellationToken.None);

        Assert.Equal("complete", result.Status);
        Assert.Equal("gemini-test", result.Model);
        Assert.Equal(22, result.Usage!.TotalTokenCount);
        Assert.Equal("S1", Assert.Single(Assert.Single(result.Findings).Evidence).SourceId);
        Assert.Equal("Which packaging lines use recycled materials?", Assert.Single(result.FollowUpQuestions));
    }

    [Fact]
    public async Task ResearchAsync_rejects_unmatched_evidence_and_returns_sources_only()
    {
        var generator = new StubGenerator(new GeneratedResearch(
            new GeneratedAnswer(
                [new GeneratedFinding("Unsupported claim", [new GeneratedEvidence("S9", "not in source")])], []),
            "gemini-test", null));

        var result = await CreateWorkflow([Source()], "live", generator)
            .ResearchAsync("Northwind", "Find packaging sustainability data", CancellationToken.None);

        Assert.Equal("sources_only", result.Status);
        Assert.Empty(result.Findings);
        Assert.Single(result.Sources);
        Assert.Contains("failed evidence validation", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task ResearchAsync_falls_back_to_sources_when_generator_fails()
    {
        var generator = new StubGenerator(new ResearchGenerationException(GenerationFailureKind.UpstreamHttp,
            HttpStatusCode.TooManyRequests));
        var result = await CreateWorkflow([Source()], "live", generator)
            .ResearchAsync("Northwind", "Find packaging sustainability data", CancellationToken.None);

        Assert.Equal("sources_only", result.Status);
        Assert.Empty(result.Findings);
        Assert.Single(result.Sources);
        Assert.Null(result.Model);
    }

    [Fact]
    public async Task ResearchAsync_marks_fixture_and_does_not_call_generator()
    {
        var generator = new StubGenerator(new InvalidOperationException("must not run"));
        var result = await CreateWorkflow([Source()], "fixture", generator)
            .ResearchAsync("Northwind", "Find packaging sustainability data", CancellationToken.None);

        Assert.Equal("fixture", result.Mode);
        Assert.Equal("sources_only", result.Status);
        Assert.Empty(result.Findings);
        Assert.Equal(0, generator.Calls);
        Assert.Contains("TEST FIXTURE", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task ResearchAsync_does_not_generate_without_sources()
    {
        var generator = new StubGenerator(new InvalidOperationException("must not run"));
        var result = await CreateWorkflow([], "live", generator)
            .ResearchAsync("Northwind", "Find packaging sustainability data", CancellationToken.None);

        Assert.Equal("insufficient_evidence", result.Status);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task GeminiGenerator_uses_structured_post_and_reads_model_and_usage()
    {
        HttpMethod? method = null;
        Uri? uri = null;
        string? apiKey = null;
        string? contentType = null;
        string? requestBody = null;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            method = request.Method;
            uri = request.RequestUri;
            apiKey = request.Headers.GetValues("x-goog-api-key").Single();
            contentType = request.Content!.Headers.ContentType!.MediaType;
            requestBody = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"modelVersion":"gemini-test-version","usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":7,"totalTokenCount":12},"candidates":[{"content":{"parts":[{"text":"{\"findings\":[],\"followUpQuestions\":[]}"}]}}]}
                    """)
            });
        }));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GEMINI_API_KEY"] = "unit-test-key",
            ["Generation:Model"] = "gemini-test"
        }).Build();
        var generator = new GeminiResearchGenerator(client, configuration, NullLogger<GeminiResearchGenerator>.Instance);

        var response = await generator.GenerateAsync("Northwind", "Find public information", [], CancellationToken.None);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("generativelanguage.googleapis.com", uri!.Host);
        Assert.EndsWith("/v1beta/models/gemini-test:generateContent", uri.AbsolutePath);
        Assert.Equal("unit-test-key", apiKey);
        Assert.Equal("application/json", contentType);
        using var payload = JsonDocument.Parse(requestBody!);
        Assert.Equal("application/json", payload.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
        Assert.True(payload.RootElement.GetProperty("generationConfig").TryGetProperty("responseSchema", out _));
        Assert.Equal("gemini-test-version", response.Model);
        Assert.Equal(12, response.Usage!.TotalTokenCount);
    }

    private static ResearchWorkflow CreateWorkflow(
        IReadOnlyList<ProviderSearchResult> results, string mode, StubGenerator generator)
    {
        var provider = new StubSearchProvider(results, mode);
        return new ResearchWorkflow(new SupplierSearchService(provider), provider, generator,
            NullLogger<ResearchWorkflow>.Instance);
    }

    private static ProviderSearchResult Source() => new(
        "Company", "https://example.test/source", "The supplier reports recyclable packaging across its product lines.");

    private sealed class StubSearchProvider(IReadOnlyList<ProviderSearchResult> results, string mode) : ISupplierSearchProvider
    {
        public string Mode => mode;
        public Task<IReadOnlyList<ProviderSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult(results);
    }

    private sealed class StubGenerator : IResearchGenerator
    {
        private readonly GeneratedResearch? result;
        private readonly Exception? failure;
        public int Calls { get; private set; }

        public StubGenerator(GeneratedResearch result) => this.result = result;
        public StubGenerator(Exception failure) => this.failure = failure;

        public Task<GeneratedResearch> GenerateAsync(
            string supplier, string question, IReadOnlyList<ResearchSource> sources, CancellationToken cancellationToken)
        {
            Calls++;
            return failure is not null
                ? Task.FromException<GeneratedResearch>(failure)
                : Task.FromResult(result!);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
