using System.Text.Json;
using SupplierResearch;

var builder = WebApplication.CreateBuilder(args);
var searchMode = builder.Configuration["Search:Mode"] ?? "Fixture";

if (searchMode.Equals("Fixture", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ISupplierSearchProvider, FixtureSearchProvider>();
}
else if (searchMode.Equals("Tavily", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["TAVILY_API_KEY"]))
    {
        throw new InvalidOperationException("TAVILY_API_KEY is required when Search:Mode is Tavily.");
    }

    builder.Services.AddHttpClient<ISupplierSearchProvider, TavilySearchProvider>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(8);
    });
}
else
{
    throw new InvalidOperationException("Search:Mode must be Fixture or Tavily.");
}

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

app.MapPost("/api/research", async Task<IResult> (
    ResearchRequest request,
    ResearchWorkflow workflow,
    CancellationToken cancellationToken) =>
{
    var supplier = request.Supplier?.Trim();
    var question = request.Question?.Trim();
    if (supplier is null || supplier.Length is < 2 or > 120 ||
        question is null || question.Length is < 10 or > 500)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["request"] = ["Supplier must be 2-120 characters and question 10-500 characters."]
        });
    }

    try
    {
        var result = await workflow.ResearchAsync(supplier, question, cancellationToken);
        return Results.Ok(result);
    }
    catch (ProviderSearchException ex) when (ex.Kind == ProviderFailureKind.Cancelled)
    {
        throw new OperationCanceledException(cancellationToken);
    }
    catch (ProviderSearchException ex)
    {
        var (statusCode, title) = ex.Kind switch
        {
            ProviderFailureKind.UpstreamHttp => (StatusCodes.Status502BadGateway,
                $"Search provider returned HTTP {(int)ex.StatusCode!.Value}."),
            ProviderFailureKind.Transport => (StatusCodes.Status502BadGateway,
                "Could not connect to the search provider."),
            ProviderFailureKind.Timeout => (StatusCodes.Status504GatewayTimeout,
                "Search provider request timed out."),
            _ => (StatusCodes.Status502BadGateway, "Search provider request failed.")
        };
        return Results.Problem(statusCode: statusCode, title: title);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem(statusCode: StatusCodes.Status504GatewayTimeout, title: "Search timed out.");
    }
    catch (HttpRequestException)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Search provider request failed.");
    }
    catch (JsonException)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Search provider returned an invalid response.");
    }
});

app.MapFallbackToFile("index.html");
app.Run();
