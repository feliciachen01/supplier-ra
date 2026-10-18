using System.Text;
using SupplierResearch;

var builder = WebApplication.CreateBuilder(args);
var hostedMode = builder.Environment.IsProduction() ||
    string.Equals(builder.Configuration["Hosting:Mode"], "Hosted", StringComparison.OrdinalIgnoreCase);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = hostedMode ? 8_192 : 16 * 1024 * 1024);
var hostedLiveEnabled = builder.Configuration.GetValue<bool>("Hosting:LiveEnabled");
var allowLocalArbitraryQueries = !hostedMode && builder.Configuration.GetValue<bool>("Search:AllowArbitraryQueries");
var searchMode = builder.Configuration["Search:Mode"] ?? "Fixture";

if (hostedMode && hostedLiveEnabled && !searchMode.Equals("Tavily", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Hosted live research requires Search:Mode=Tavily.");
}
if (hostedMode && hostedLiveEnabled && string.IsNullOrWhiteSpace(builder.Configuration["GEMINI_API_KEY"]))
{
    throw new InvalidOperationException("GEMINI_API_KEY is required when hosted live research is enabled.");
}

if (searchMode.Equals("Fixture", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ISupplierSearchProvider, FixtureSearchProvider>();
}
else if (searchMode.Equals("Tavily", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["TAVILY_API_KEY"]))
    {
        if (hostedMode && !hostedLiveEnabled)
        {
            builder.Services.AddSingleton<ISupplierSearchProvider, DisabledSearchProvider>();
        }
        else
        {
            throw new InvalidOperationException("TAVILY_API_KEY is required when Search:Mode is Tavily.");
        }
    }
    else
    {
        builder.Services.AddHttpClient<ISupplierSearchProvider, TavilySearchProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(8);
        });
    }
}
else
{
    throw new InvalidOperationException("Search:Mode must be Fixture or Tavily.");
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LiveRequestGate>();
builder.Services.AddSingleton<SupplierSearchService>();
builder.Services.AddHttpClient<IResearchGenerator, GeminiResearchGenerator>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<ResearchWorkflow>();
builder.Services.AddScoped<EvidenceWorkspace>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/demo/presets", () => Results.Ok(new
{
    mode = hostedMode ? "hosted" : "local",
    liveEnabled = hostedMode && hostedLiveEnabled,
    localArbitraryQueries = allowLocalArbitraryQueries,
    presets = ResearchPresets.All.Select(preset => new { preset.Id, preset.Label, preset.HasSavedExample })
}));

app.MapGet("/api/examples/{presetId}", (string presetId, IWebHostEnvironment environment) =>
{
    if (!ResearchPresets.TryGet(presetId, out var preset) || !preset.HasSavedExample)
    {
        return Results.NotFound();
    }

    var path = Path.Combine(environment.WebRootPath, "examples", $"{preset.Id}.json");
    return File.Exists(path) ? Results.File(path, "application/json") : Results.NotFound();
});

if (hostedMode)
{
    app.MapPost("/api/demo/{presetId}", ResearchEndpoints.PublicDemoAsync);
    app.MapMethods("/api/{**path}", ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
        () => Results.NotFound());
}
else
{
    if (allowLocalArbitraryQueries) app.MapPost("/api/research", ResearchEndpoints.LocalResearchAsync);
    app.MapPost("/api/workspace/documents", async (HttpRequest request, EvidenceWorkspace workspace, CancellationToken token) =>
    {
        if (!request.HasFormContentType) return Results.BadRequest(new { title = "Use multipart form upload." });
        try { var form = await request.ReadFormAsync(token); return Results.Ok(await workspace.UploadAsync(form.Files, token)); }
        catch (WorkspaceInputException ex) { return Results.BadRequest(new { title = ex.Message }); }
    }).DisableAntiforgery();
    app.MapGet("/api/workspace/documents", async (EvidenceWorkspace workspace, CancellationToken token) => Results.Ok(await workspace.ListDocumentsAsync(token)));
    app.MapPost("/api/workspace/research", async (WorkspaceResearchRequest input, HttpContext context, EvidenceWorkspace workspace,
        IResearchGenerator generator, LiveRequestGate gate, IConfiguration configuration, CancellationToken token) =>
    {
        if (input.Question is null || input.Question.Trim().Length is < 10 or > 500) return Results.BadRequest(new { title = "Question must be 10-500 characters." });
        if (!string.IsNullOrWhiteSpace(configuration["GEMINI_API_KEY"]))
        {
            if (!gate.TryAcquire(out var lease, out var failure)) return ResearchEndpointHelpers.AdmissionRejected(failure);
            using (lease) return await ResearchEndpoints.ExecuteWithDeadlineAsync(context,
                ct => workspace.ResearchAsync(input.Question.Trim(), generator, ct), token);
        }
        return await ResearchEndpoints.ExecuteWithDeadlineAsync(context,
            ct => workspace.ResearchAsync(input.Question.Trim(), generator, ct), token);
    });
    app.MapPost("/api/workspace/findings/{id}/review", async (string id, WorkspaceReviewRequest input, EvidenceWorkspace workspace, CancellationToken token) =>
    {
        if (id.Length != 32 || id.Any(c => !Uri.IsHexDigit(c))) return Results.BadRequest(new { title = "Invalid finding ID." });
        try { await workspace.DecideAsync(id, input.Decision ?? "", input.Note ?? "", token); return Results.Ok(); }
        catch (WorkspaceInputException ex) { return Results.BadRequest(new { title = ex.Message }); }
    });
    app.MapGet("/api/workspace/export.csv", async (EvidenceWorkspace workspace, CancellationToken token) =>
        Results.File(Encoding.UTF8.GetBytes(await workspace.ExportCsvAsync(token)), "text/csv", "reviewed-findings.csv"));
}

app.MapFallbackToFile("index.html");
app.Run();

public sealed record WorkspaceResearchRequest(string? Question);
public sealed record WorkspaceReviewRequest(string? Decision, string? Note);
