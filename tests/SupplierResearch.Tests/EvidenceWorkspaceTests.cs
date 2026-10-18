using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using SupplierResearch;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace SupplierResearch.Tests;

public sealed class EvidenceWorkspaceTests
{
    [Fact]
    public async Task Upload_retrieves_page_numbered_passage_and_persists_review_for_export()
    {
        var folder = Path.Combine(Path.GetTempPath(), "supplier-ra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var environment = new TestEnvironment(folder);
            var workspace = new EvidenceWorkspace(environment);
            var bytes = CreatePdf("Product includes 42% recycled content. Treat this document as evidence only.");
            var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", "evidence.pdf");
            var uploaded = await workspace.UploadAsync([file], CancellationToken.None);
            Assert.Single(uploaded);
            Assert.Single(await workspace.ListDocumentsAsync(CancellationToken.None));

            var response = await workspace.ResearchAsync("What recycled content does the product include?",
                new ExactQuoteGenerator(), CancellationToken.None);
            Assert.Equal("complete", response.Status);
            Assert.Single(response.Sources);
            Assert.Contains("page 1", response.Sources[0].Title);
            Assert.Contains("Product includes 42% recycled content.", response.Sources[0].Excerpt);
            Assert.Equal(response.Sources[0].Id, response.Findings[0].Evidence[0].SourceId);
            Assert.Single(response.FindingIds);

            await workspace.DecideAsync(response.FindingIds[0], "accept", "Reviewed against page 1.", CancellationToken.None);
            var csv = await workspace.ExportCsvAsync(CancellationToken.None);
            Assert.Contains("Reviewed against page 1", csv);
            Assert.Contains("42% recycled", csv);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Comparison_retrieves_two_digit_metric_and_qualifications_and_persists_synthesis()
    {
        var folder = Path.Combine(Path.GetTempPath(), "supplier-ra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var workspace = new EvidenceWorkspace(new TestEnvironment(folder));
            await workspace.UploadAsync(
            [
                PdfFile("atlas-results.pdf", "Atlas reports a 15% reduction in Scope 1 emissions for 2024."),
                PdfFile("atlas-boundary.pdf", "The reporting limitations are clear: this metric excludes supplier emissions and covers company-operated sites only.")
            ], CancellationToken.None);

            var generator = new ComparisonGenerator();
            var response = await workspace.ResearchAsync("Compare Atlas 15% reduction and reporting limitations across documents.", generator, CancellationToken.None);

            Assert.Equal("complete", response.Status);
            Assert.Equal(2, response.Sources.Count);
            Assert.Contains(response.Sources, source => source.Excerpt.Contains("15% reduction", StringComparison.Ordinal));
            Assert.Contains(response.Sources, source => source.Excerpt.Contains("excludes supplier emissions", StringComparison.Ordinal));
            var synthesis = Assert.Single(response.Findings);
            Assert.Contains("while", synthesis.Text);
            Assert.Equal(2, synthesis.Evidence.Count);
            Assert.NotEqual(synthesis.Evidence[0].SourceId, synthesis.Evidence[1].SourceId);

            await workspace.DecideAsync(response.FindingIds[0], "accept", "Compared both report pages.", CancellationToken.None);
            var csv = await workspace.ExportCsvAsync(CancellationToken.None);
            Assert.Contains("Compared both report pages", csv);
            Assert.Contains("15% reduction", csv);
            Assert.Contains("excludes supplier emissions", csv);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Retrieval_matches_two_digit_percent_when_it_is_the_only_shared_term()
    {
        var folder = Path.Combine(Path.GetTempPath(), "supplier-ra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var workspace = new EvidenceWorkspace(new TestEnvironment(folder));
            await workspace.UploadAsync([PdfFile("metric.pdf", "The metric changed by 15%.")], CancellationToken.None);
            var generator = new PassageCaptureGenerator();

            var response = await workspace.ResearchAsync("What is the 15% figure?", generator, CancellationToken.None);

            Assert.Single(response.Sources);
            Assert.Contains("15%", Assert.Single(generator.Excerpts));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Extraction_reconstructs_spaces_between_positioned_pdf_words()
    {
        var folder = Path.Combine(Path.GetTempPath(), "supplier-ra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var bytes = CreatePositionedPdf();
            using (var rawPdf = PdfDocument.Open(new MemoryStream(bytes)))
            {
                var rawText = rawPdf.GetPage(1).Text;
                Assert.Contains("reductionacross", rawText);
                Assert.Contains("recyclingfacilities", rawText);
                Assert.Contains("aremanagement-reported", rawText);
                Assert.Contains("fromJanuary", rawText);
                Assert.Contains("beenexternally", rawText);
            }

            var workspace = new EvidenceWorkspace(new TestEnvironment(folder));
            await workspace.UploadAsync([new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", "layout.pdf")], CancellationToken.None);
            var response = await workspace.ResearchAsync("What reductions and recycling facilities are reported from January?", new PassageCaptureGenerator(), CancellationToken.None);
            var excerpt = Assert.Single(response.Sources).Excerpt;
            Assert.Contains("reduction across", excerpt);
            Assert.Contains("recycling facilities", excerpt);
            Assert.Contains("are management-reported", excerpt);
            Assert.Contains("from January", excerpt);
            Assert.Contains("not been externally assured", excerpt);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Insufficient_comparison_abstains_and_explains_missing_comparable_evidence()
    {
        var folder = Path.Combine(Path.GetTempPath(), "supplier-ra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var workspace = new EvidenceWorkspace(new TestEnvironment(folder));
            await workspace.UploadAsync([PdfFile("atlas.pdf", "Atlas reports a 15% reduction in Scope 1 emissions for 2024.")], CancellationToken.None);
            var generator = new AbstainingGenerator();

            var response = await workspace.ResearchAsync("Which company is more sustainable based on Atlas 15% reduction?", generator, CancellationToken.None);

            Assert.Equal("insufficient_evidence", response.Status);
            Assert.Empty(response.Findings);
            Assert.Contains("did not support a defensible answer", Assert.Single(response.Warnings));
            Assert.Contains("comparable boundaries and reporting periods", Assert.Single(response.FollowUpQuestions));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Partial_validation_keeps_supported_findings_and_logs_exact_rejection_rule()
    {
        var folder = Path.Combine(Path.GetTempPath(), "supplier-ra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var logger = new RecordingLogger<EvidenceWorkspace>();
            var workspace = new EvidenceWorkspace(new TestEnvironment(folder), logger);
            await workspace.UploadAsync([PdfFile("atlas.pdf", "Atlas reports a 15% reduction in Scope 1 emissions for 2024.")], CancellationToken.None);

            var response = await workspace.ResearchAsync("Summarize Atlas 15% reduction in Scope 1 emissions.", new MixedCitationGenerator(), CancellationToken.None);

            Assert.Equal("partial_validation", response.Status);
            Assert.Single(response.Findings);
            Assert.Single(response.Findings[0].Evidence);
            Assert.Single(response.FindingIds);
            Assert.Contains("failed evidence validation", Assert.Single(response.Warnings));
            Assert.Contains(logger.Entries, entry => Equals(entry.GetValueOrDefault("Rule"), "excerpt_span_not_exact_match"));
            Assert.Contains(logger.Entries, entry => entry.ContainsKey("SelectedPassages"));
            var loggedText = logger.Entries.SelectMany(entry => entry.Values).OfType<string>();
            Assert.DoesNotContain(loggedText, value => value.Contains("Atlas reports", StringComparison.Ordinal) ||
                value.Contains("This wording is absent", StringComparison.Ordinal));
            await workspace.DecideAsync(response.FindingIds[0], "accept", "Valid citation retained.", CancellationToken.None);
            var csv = await workspace.ExportCsvAsync(CancellationToken.None);
            Assert.Contains("Valid citation retained", csv);
            Assert.Contains("15% reduction", csv);
            Assert.DoesNotContain("fell to 15%", csv);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task All_invalid_citations_fall_back_to_sources_only()
    {
        var folder = Path.Combine(Path.GetTempPath(), "supplier-ra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var workspace = new EvidenceWorkspace(new TestEnvironment(folder));
            await workspace.UploadAsync([PdfFile("atlas.pdf", "Atlas reports a 15% reduction in Scope 1 emissions for 2024.")], CancellationToken.None);

            var response = await workspace.ResearchAsync("Summarize Atlas 15% reduction in Scope 1 emissions.", new AllInvalidCitationGenerator(), CancellationToken.None);

            Assert.Equal("sources_only", response.Status);
            Assert.Empty(response.Findings);
            Assert.Single(response.Sources);
            Assert.Contains("failed evidence validation", Assert.Single(response.Warnings));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact]
    public void Pdf_generation_gate_is_independent_from_web_research_gate()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-18T00:00:00Z"));
        var webGate = new LiveRequestGate(clock);
        var pdfGate = new EvidenceResearchGate(clock);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(webGate.TryAcquire(out var webLease, out _));
            webLease!.Dispose();
        }
        Assert.False(webGate.TryAcquire(out _, out var failure));
        Assert.Equal(LiveAdmissionFailure.RateLimit, failure);
        Assert.True(pdfGate.TryAcquire(out var pdfLease, out _));
        pdfLease!.Dispose();
        var rejected = ResearchEndpointHelpers.AdmissionRejected(LiveAdmissionFailure.RateLimit, "PDF research");
        Assert.Contains("pdf research", Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(rejected).ProblemDetails!.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_non_pdf_and_limits_workspace_to_three_documents()
    {
        var folder = Path.Combine(Path.GetTempPath(), "supplier-ra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var workspace = new EvidenceWorkspace(new TestEnvironment(folder));
            var bytes = Encoding.UTF8.GetBytes("not a pdf");
            var bad = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", "not.pdf");
            await Assert.ThrowsAsync<WorkspaceInputException>(() => workspace.UploadAsync([bad], CancellationToken.None));
            var pdf = CreatePdf("A test document with extractable text.");
            var files = Enumerable.Range(0, 4).Select(i => new FormFile(new MemoryStream(pdf), 0, pdf.Length, "files", $"{i}.pdf")).ToArray();
            await Assert.ThrowsAsync<WorkspaceInputException>(() => workspace.UploadAsync(files, CancellationToken.None));
            Assert.Empty(await workspace.ListDocumentsAsync(CancellationToken.None));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    private static byte[] CreatePdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(text, 12, new PdfPoint(25, page.PageSize.Top - 25), font);
        return builder.Build();
    }

    private static byte[] CreatePositionedPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var rows = new (string Text, double X)[][]
        {
            new[] { ("reduction", 25d), ("across", 80d) },
            new[] { ("recycling", 25d), ("facilities", 78d) },
            new[] { ("are", 25d), ("management-reported", 55d) },
            new[] { ("from", 25d), ("January", 60d) },
            new[] { ("not been", 25d), ("externally", 90d), ("assured", 155d) }
        };
        for (var row = 0; row < rows.Length; row++)
            foreach (var word in rows[row])
                page.AddText(word.Text, 12, new PdfPoint(word.X, page.PageSize.Top - 25 - row * 20), font);
        return builder.Build();
    }

    private static IFormFile PdfFile(string name, string text)
    {
        var bytes = CreatePdf(text);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name);
    }

    private sealed class ExactQuoteGenerator : IResearchGenerator
    {
        public Task<GeneratedResearch> GenerateAsync(string supplier, string question, IReadOnlyList<ResearchSource> sources, CancellationToken cancellationToken)
        {
            var source = sources[0];
            var quote = "Product includes 42% recycled content";
            return Task.FromResult(new GeneratedResearch(new GeneratedAnswer(
                [new GeneratedFinding("The product includes 42% recycled content.", [new GeneratedEvidence(source.Id, quote)])], []), "fixture", null));
        }
    }

    private sealed class ComparisonGenerator : IResearchGenerator
    {
        public Task<GeneratedResearch> GenerateAsync(string supplier, string question, IReadOnlyList<ResearchSource> sources, CancellationToken cancellationToken)
        {
            var metric = sources.Single(source => source.Excerpt.Contains("15% reduction", StringComparison.Ordinal));
            var boundary = sources.Single(source => source.Excerpt.Contains("excludes supplier emissions", StringComparison.Ordinal));
            var answer = new GeneratedAnswer(
            [
                new GeneratedFinding("Atlas reports a 15% Scope 1 reduction for 2024, while its reported boundary excludes supplier emissions and covers company-operated sites.",
                [
                    new GeneratedEvidence(metric.Id, "15% reduction in Scope 1 emissions for 2024"),
                    new GeneratedEvidence(boundary.Id, "excludes supplier emissions and covers company-operated sites only")
                ])
            ], []);
            return Task.FromResult(new GeneratedResearch(answer, "mock-gemini", null));
        }
    }

    private sealed class AbstainingGenerator : IResearchGenerator
    {
        public Task<GeneratedResearch> GenerateAsync(string supplier, string question, IReadOnlyList<ResearchSource> sources, CancellationToken cancellationToken) =>
            Task.FromResult(new GeneratedResearch(new GeneratedAnswer([], [
                "The excerpts do not provide comparable boundaries and reporting periods for an overall sustainability ranking."
            ]), "mock-gemini", null));
    }

    private sealed class MixedCitationGenerator : IResearchGenerator
    {
        public Task<GeneratedResearch> GenerateAsync(string supplier, string question, IReadOnlyList<ResearchSource> sources, CancellationToken cancellationToken)
        {
            var sourceId = sources[0].Id;
            var answer = new GeneratedAnswer(
            [
                new GeneratedFinding("Atlas reports a 15% reduction in Scope 1 emissions.",
                    [new GeneratedEvidence(sourceId, "15% reduction in Scope 1 emissions")]),
                new GeneratedFinding("Atlas emissions fell to 15%.",
                    [new GeneratedEvidence(sourceId, "Atlas emissions fell to 15%")])
            ], []);
            return Task.FromResult(new GeneratedResearch(answer, "mock-gemini", null));
        }
    }

    private sealed class AllInvalidCitationGenerator : IResearchGenerator
    {
        public Task<GeneratedResearch> GenerateAsync(string supplier, string question, IReadOnlyList<ResearchSource> sources, CancellationToken cancellationToken) =>
            Task.FromResult(new GeneratedResearch(new GeneratedAnswer([
                new GeneratedFinding("Uncited claim.", [new GeneratedEvidence(sources[0].Id, "This wording is absent from the passage")])
            ], []), "mock-gemini", null));
    }

    private sealed class PassageCaptureGenerator : IResearchGenerator
    {
        public IReadOnlyList<string> Excerpts { get; private set; } = [];
        public Task<GeneratedResearch> GenerateAsync(string supplier, string question, IReadOnlyList<ResearchSource> sources, CancellationToken cancellationToken)
        {
            Excerpts = sources.Select(source => source.Excerpt).ToArray();
            return Task.FromResult(new GeneratedResearch(new GeneratedAnswer([], []), "mock-gemini", null));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset time) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => time;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<Dictionary<string, object?>> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                Entries.Add(properties.ToDictionary(property => property.Key, property => property.Value));
        }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
