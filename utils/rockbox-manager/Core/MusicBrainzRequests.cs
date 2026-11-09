namespace RockboxManager.Core;

internal static class MusicBrainzRequests
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime nextRequest;

    // Artwork and discovery share the same per-application request budget.
    public static async Task Wait(CancellationToken cancellation)
    {
        await Gate.WaitAsync(cancellation);
        try
        {
            var delay = nextRequest - DateTime.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            nextRequest = DateTime.UtcNow.AddMilliseconds(1100);
        }
        finally { Gate.Release(); }
    }
}
