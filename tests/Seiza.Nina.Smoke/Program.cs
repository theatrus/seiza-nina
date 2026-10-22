using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using Moq;
using NINA.Astrometry;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Keep WPF composition on the main STA; no user application state is touched.
        try { Run(args).GetAwaiter().GetResult(); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    }

    private static async Task Run(string[] args)
    {
        var assemblyPath = Path.GetFullPath(args[0]);
        var fixture = Path.GetFullPath(args[1]);
        var contextType = typeof(PluginLoader).GetNestedType("PluginAssemblyLoadContext", BindingFlags.NonPublic)!;
        var context = (AssemblyLoadContext)Activator.CreateInstance(contextType, "Seiza smoke test", assemblyPath, false)!;
        var assembly = context.LoadFromAssemblyPath(assemblyPath);
        var profile = new Mock<IProfileService>();
        profile.SetupGet(x => x.ActiveProfile.PluginSettings).Returns(new PluginSettings());
        using var catalog = new AssemblyCatalog(assembly);
        using var manifests = new CompositionContainer(catalog);
        using var extensions = new CompositionContainer(catalog);
        extensions.ComposeExportedValue(profile.Object);
        var manifest = manifests.GetExportedValue<IPluginManifest>()!;
        await manifest.Initialize();
        var provider = extensions.GetExportedValue<IPlateSolverProvider>()!;
        var settings = provider.GetType().GetProperty("Settings")!.GetValue(provider)!;
        settings.GetType().GetProperty("CatalogPath")!.SetValue(settings, fixture);
        settings.GetType().GetProperty("MinimumScale")!.SetValue(settings, 8.0);
        settings.GetType().GetProperty("MaximumScale")!.SetValue(settings, 12.0);
        var resources = extensions.GetExportedValue<ResourceDictionary>()!;
        var template = (DataTemplate)resources[new DataTemplateKey(provider.GetType())];
        var view = (FrameworkElement)template.LoadContent();
        view.DataContext = provider;
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));
        var image = new Mock<IImageData>();
        var bytes = File.ReadAllBytes(Path.Combine(fixture, "image.raw"));
        var pixels = new ushort[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pixels, 0, bytes.Length);
        image.SetupGet(x => x.Data.FlatArray).Returns(pixels);
        image.SetupGet(x => x.Properties).Returns(new ImageProperties(1024, 768, 16, false, 0, 0));
        PlateSolverFactory.RegisterProviders([provider]);
        var plateSettings = new PlateSolveSettings { PlateSolverProviderId = provider.Id, BlindSolverProviderId = provider.Id };
        try
        {
            foreach (var blind in new[] { false, true })
            {
                var parameter = new PlateSolveParameter
                {
                    Coordinates = blind ? null : new Coordinates(120.1, 25.1, Epoch.J2000, Coordinates.RAType.Degrees),
                    PixelSize = 4.848136811, FocalLength = 100, SearchRadius = 1, DownSampleFactor = 1
                };
                var solver = blind ? PlateSolverFactory.GetBlindSolver(plateSettings) : PlateSolverFactory.GetPlateSolver(plateSettings);
                var result = await solver.SolveAsync(image.Object, parameter, null!, CancellationToken.None);
                if (!result.Success || Math.Abs(result.Coordinates.RADegrees - 120) > 0.002)
                    throw new InvalidDataException("Plugin-context solve did not recover the fixture field.");
            }
        }
        finally { await manifest.Teardown(); PlateSolverFactory.RegisterProviders([]); }

        try
        {
            await provider.CreateSolver(plateSettings).SolveAsync(image.Object, new PlateSolveParameter(), null!, CancellationToken.None);
            throw new InvalidOperationException("Teardown failed to stop the runtime.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("Primary + blind solves, MEF, embedded WPF settings and teardown passed in NINA's plugin load context.");
    }
}
