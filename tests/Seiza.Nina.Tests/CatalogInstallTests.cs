using System.IO;
using Moq;
using NINA.Profile.Interfaces;
using NUnit.Framework;

namespace Seiza.Nina.Tests;

[TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
public class CatalogInstallTests
{
    [Test]
    public async Task InstallingUsesSeizaDefaultsAndReleasesOldMappings()
    {
        var engine = new TrackingEngine();
        await using var host = new SeizaEngineHost(_ => engine);
        await host.SolveAsync([], 0, 0, TestData.Request(), TestData.Options, CancellationToken.None);
        var installer = new FakeInstaller { InstallAction = (package, token) => { Assert.That(engine.Disposed, Is.True); Assert.That(package, Is.EqualTo("standard")); return "native default directory"; } };
        var viewModel = new CatalogInstallerViewModel(host, installer);
        await viewModel.InstallCommand.ExecuteAsync(null);
        Assert.That(viewModel.Status, Does.Contain("automatically"));
        Assert.That(viewModel.Status, Does.Not.Contain("native default directory"));
        Assert.That(viewModel.IsInstalling, Is.False);
        Assert.That(viewModel.Percent, Is.EqualTo(100));
    }

    [Test]
    public void LegacyPluginPathsAreIgnoredInFavorOfSeizaDefaults()
    {
        var profiles = TestData.Profile();
        foreach (var key in new[] { "CatalogPath", "IndexPath", "CatalogInstallDirectory" })
            profiles.Object.ActiveProfile.PluginSettings.SetValue(SeizaSettings.PluginId, key, "obsolete custom path");
        var settings = new SeizaSettings(profiles.Object);
        Assert.That(settings.Snapshot().CatalogPath, Is.Empty);
        Assert.That(settings.Snapshot().IndexPath, Is.Empty);
        Assert.That(typeof(SeizaSettings).GetProperty("CatalogPath"), Is.Null);
        Assert.That(typeof(SeizaSettings).GetProperty("CatalogInstallDirectory"), Is.Null);
    }

    [Test]
    public async Task FailedInstallShowsTheError()
    {
        await using var host = new SeizaEngineHost(_ => new TrackingEngine());
        var installer = new FakeInstaller { InstallAction = (_, _) => throw new IOException("Test download error") };
        var viewModel = new CatalogInstallerViewModel(host, installer);
        await viewModel.InstallCommand.ExecuteAsync(null);
        Assert.That(viewModel.Status, Does.Contain("Test download error"));
        Assert.That(viewModel.IsIdle, Is.True);
    }

    [Test]
    public async Task CancelCommandStopsTheInstallerWithoutChangingProfileSettings()
    {
        await using var host = new SeizaEngineHost(_ => new TrackingEngine());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new FakeInstaller
        {
            InstallAction = (_, token) =>
            {
                entered.SetResult();
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return "native default";
            }
        };
        var viewModel = new CatalogInstallerViewModel(host, installer);
        var installing = viewModel.InstallCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.InstallCancelCommand.Execute(null);
        await installing;
        Assert.That(viewModel.Status, Does.Contain("cancelled"));
        Assert.That(viewModel.IsInstalling, Is.False);
    }

    [Test]
    public void NativeCatalogDiscoveryUsesSeizasEnvironmentDefaults()
    {
        using var defaults = new TestData.DefaultCatalogScope();
        var available = new NativeCatalogInstaller().Status();
        Assert.That(available.StarCatalog, Is.EqualTo(Path.Combine(TestData.Fixture, "stars.bin")));
        Assert.That(available.BlindIndex, Is.EqualTo(Path.Combine(TestData.Fixture, "blind-gaia16.idx")));
        Assert.That(available.Summary, Does.Contain("Primary and blind solving ready"));
    }

    [Test]
    public async Task PluginTeardownCancelsActiveCatalogMaintenance()
    {
        var host = new SeizaEngineHost(_ => new TrackingEngine());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installing = host.RunMaintenanceAsync(token =>
        {
            entered.SetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return "unused";
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.DisposeAsync();
        Assert.That(async () => await installing, Throws.InstanceOf<OperationCanceledException>());
    }

    private sealed class FakeInstaller : ICatalogInstaller
    {
        public Func<string, CancellationToken, string> InstallAction { get; init; } = (_, _) => "native default";

        public string Install(string package, IProgress<CatalogProgress> progress, CancellationToken cancellationToken) => InstallAction(package, cancellationToken);

        public CatalogAvailability Status() => new("stars-deep-gaia17.bin", "blind-gaia16.idx");
    }

    private sealed class TrackingEngine : ISeizaEngine
    {
        public bool Disposed { get; private set; }

        public SolveResponse Solve(ushort[] pixels, int width, int height, SolveRequest request, CancellationToken cancellationToken) => new();

        public void Dispose() => Disposed = true;
    }
}
