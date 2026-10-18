using SupplierResearch;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8_192);

var hostedMode = builder.Environment.IsProduction() ||
    string.Equals(builder.Configuration["Hosting:Mode"], "Hosted", StringComparison.OrdinalIgnoreCase);
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
else if (allowLocalArbitraryQueries)
{
    app.MapPost("/api/research", ResearchEndpoints.LocalResearchAsync);
}

app.MapFallbackToFile("index.html");
app.Run();
