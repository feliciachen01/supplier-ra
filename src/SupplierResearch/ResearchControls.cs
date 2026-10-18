namespace SupplierResearch;

public sealed record ResearchPreset(string Id, string Label, string Supplier, string Question, bool HasSavedExample);

public static class ResearchPresets
{
    public static IReadOnlyList<ResearchPreset> All { get; } =
    [
        new("tetra-pak-sustainability", "Tetra Pak sustainability", "Tetra Pak",
            "Find public company and sustainability information", false),
        new("northwind-packaging", "Northwind Packaging", "Northwind Packaging",
            "Find public company and sustainability information", true)
    ];

    public static bool TryGet(string id, out ResearchPreset preset)
    {
        preset = All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))!;
        return preset is not null;
    }
}

public enum LiveAdmissionFailure
{
    None,
    ConcurrentRequest,
    RateLimit,
    LifetimeLimit
}

public sealed class LiveRequestGate(TimeProvider timeProvider)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaxPerWindow = 3;
    private const int MaxPerProcess = 20;
    private readonly object sync = new();
    private readonly Queue<DateTimeOffset> recentAdmissions = new();
    private int admittedCount;
    private bool requestInFlight;

    public bool TryAcquire(out IDisposable? lease, out LiveAdmissionFailure failure)
    {
        lock (sync)
        {
            var cutoff = timeProvider.GetUtcNow() - Window;
            while (recentAdmissions.TryPeek(out var admittedAt) && admittedAt <= cutoff)
            {
                recentAdmissions.Dequeue();
            }

            if (requestInFlight)
            {
                lease = null;
                failure = LiveAdmissionFailure.ConcurrentRequest;
                return false;
            }
            if (admittedCount >= MaxPerProcess)
            {
                lease = null;
                failure = LiveAdmissionFailure.LifetimeLimit;
                return false;
            }
            if (recentAdmissions.Count >= MaxPerWindow)
            {
                lease = null;
                failure = LiveAdmissionFailure.RateLimit;
                return false;
            }

            requestInFlight = true;
            admittedCount++;
            recentAdmissions.Enqueue(timeProvider.GetUtcNow());
            lease = new AdmissionLease(this);
            failure = LiveAdmissionFailure.None;
            return true;
        }
    }

    private void Release()
    {
        lock (sync)
        {
            requestInFlight = false;
        }
    }

    private sealed class AdmissionLease(LiveRequestGate owner) : IDisposable
    {
        private LiveRequestGate? owner = owner;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}

public static class ResearchEndpointHelpers
{
    public static async Task<bool> HasRequestOverridesAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.Query.Count > 0 || request.ContentLength is > 0)
        {
            return true;
        }

        if (request.Body.CanSeek)
        {
            return request.Body.Length > request.Body.Position;
        }

        var firstByte = new byte[1];
        return await request.Body.ReadAsync(firstByte, cancellationToken) != 0;
    }

    public static IResult AdmissionRejected(LiveAdmissionFailure failure) =>
        failure == LiveAdmissionFailure.ConcurrentRequest
            ? Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
                title: "A live research request is already in progress.")
            : Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
                title: "The live research request limit has been reached.");

}
