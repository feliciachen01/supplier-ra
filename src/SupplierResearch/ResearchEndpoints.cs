namespace SupplierResearch;

public static class ResearchEndpoints
{
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);

    public static async Task<IResult> LocalResearchAsync(
        ResearchRequest request,
        HttpContext context,
        ResearchWorkflow workflow,
        LiveRequestGate gate,
        CancellationToken cancellationToken)
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

        if (workflow.Mode == "live")
        {
            if (!gate.TryAcquire(out var lease, out var failure))
            {
                return ResearchEndpointHelpers.AdmissionRejected(failure);
            }

            using (lease)
            {
                return await ExecuteWithDeadlineAsync(context, token => workflow.ResearchAsync(supplier, question, token), cancellationToken);
            }
        }

        return await ExecuteWithDeadlineAsync(context, token => workflow.ResearchAsync(supplier, question, token), cancellationToken);
    }

    public static async Task<IResult> PublicDemoAsync(
        string presetId,
        HttpRequest request,
        HttpContext context,
        ResearchWorkflow workflow,
        LiveRequestGate gate,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!ResearchPresets.TryGet(presetId, out var preset))
        {
            return Results.NotFound();
        }

        if (await ResearchEndpointHelpers.HasRequestOverridesAsync(request, cancellationToken))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Public demo requests cannot override preset inputs.");
        }

        if (!configuration.GetValue<bool>("Hosting:LiveEnabled") || workflow.Mode != "live")
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Public live research is disabled.");
        }

        if (!gate.TryAcquire(out var lease, out var failure))
        {
            return ResearchEndpointHelpers.AdmissionRejected(failure);
        }

        using (lease)
        {
            return await ExecuteWithDeadlineAsync(context,
                token => workflow.ResearchAsync(preset.Supplier, preset.Question, token), cancellationToken);
        }
    }

    public static async Task<IResult> ExecuteWithDeadlineAsync(
        HttpContext context,
        Func<CancellationToken, Task<ResearchResponse>> execute,
        CancellationToken requestAborted,
        TimeSpan? deadlineOverride = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        deadline.CancelAfter(deadlineOverride ?? RequestDeadline);
        try
        {
            return Results.Ok(await execute(deadline.Token));
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Problem(context, StatusCodes.Status504GatewayTimeout, "Research request timed out.");
        }
        catch (ProviderSearchException exception)
        {
            var (statusCode, title) = exception.Kind switch
            {
                ProviderFailureKind.Timeout or ProviderFailureKind.Cancelled =>
                    (StatusCodes.Status504GatewayTimeout, "Live search timed out."),
                ProviderFailureKind.UpstreamHttp when exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or
                    System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests =>
                    (StatusCodes.Status503ServiceUnavailable, "Live search is temporarily unavailable."),
                _ => (StatusCodes.Status502BadGateway, "Live search failed.")
            };
            return Problem(context, statusCode, title);
        }
        catch (HttpRequestException)
        {
            return Problem(context, StatusCodes.Status502BadGateway, "Search provider request failed.");
        }
        catch (System.Text.Json.JsonException)
        {
            return Problem(context, StatusCodes.Status502BadGateway, "Search provider returned an invalid response.");
        }
    }

    private static IResult Problem(HttpContext context, int statusCode, string title) =>
        Results.Problem(statusCode: statusCode, title: title,
            extensions: new Dictionary<string, object?> { ["requestId"] = context.TraceIdentifier });
}
