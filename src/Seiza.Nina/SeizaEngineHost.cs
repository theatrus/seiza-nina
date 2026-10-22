namespace Seiza.Nina;

internal static class SeizaRuntime
{
    internal static SeizaEngineHost Host { get; } = new(options => new NativeSeizaEngine(options));
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NINA.Profile.Interfaces.IProfileService, SeizaSettings> settings = new();

    internal static SeizaSettings SettingsFor(NINA.Profile.Interfaces.IProfileService profiles) => settings.GetValue(profiles, service => new(service));
}

internal interface ISeizaEngine : IDisposable
{
    SolveResponse Solve(ushort[] pixels, int width, int height, SolveRequest request, CancellationToken cancellationToken);
}

internal sealed class SeizaEngineHost(Func<SolveOptions, ISeizaEngine> factory) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private ISeizaEngine? engine;
    private (string Catalog, string Index)? engineKey;

    public async Task<SolveResponse> SolveAsync(ushort[] pixels, int width, int height,
        SolveRequest request, SolveOptions options, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var token = linked.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var key = (options.CatalogPath, options.IndexPath);
                if (engineKey != key)
                {
                    engine?.Dispose();
                    engine = null;
                    engineKey = null;
                }

                engine ??= factory(options);
                engineKey = key;
                token.ThrowIfCancellationRequested();
                var result = engine.Solve(pixels, width, height, request, token);
                token.ThrowIfCancellationRequested();
                return result;
            }, token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Cancels active native work and queued callers before waiting to release catalog mappings.
        await shutdown.CancelAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            engine?.Dispose();
            engine = null;
            engineKey = null;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<T> RunMaintenanceAsync<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var token = linked.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            // Release our mappings before atomically publishing replacement catalogs.
            engine?.Dispose();
            engine = null;
            engineKey = null;
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var result = operation(token);
                token.ThrowIfCancellationRequested();
                return result;
            }, token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
