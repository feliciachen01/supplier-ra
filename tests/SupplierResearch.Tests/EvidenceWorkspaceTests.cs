using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SupplierResearch;
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
