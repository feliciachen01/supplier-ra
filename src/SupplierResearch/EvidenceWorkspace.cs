using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

namespace SupplierResearch;

public sealed record WorkspacePassage(string Id, string DocumentId, string Name, int Page, string Text);

public sealed class EvidenceWorkspace(IWebHostEnvironment environment, ILogger<EvidenceWorkspace>? logger = null)
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxPassages = 500;
    private const int MaxRetrieved = 12;
    private readonly ILogger<EvidenceWorkspace> _logger = logger ?? NullLogger<EvidenceWorkspace>.Instance;
    private readonly string _connectionString = CreateDatabase(environment);

    public static long FileLimit => MaxFileBytes;

    private static string CreateDatabase(IWebHostEnvironment environment)
    {
        var folder = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "evidence-workspace.sqlite3");
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS documents(id TEXT PRIMARY KEY, name TEXT NOT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS passages(id TEXT PRIMARY KEY, document_id TEXT NOT NULL REFERENCES documents(id), page INTEGER NOT NULL, text TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS research_runs(id TEXT PRIMARY KEY, question TEXT NOT NULL, status TEXT NOT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS findings(id TEXT PRIMARY KEY, run_id TEXT NOT NULL REFERENCES research_runs(id), text TEXT NOT NULL, evidence_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS review_decisions(finding_id TEXT PRIMARY KEY REFERENCES findings(id), decision TEXT NOT NULL, note TEXT NOT NULL, updated TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        return cs;
    }

    public async Task<IReadOnlyList<object>> UploadAsync(IReadOnlyList<IFormFile> files, CancellationToken token)
    {
        if (files.Count is < 1 or > 3) throw new WorkspaceInputException("Upload between one and three PDFs.");
        var added = new List<object>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        var existing = connection.CreateCommand(); existing.CommandText = "SELECT COUNT(*) FROM documents";
        var currentDocs = Convert.ToInt32(await existing.ExecuteScalarAsync(token));
        if (currentDocs + files.Count > 3) throw new WorkspaceInputException("Workspace supports up to three PDFs total.");
        foreach (var file in files)
        {
            if (file.Length is < 8 or > MaxFileBytes || !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceInputException("Each file must be a PDF between 1 byte and 5 MB.");
            await using var memory = new MemoryStream((int)file.Length);
            await file.CopyToAsync(memory, token);
            var bytes = memory.ToArray();
            if (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-")
                throw new WorkspaceInputException("A file is not a valid PDF.");
            List<(int Page, string Text)> extracted;
            try
            {
                using var pdf = PdfDocument.Open(new MemoryStream(bytes));
                if (pdf.NumberOfPages > 200) throw new WorkspaceInputException("PDF exceeds the 200-page limit.");
                extracted = pdf.GetPages().Select(page => (page.Number, page.Text.Trim()))
                    .Where(page => page.Item2.Length > 0).Select(page => (page.Number, page.Item2)).ToList();
            }
            catch (WorkspaceInputException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new WorkspaceInputException("A PDF is encrypted, damaged, or could not be read.");
            }
            if (extracted.Count == 0) throw new WorkspaceInputException("A PDF contains no extractable text.");
            var docId = Guid.NewGuid().ToString("N");
            using var transaction = connection.BeginTransaction();
            var insertDoc = connection.CreateCommand(); insertDoc.Transaction = transaction;
            insertDoc.CommandText = "INSERT INTO documents VALUES($id,$name,$created)";
            insertDoc.Parameters.AddWithValue("$id", docId);
            insertDoc.Parameters.AddWithValue("$name", Path.GetFileName(file.FileName)[..Math.Min(Path.GetFileName(file.FileName).Length, 160)]);
            insertDoc.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            await insertDoc.ExecuteNonQueryAsync(token);
            foreach (var page in extracted)
            {
                foreach (var chunk in Chunk(page.Text))
                {
                    if (await PassageCountAsync(connection, transaction, token) >= MaxPassages)
                        throw new WorkspaceInputException("Workspace passage limit reached (500). Remove documents before uploading more.");
                    var id = Guid.NewGuid().ToString("N");
                    var insert = connection.CreateCommand(); insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO passages VALUES($id,$doc,$page,$text)";
                    insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$doc", docId);
                    insert.Parameters.AddWithValue("$page", page.Page); insert.Parameters.AddWithValue("$text", chunk);
                    await insert.ExecuteNonQueryAsync(token);
                }
            }
            transaction.Commit();
            added.Add(new { id = docId, name = Path.GetFileName(file.FileName), pages = extracted.Count });
        }
        return added;
    }

    public async Task<IReadOnlyList<object>> ListDocumentsAsync(CancellationToken token)
    {
        await using var c = new SqliteConnection(_connectionString); await c.OpenAsync(token);
        var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,name,created FROM documents ORDER BY created DESC";
        var rows = new List<object>(); await using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) rows.Add(new { id = r.GetString(0), name = r.GetString(1), created = r.GetString(2) });
        return rows;
    }

    public async Task<ResearchResponse> ResearchAsync(string question, IResearchGenerator generator, CancellationToken token)
    {
        await using var c = new SqliteConnection(_connectionString); await c.OpenAsync(token);
        var passages = new List<WorkspacePassage>();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT p.id,p.document_id,d.name,p.page,p.text FROM passages p JOIN documents d ON d.id=p.document_id";
        await using (var r = await cmd.ExecuteReaderAsync(token))
            while (await r.ReadAsync(token)) passages.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4)));
        var terms = Regex.Matches(question.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(m => m.Value).Distinct().ToArray();
        var ranked = passages.Select(p => (Passage: p, Score: terms.Sum(t => Regex.Matches(p.Text.ToLowerInvariant(), $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(t)}(?![\p{{L}}\p{{N}}])").Count)))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Passage.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Passage.Page).ThenBy(x => x.Passage.Id, StringComparer.Ordinal).Take(MaxRetrieved).ToArray();
        var retrieved = ranked.Select(x => x.Passage).ToArray();
        var sources = retrieved.Select((p, i) => new ResearchSource(p.Id, $"{p.Name}, page {p.Page}", $"https://evidence.local/{p.DocumentId}/{p.Page}", p.Text, "uploaded_pdf_passage")).ToArray();
        _logger.LogInformation("PDF research retrieval: {Status} {PassageCount} {SelectedPassages}",
            sources.Length == 0 ? "insufficient_evidence" : "retrieved",
            passages.Count, ranked.Select(x => new { PassageId = x.Passage.Id, x.Passage.DocumentId, x.Passage.Page, x.Score }).ToArray());
        GeneratedResearch? generated = null; var findings = (IReadOnlyList<ResearchFinding>)Array.Empty<ResearchFinding>();
        var followups = (IReadOnlyList<string>)Array.Empty<string>(); IReadOnlyList<string> warnings = Array.Empty<string>(); string status;
        if (sources.Length == 0) status = "insufficient_evidence";
        else
        {
            try { generated = await generator.GenerateAsync("Uploaded documents", question, sources, token); }
            catch (ResearchGenerationException ex) when (ex.Kind == GenerationFailureKind.Cancelled) { throw new OperationCanceledException(token); }
            catch (ResearchGenerationException ex) when (ex.Kind != GenerationFailureKind.Cancelled)
            {
                _logger.LogWarning("PDF research generation failed: {FailureKind} {StatusCode}", ex.Kind,
                    ex.StatusCode is null ? null : (int)ex.StatusCode.Value);
                warnings = ["Generation unavailable; retrieved passages are shown without findings."];
            }
            if (generated is not null)
            {
                if (ResearchWorkflow.TryValidate(generated.Answer, sources, out findings, out followups, out var failure))
                {
                    status = findings.Count == 0 ? "insufficient_evidence" : "complete";
                    if (findings.Count == 0)
                        warnings = ["The retrieved evidence did not support a defensible answer to this question; no finding was generated."];
                }
                else
                {
                    _logger.LogWarning("PDF research validation rejected output: {Rule} {FindingIndex} {EvidenceIndex} {SourceId} {AvailableSourceIds} {ExcerptSpanLength} {SourceExcerptLength}",
                        failure!.Rule, failure.FindingIndex, failure.EvidenceIndex, failure.SourceId,
                        failure.AvailableSourceIds, failure.ExcerptSpanLength, failure.SourceExcerptLength);
                    findings = []; warnings = ["Generated citations failed evidence validation; retrieved passages are shown without findings."]; status = "sources_only";
                }
            }
            else status = "sources_only";
        }
        var runId = Guid.NewGuid().ToString("N");
        var ins = c.CreateCommand(); ins.CommandText = "INSERT INTO research_runs VALUES($id,$question,$status,$created)";
        ins.Parameters.AddWithValue("$id", runId); ins.Parameters.AddWithValue("$question", question);
        ins.Parameters.AddWithValue("$status", status); ins.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O")); await ins.ExecuteNonQueryAsync(token);
        var findingIds = new List<string>();
        foreach (var f in findings)
        {
            var fId = Guid.NewGuid().ToString("N"); findingIds.Add(fId); var fi = c.CreateCommand();
            fi.CommandText = "INSERT INTO findings VALUES($id,$run,$text,$evidence)"; fi.Parameters.AddWithValue("$id", fId);
            fi.Parameters.AddWithValue("$run", runId); fi.Parameters.AddWithValue("$text", f.Text);
            fi.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(f.Evidence)); await fi.ExecuteNonQueryAsync(token);
        }
        _logger.LogInformation("PDF research request completed: {RunId} {Status} {RetrievedPassageCount} {FindingCount}",
            runId, status, sources.Length, findings.Count);
        return new ResearchResponse("Uploaded documents", question, "documents", status, findings, followups, sources, warnings, generated?.Model, generated?.Usage)
        { RunId = runId, FindingIds = findingIds };
    }

    public async Task DecideAsync(string findingId, string decision, string note, CancellationToken token)
    {
        if (decision is not ("accept" or "reject" or "flag") || note.Length > 500) throw new WorkspaceInputException("Choose accept, reject, or flag; notes are limited to 500 characters.");
        await using var c = new SqliteConnection(_connectionString); await c.OpenAsync(token);
        var exists = c.CreateCommand(); exists.CommandText = "SELECT 1 FROM findings WHERE id=$id"; exists.Parameters.AddWithValue("$id", findingId);
        if (await exists.ExecuteScalarAsync(token) is null) throw new WorkspaceInputException("Finding was not found.");
        var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO review_decisions VALUES($id,$decision,$note,$updated) ON CONFLICT(finding_id) DO UPDATE SET decision=excluded.decision,note=excluded.note,updated=excluded.updated";
        cmd.Parameters.AddWithValue("$id", findingId); cmd.Parameters.AddWithValue("$decision", decision); cmd.Parameters.AddWithValue("$note", note);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(token);
    }

    public async Task<string> ExportCsvAsync(CancellationToken token)
    {
        await using var c = new SqliteConnection(_connectionString); await c.OpenAsync(token);
        var cmd = c.CreateCommand(); cmd.CommandText = "SELECT f.text,f.evidence_json,r.question,d.decision,d.note FROM findings f JOIN review_decisions d ON d.finding_id=f.id JOIN research_runs r ON r.id=f.run_id WHERE d.decision IN ('accept','reject','flag') ORDER BY d.updated";
        await using var r = await cmd.ExecuteReaderAsync(token); var sb = new StringBuilder("finding,question,decision,note,evidence\r\n");
        while (await r.ReadAsync(token)) sb.AppendJoin(',', Enumerable.Range(0, 5).Select(i => Csv(r.GetString(i)))).Append("\r\n");
        return sb.ToString();
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    private static IEnumerable<string> Chunk(string text)
    {
        const int size = 1200; for (var start = 0; start < text.Length; start += size)
        { var length = Math.Min(size, text.Length - start); var end = start + length;
          if (end < text.Length) { var boundary = text.LastIndexOf(' ', end - 1, length); if (boundary > start + size / 2) length = boundary - start; }
          yield return text.Substring(start, length).Trim(); }
    }
    private static async Task<int> PassageCountAsync(SqliteConnection c, SqliteTransaction t, CancellationToken token)
    { var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "SELECT COUNT(*) FROM passages"; return Convert.ToInt32(await cmd.ExecuteScalarAsync(token)); }
}

public sealed class WorkspaceInputException(string message) : Exception(message);
