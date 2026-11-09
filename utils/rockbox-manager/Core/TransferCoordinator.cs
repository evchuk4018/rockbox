namespace RockboxManager.Core;

public static class TransferCoordinator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IDisposable Enter(CancellationToken cancellation = default)
    {
        Gate.Wait(cancellation);
        return new Lease();
    }

    public static async Task<IDisposable> EnterAsync(CancellationToken cancellation = default)
    {
        await Gate.WaitAsync(cancellation);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) Gate.Release(); }
    }
}
