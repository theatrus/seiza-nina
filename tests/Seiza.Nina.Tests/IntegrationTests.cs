using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using Moq;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NUnit.Framework;

namespace Seiza.Nina.Tests;

[TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
public class IntegrationTests
{
    [TestCase(false), TestCase(true)]
    public async Task SolvesThroughUpstreamFactory(bool blind)
    {
        var provider = new SeizaSolverProvider(TestData.Profile().Object);
        provider.Settings.CatalogPath = TestData.Fixture;
        provider.Settings.MinimumScale = 8;
        provider.Settings.MaximumScale = 12;
        PlateSolverFactory.RegisterProviders([provider]);
        var settings = new PlateSolveSettings
        {
            PlateSolverProviderId = provider.Id,
            BlindSolverProviderId = provider.Id
        };
        try
        {
            var solver = blind ? PlateSolverFactory.GetBlindSolver(settings) : PlateSolverFactory.GetPlateSolver(settings);
            var result = await solver.SolveAsync(TestData.Image(), TestData.Parameter(blind), null!, CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(result.Success, Is.True);
                Assert.That(result.Coordinates.RADegrees, Is.EqualTo(120).Within(0.002));
                Assert.That(result.Coordinates.Dec, Is.EqualTo(25).Within(0.002));
                Assert.That(result.Pixscale, Is.EqualTo(10).Within(0.02));
                Assert.That(result.PositionAngle, Is.EqualTo(330).Within(0.2));
                Assert.That(result.Flipped, Is.False);
            });
        }
        finally
        {
            PlateSolverFactory.RegisterProviders([]);
        }
    }

    [Test]
    public void SettingsFollowProfileSwitchAndNotifyTheView()
    {
        var profile = TestData.Profile();
        var settings = new SeizaSettings(profile.Object) { CatalogPath = "first", TimeLimitSeconds = 15 };
        var first = profile.Object.ActiveProfile.PluginSettings;
        var second = new PluginSettings();
        second.SetValue(SeizaSettings.PluginId, nameof(SeizaSettings.CatalogPath), "second");
        var changed = false;
        settings.PropertyChanged += (_, args) => changed |= string.IsNullOrEmpty(args.PropertyName);
        profile.SetupGet(x => x.ActiveProfile.PluginSettings).Returns(second);
        profile.Raise(x => x.ProfileChanged += null, EventArgs.Empty);
        Assert.That(settings.CatalogPath, Is.EqualTo("second"));
        Assert.That(settings.TimeLimitSeconds, Is.EqualTo(60));
        Assert.That(changed, Is.True);
        settings.TimeLimitSeconds = 90;
        profile.SetupGet(x => x.ActiveProfile.PluginSettings).Returns(first);
        Assert.That(settings.TimeLimitSeconds, Is.EqualTo(15));
    }

    [Test]
    public void SeparateMefContainersDiscoverManifestProviderAndSettingsTemplate()
    {
        using var catalog = new AssemblyCatalog(typeof(SeizaPlugin).Assembly);
        using var manifests = new CompositionContainer(catalog);
        using var extensions = new CompositionContainer(catalog);
        var profiles = TestData.Profile().Object;
        manifests.ComposeExportedValue(profiles);
        extensions.ComposeExportedValue(profiles);
        var manifest = manifests.GetExportedValue<IPluginManifest>()!;
        var provider = extensions.GetExportedValue<IPlateSolverProvider>()!;
        var resources = extensions.GetExportedValue<ResourceDictionary>()!;
        Assert.That(manifest.MinimumApplicationVersion.ToString(), Does.Contain("3.3.0.1068"));
        Assert.That(provider.SupportsPrimary && provider.SupportsBlind, Is.True);
        Assert.That(manifest.Identifier, Is.EqualTo(SeizaSettings.PluginId.ToString()));
        Assert.That(((SeizaPlugin)manifest).Settings, Is.SameAs(((SeizaSolverProvider)provider).Settings));
        Assert.That(resources["Seiza Plate Solver_Options"], Is.InstanceOf<DataTemplate>());
        var template = (DataTemplate)resources[new DataTemplateKey(provider.GetType())];
        var content = (FrameworkElement)template.LoadContent();
        content.DataContext = provider;
        var window = new Window { Content = content, Width = 800, Height = 450, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            window.UpdateLayout();
            var settingsView = (SettingsView)((Grid)content).Children[0];
            var textBox = (TextBox)settingsView.FindName("CatalogPath");
            var packages = (ComboBox)settingsView.FindName("Package");
            Assert.That(packages.SelectedItem, Is.EqualTo(((SeizaSolverProvider)provider).Settings.Catalogs.Packages[0]));
            Assert.That(((Button)settingsView.FindName("InstallButton")).Command, Is.Not.Null);
            Assert.That(textBox.GetBindingExpression(TextBox.TextProperty)!.HasError, Is.False);
            ((SeizaSolverProvider)provider).Settings.CatalogPath = "C:\\test catalogs";
            textBox.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
            Assert.That(textBox.Text, Is.EqualTo("C:\\test catalogs"));
        }
        finally { window.Close(); }
    }

    [Test]
    public async Task PackagedAssemblyLoadsAndSolvesInNinasActualPluginLoadContext()
    {
        // A separate process keeps WPF's global pack-URI cache from mixing assemblies
        // with identical identities in the default and plugin load contexts.
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(TestData.Root, "tests", "Seiza.Nina.Smoke", "bin", "Release", "net10.0-windows", "Seiza.Nina.Smoke.dll"));
        start.ArgumentList.Add(typeof(SeizaPlugin).Assembly.Location);
        start.ArgumentList.Add(TestData.Fixture);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert.That(process.ExitCode, Is.Zero, (await stdout) + (await stderr));
    }

    [Test]
    public void BinningIsAppliedExactlyOnceAndBayerDetectionUsesEvenBlocks()
    {
        var parameter = TestData.Parameter();
        parameter.Binning = 2;
        parameter.DownSampleFactor = 3;
        var request = SolveRequest.Create(TestData.Image(bayer: true), parameter, TestData.Options);
        Assert.That(request.Scale, Is.EqualTo(20).Within(0.001));
        Assert.That(request.Downsample, Is.EqualTo(4));
        Assert.That(request.Center![0], Is.EqualTo(120.1).Within(1e-8));
    }
}
