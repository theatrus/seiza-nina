using System.IO;
using Moq;
using NINA.Profile.Interfaces;
using NUnit.Framework;

namespace Seiza.Nina.Tests;

[TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
public class CatalogInstallTests
{
    [Test]
    public async Task InstallingSelectsTheDownloadedCatalogsAndReleasesOldMappings()
    {
        var profiles = TestData.Profile();
        var settings = new SeizaSettings(profiles.Object) { CatalogInstallDirectory = Path.Combine(TestData.Root, "artifacts", "install-test") };
        var engine = new TrackingEngine();
        await using var host = new SeizaEngineHost(_ => engine);
        await host.SolveAsync([], 0, 0, TestData.Request(), TestData.Options, CancellationToken.None);
        var installer = new FakeInstaller { InstallAction = (directory, _, token) => { Assert.That(engine.Disposed, Is.True); return directory; } };
        var viewModel = new CatalogInstallerViewModel(settings, profiles.Object, host, installer);
        await viewModel.InstallCommand.ExecuteAsync(null);
        Assert.That(settings.CatalogPath, Is.EqualTo(settings.CatalogInstallDirectory));
        Assert.That(settings.IndexPath, Is.Empty);
        Assert.That(viewModel.Status, Does.Contain("This profile now uses"));
        Assert.That(viewModel.IsInstalling, Is.False);
        Assert.That(viewModel.Percent, Is.EqualTo(100));
    }

    [Test]
    public async Task AProfileSwitchDuringInstallationDoesNotChangeTheNewProfilesPaths()
    {
        var profiles = TestData.Profile();
        var first = Guid.NewGuid();
        profiles.SetupGet(x => x.ActiveProfile.Id).Returns(first);
        var settings = new SeizaSettings(profiles.Object) { CatalogPath = "unchanged" };
        await using var host = new SeizaEngineHost(_ => new TrackingEngine());
        var installer = new FakeInstaller
        {
            InstallAction = (directory, _, _) =>
            {
                profiles.SetupGet(x => x.ActiveProfile.Id).Returns(Guid.NewGuid());
                return directory;
            }
        };
        var viewModel = new CatalogInstallerViewModel(settings, profiles.Object, host, installer);
        await viewModel.InstallCommand.ExecuteAsync(null);
        Assert.That(settings.CatalogPath, Is.EqualTo("unchanged"));
        Assert.That(viewModel.Status, Does.Contain("active profile changed"));
    }

    [Test]
    public async Task FailedInstallPreservesSelectedPathsAndShowsTheError()
    {
        var profiles = TestData.Profile();
        var settings = new SeizaSettings(profiles.Object) { CatalogPath = "old catalog", IndexPath = "old index" };
        await using var host = new SeizaEngineHost(_ => new TrackingEngine());
        var installer = new FakeInstaller { InstallAction = (_, _, _) => throw new IOException("Test download error") };
        var viewModel = new CatalogInstallerViewModel(settings, profiles.Object, host, installer);
        await viewModel.InstallCommand.ExecuteAsync(null);
        Assert.That(settings.CatalogPath, Is.EqualTo("old catalog"));
        Assert.That(settings.IndexPath, Is.EqualTo("old index"));
        Assert.That(viewModel.Status, Does.Contain("Test download error"));
        Assert.That(viewModel.IsIdle, Is.True);
    }

    [Test]
    public async Task CancelCommandStopsTheInstallerWithoutChangingProfileSettings()
    {
        var profiles = TestData.Profile();
        var settings = new SeizaSettings(profiles.Object) { CatalogPath = "old catalog" };
        await using var host = new SeizaEngineHost(_ => new TrackingEngine());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new FakeInstaller
        {
            InstallAction = (directory, _, token) =>
            {
                entered.SetResult();
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return directory;
            }
        };
        var viewModel = new CatalogInstallerViewModel(settings, profiles.Object, host, installer);
        var installing = viewModel.InstallCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.InstallCancelCommand.Execute(null);
        await installing;
        Assert.That(settings.CatalogPath, Is.EqualTo("old catalog"));
        Assert.That(viewModel.Status, Does.Contain("cancelled"));
        Assert.That(viewModel.IsInstalling, Is.False);
    }

    [Test]
    public void NativeCatalogDiscoveryUsesTheConfiguredFolder()
    {
        var available = new NativeCatalogInstaller().Status(TestData.Fixture, "");
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
        public Func<string, string, CancellationToken, string> InstallAction { get; init; } = (directory, _, _) => directory;

        public string Install(string directory, string package, IProgress<CatalogProgress> progress, CancellationToken cancellationToken) => InstallAction(directory, package, cancellationToken);

        public CatalogAvailability Status(string catalogPath, string indexPath) => new("stars-deep-gaia17.bin", "blind-gaia16.idx");
    }

    private sealed class TrackingEngine : ISeizaEngine
    {
        public bool Disposed { get; private set; }

        public SolveResponse Solve(ushort[] pixels, int width, int height, SolveRequest request, CancellationToken cancellationToken) => new();

        public void Dispose() => Disposed = true;
    }
}
