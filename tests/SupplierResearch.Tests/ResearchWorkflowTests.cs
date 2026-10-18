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
    public async Task ResearchAsync_returns_one_clear_supported_claim()
    {
        var generator = new StubGenerator(new GeneratedResearch(new GeneratedAnswer(
            [new GeneratedFinding("The supplier reports recyclable packaging.",
                [new GeneratedEvidence("S1", "recyclable packaging")])], []), "gemini-test", null));

        var result = await CreateWorkflow([Source()], "live", generator)
            .ResearchAsync("Northwind", "Find packaging information", CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("complete", result.Status);
        Assert.Contains("recyclable packaging", finding.Text);
        Assert.Contains(finding.Evidence[0].ExcerptSpan, result.Sources[0].Excerpt);
    }

    [Fact]
    public async Task ResearchAsync_separates_supported_finding_from_missing_context_follow_up()
    {
        var generator = new StubGenerator(new GeneratedResearch(
            new GeneratedAnswer(
                [new GeneratedFinding("The supplier reports recyclable packaging.",
                    [new GeneratedEvidence("S1", "recyclable packaging")])],
                ["What reporting period applies to the recyclable-packaging statement?"]),
            "gemini-test", new ProviderUsage(10, 12, 22)));

        var result = await CreateWorkflow([Source()], "live", generator)
            .ResearchAsync("Northwind", "Find packaging sustainability data", CancellationToken.None);

        Assert.Equal("complete", result.Status);
        Assert.Equal("gemini-test", result.Model);
        Assert.Equal(22, result.Usage!.TotalTokenCount);
        Assert.Equal("S1", Assert.Single(Assert.Single(result.Findings).Evidence).SourceId);
        Assert.Equal("What reporting period applies to the recyclable-packaging statement?", Assert.Single(result.FollowUpQuestions));
    }

    [Fact]
    public async Task ResearchAsync_returns_multiple_separately_supported_claims()
    {
        ProviderSearchResult[] providerResults =
        [
            new("Materials", "https://example.test/materials", "The supplier reports that its cartons use plant-based polymers."),
            new("Emissions", "https://example.test/emissions", "The supplier states that factory energy use fell by 12 percent last year.")
        ];
        var generator = new StubGenerator(new GeneratedResearch(
            new GeneratedAnswer(
            [
                new GeneratedFinding("The cartons use plant-based polymers.",
                    [new GeneratedEvidence("S1", "cartons use plant-based polymers")]),
                new GeneratedFinding("Factory energy use fell by 12 percent last year.",
                    [new GeneratedEvidence("S2", "factory energy use fell by 12 percent last year")])
            ], []), "gemini-test", null));

        var result = await CreateWorkflow(providerResults, "live", generator)
            .ResearchAsync("Northwind", "Find materials and energy information", CancellationToken.None);

        Assert.Equal("complete", result.Status);
        Assert.Equal(2, result.Findings.Count);
        Assert.Equal("S1", Assert.Single(result.Findings[0].Evidence).SourceId);
        Assert.Contains("plant-based polymers", result.Sources[0].Excerpt);
        Assert.Equal("S2", Assert.Single(result.Findings[1].Evidence).SourceId);
        Assert.Contains("12 percent last year", result.Sources[1].Excerpt);
    }

    [Fact]
    public async Task ResearchAsync_abstains_when_sources_do_not_support_a_finding()
    {
        var generator = new StubGenerator(new GeneratedResearch(new GeneratedAnswer([], []), "gemini-test", null));
        var result = await CreateWorkflow([Source()], "live", generator)
            .ResearchAsync("Northwind", "Find a verified facility count", CancellationToken.None);

        Assert.Equal("insufficient_evidence", result.Status);
        Assert.Empty(result.Findings);
        Assert.Single(result.Sources);
    }

    [Fact]
    public async Task ResearchAsync_keeps_conflicting_claims_separately_attributed()
    {
        ProviderSearchResult[] providerResults =
        [
            new("Supplier statement", "https://example.test/supplier", "The supplier reports a 30 percent reduction in packaging waste."),
            new("Independent review", "https://example.test/review", "The review says no packaging-waste reduction was measured for the same period.")
        ];
        var generator = new StubGenerator(new GeneratedResearch(new GeneratedAnswer(
        [
            new GeneratedFinding("The supplier reports a 30 percent packaging-waste reduction.",
                [new GeneratedEvidence("S1", "30 percent reduction in packaging waste")]),
            new GeneratedFinding("A review says no reduction was measured; these sources conflict.",
                [new GeneratedEvidence("S2", "no packaging-waste reduction was measured for the same period")])
        ], []), "gemini-test", null));

        var result = await CreateWorkflow(providerResults, "live", generator)
            .ResearchAsync("Northwind", "Compare packaging-waste claims", CancellationToken.None);

        Assert.Equal("complete", result.Status);
        Assert.Equal(2, result.Findings.Count);
        Assert.Equal("S1", Assert.Single(result.Findings[0].Evidence).SourceId);
        Assert.Equal("S2", Assert.Single(result.Findings[1].Evidence).SourceId);
        Assert.Contains("supplier reports", result.Findings[0].Text);
        Assert.Contains("sources conflict", result.Findings[1].Text);
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
    public async Task ResearchAsync_rejects_full_source_excerpt_when_it_exceeds_span_limit()
    {
        var fullExcerpt = new string('a', 301);
        var generator = new StubGenerator(new GeneratedResearch(
            new GeneratedAnswer(
                [new GeneratedFinding("Supported statement", [new GeneratedEvidence("S1", fullExcerpt)])], []),
            "gemini-test", null));
        var result = await CreateWorkflow([new("Company", "https://example.test/source", fullExcerpt)], "live", generator)
            .ResearchAsync("Northwind", "Find supporting information", CancellationToken.None);

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
    public async Task ResearchAsync_returns_sources_only_when_generation_times_out()
    {
        var generator = new StubGenerator(new ResearchGenerationException(GenerationFailureKind.Timeout));
        var result = await CreateWorkflow([Source()], "live", generator)
            .ResearchAsync("Northwind", "Find packaging information", CancellationToken.None);

        Assert.Equal("sources_only", result.Status);
        Assert.Empty(result.Findings);
        Assert.Single(result.Sources);
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

        var source = new ResearchSource("S3", "Packaging recycling", "https://example.test/s3",
            "Our cartons are recyclable where collection, sorting and recycling infrastructure exists at scale. By increasing the fibre content and reducing the plastics and aluminium, our packaging becomes more attractive to paper mills and fibre recycles, and easier to recycle.",
            "provider_excerpt");
        var response = await generator.GenerateAsync("Tetra Pak", "Research packaging recycling practices", [source], CancellationToken.None);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("generativelanguage.googleapis.com", uri!.Host);
        Assert.EndsWith("/v1beta/models/gemini-test:generateContent", uri.AbsolutePath);
        Assert.Equal("unit-test-key", apiKey);
        Assert.Equal("application/json", contentType);
        using var payload = JsonDocument.Parse(requestBody!);
        Assert.Equal("application/json", payload.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
        Assert.True(payload.RootElement.GetProperty("generationConfig").TryGetProperty("responseSchema", out _));
        var schema = payload.RootElement.GetProperty("generationConfig").GetProperty("responseSchema");
        var excerptDescription = schema.GetProperty("properties").GetProperty("findings").GetProperty("items")
            .GetProperty("properties").GetProperty("evidence").GetProperty("items")
            .GetProperty("properties").GetProperty("excerptSpan").GetProperty("description").GetString();
        Assert.Contains("maximum 200 characters", excerptDescription);
        var instruction = payload.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0]
            .GetProperty("text").GetString();
        Assert.Contains("never copy the full source excerpt", instruction);
        Assert.Contains("Preserve each source's qualifications and scope", instruction);
        Assert.Contains("do not add absolute or exclusive wording such as 'only'", instruction);
        Assert.Contains("If excerpts conflict, report the claims separately", instruction);
        Assert.Contains("cross-document synthesis finding", instruction);
        Assert.Contains("never choose an overall winner without comparable evidence", instruction);
        Assert.Contains("cite each supporting passage in a separate evidence item", instruction);
        Assert.Contains("never combine text from different sources into one fabricated quote", instruction);
        using var generationInput = JsonDocument.Parse(payload.RootElement.GetProperty("contents")[0]
            .GetProperty("parts")[0].GetProperty("text").GetString()!);
        Assert.Equal("S3", generationInput.RootElement.GetProperty("sources")[0].GetProperty("sourceId").GetString());
        Assert.Contains("recyclable where collection, sorting and recycling infrastructure exists at scale",
            generationInput.RootElement.GetProperty("sources")[0].GetProperty("excerpt").GetString());
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
