using System.IO;
using NUnit.Framework;

namespace Seiza.Nina.Tests;

[TestFixture, NonParallelizable]
public class EngineTests
{
    [Test]
    public async Task ReusesEngineAndReplacesItWhenCatalogPathsChange()
    {
        var engines = new List<FakeEngine>();
        await using var host = new SeizaEngineHost(_ => { var e = new FakeEngine(); engines.Add(e); return e; });
        var request = TestData.Request();
        await host.SolveAsync([], 0, 0, request, TestData.Options, CancellationToken.None);
        await host.SolveAsync([], 0, 0, request, TestData.Options with { TimeLimitSeconds = 15 }, CancellationToken.None);
        Assert.That(engines, Has.Count.EqualTo(1));
        await host.SolveAsync([], 0, 0, request, TestData.Options with { CatalogPath = "other" }, CancellationToken.None);
        Assert.That(engines, Has.Count.EqualTo(2));
        Assert.That(engines[0].Disposed, Is.True);
        Assert.That(engines[1].Disposed, Is.False);
    }

    [Test]
    public async Task CancelsQueuedWorkAndTeardownCancelsActiveWorkBeforeDisposal()
    {
        using var entered = new ManualResetEventSlim();
        var active = 0;
        var engine = new FakeEngine
        {
            Action = token =>
            {
                Interlocked.Increment(ref active);
                entered.Set();
                try { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
                finally { Interlocked.Decrement(ref active); }
                return new();
            },
            OnDispose = () => Assert.That(active, Is.Zero)
        };
        var host = new SeizaEngineHost(_ => engine);
        var first = host.SolveAsync([], 0, 0, TestData.Request(), TestData.Options, CancellationToken.None);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        using var cancel = new CancellationTokenSource();
        var queued = host.SolveAsync([], 0, 0, TestData.Request(), TestData.Options, cancel.Token);
        cancel.Cancel();
        Assert.That(async () => await queued, Throws.InstanceOf<OperationCanceledException>());
        await host.DisposeAsync();
        Assert.That(async () => await first, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(engine.Disposed, Is.True);
        Assert.That(async () => await host.SolveAsync([], 0, 0, TestData.Request(), TestData.Options, CancellationToken.None), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void NativeCancellationLeavesEngineUsable()
    {
        using var engine = new NativeSeizaEngine(TestData.Options);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => engine.Solve(TestData.Pixels(), 1024, 768, TestData.Request(), cancel.Token));
        var result = engine.Solve(TestData.Pixels(), 1024, 768, TestData.Request(), CancellationToken.None);
        Assert.That(result.Success, Is.True);
    }

    [Test]
    public void NativeTimeoutAndNoMatchReturnUnsuccessfulResults()
    {
        using var engine = new NativeSeizaEngine(TestData.Options);
        var timedOut = engine.Solve(TestData.Pixels(), 1024, 768, TestData.Request() with { TimeLimitSeconds = 1e-9 }, CancellationToken.None);
        Assert.That(timedOut.Success, Is.False);
        Assert.That(timedOut.Message, Does.Contain("time limit"));
        var miss = engine.Solve(new ushort[1024 * 768], 1024, 768, TestData.Request(), CancellationToken.None);
        Assert.That(miss.ToResult().Success, Is.False);
    }

    [Test]
    public void MissingCatalogGivesActionableError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new NativeSeizaEngine(TestData.Options with { CatalogPath = Path.Combine(TestData.Fixture, "missing") }));
        Assert.That(error!.Message, Does.Contain("catalog"));
    }

    [TestCase(0), TestCase(double.NaN), TestCase(double.PositiveInfinity)]
    public void InvalidSettingsAreRejected(double value)
    {
        Assert.Throws<InvalidOperationException>(() => (TestData.Options with { TimeLimitSeconds = value }).Validate());
    }

    [Test]
    public void InvalidOrUnconfirmedNativeSuccessIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => new SolveResponse { Success = true, MatchedStars = 0 }.ToResult());
    }

    [TestCase(0, false), TestCase(30, false), TestCase(90, false), TestCase(270, false)]
    [TestCase(0, true), TestCase(30, true), TestCase(90, true), TestCase(270, true)]
    public void WcsMapsToNinasImageOrientationAndParity(double rotation, bool mirrored)
    {
        // Seiza's image convention: east left, north down at zero rotation.
        const double scale = 2.0 / 3600;
        var angle = rotation * Math.PI / 180;
        var parity = mirrored ? -1 : 1;
        var response = new SolveResponse
        {
            Success = true, Center = [120, 25], Crpix = [511.5, 383.5], Scale = 2, Radius = 1,
            MatchedStars = 30, RmsArcsec = 0.1,
            Cd = [[-scale * parity * Math.Cos(angle), scale * Math.Sin(angle)],
                  [-scale * parity * Math.Sin(angle), -scale * Math.Cos(angle)]]
        };
        var result = response.ToResult();
        Assert.That(result.PositionAngle, Is.EqualTo((360 - rotation) % 360).Within(1e-8));
        Assert.That(result.Flipped, Is.EqualTo(mirrored));
        Assert.That(result.Coordinates.RADegrees, Is.EqualTo(120));
    }

    private sealed class FakeEngine : ISeizaEngine
    {
        public bool Disposed { get; private set; }
        public Func<CancellationToken, SolveResponse> Action { get; init; } = _ => new();
        public Action? OnDispose { get; init; }

        public SolveResponse Solve(ushort[] pixels, int width, int height, SolveRequest request, CancellationToken cancellationToken) => Action(cancellationToken);

        public void Dispose() { OnDispose?.Invoke(); Disposed = true; }
    }
}
