using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.ViewModel.Interfaces;
using NINA.WPF.Base.ViewModel;

internal static class Program
{
    private static string root = null!;
    private static readonly List<object> checks = [];

    [STAThread]
    private static int Main()
    {
        root = RequiredEnvironment("SEIZA_NINA_E2E_ROOT");
        Directory.CreateDirectory(root);
        // Set NINA's supported application/profile roots before composing its UI.
        // This process has its own config identity and never opens the user's profiles.
        CoreUtil.APPLICATIONTEMPPATH = Path.Combine(root, "NINA");
        Directory.CreateDirectory(CoreUtil.APPLICATIONTEMPPATH);
        ProfileService.PROFILEFOLDER = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Profiles");
        var pluginDirectory = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Plugins", "3.0.0", "Seiza");
        Directory.CreateDirectory(pluginDirectory);
        var pluginSource = RequiredEnvironment("SEIZA_NINA_E2E_PLUGIN");
        foreach (var name in new[] { "Seiza.Nina.dll", "seiza_nina_native.dll" })
            File.Copy(Path.Combine(pluginSource, name), Path.Combine(pluginDirectory, name), true);

        var ninaSettings = typeof(NINA.App).Assembly.GetType("NINA.Properties.Settings", true)!;
        var configuration = ninaSettings.GetProperty("Default")!.GetValue(null)!;
        ninaSettings.GetProperty("UpdateSettings")!.SetValue(configuration, false);
        ninaSettings.GetProperty("UseSavedProfileSelection")!.SetValue(configuration, true);
        ninaSettings.GetProperty("DatabaseLocation")!.SetValue(configuration, Path.Combine(root, "NINA.sqlite"));

        var app = new NINA.App();
        app.InitializeComponent();
        var exitCode = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += async (_, _) =>
        {
            if (app.MainWindow?.DataContext is not IMainWindowVM main) return;
            timer.Stop();
            app.MainWindow.WindowState = WindowState.Normal;
            app.MainWindow.Width = 1400;
            app.MainWindow.Height = 1000;
            app.MainWindow.Title = "N.I.N.A. — Seiza end-to-end test";
            try
            {
                await RunChecks(app, main);
                WriteReport("passed");
                app.MainWindow.Title = "N.I.N.A. — Seiza end-to-end tests passed";
            }
            catch (Exception error)
            {
                checks.Add(new { name = "failure", error = error.ToString() });
                WriteReport("failed");
                exitCode = 1;
            }
            if (Environment.GetEnvironmentVariable("SEIZA_NINA_E2E_STAY_OPEN") != "1") app.MainWindow.Close();
        };
        timer.Start();
        try { app.Run(); }
        catch (Exception error) { checks.Add(new { name = "startup", error = error.ToString() }); WriteReport("failed"); return 1; }
        return exitCode;
    }

    private static async Task RunChecks(NINA.App app, IMainWindowVM main)
    {
        const string providerId = "fyi.seiza.nina.plate-solver";
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!PlateSolverFactory.Providers.Any(p => p.Id == providerId)
            || !main.PluginsVM.InstalledPlugins.Any(p => p.Key.Name == "Seiza Plate Solver"))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("NINA did not discover the deployed Seiza plugin.");
            await Task.Delay(100);
        }
        var installed = main.PluginsVM.InstalledPlugins.Single(p => p.Key.Name == "Seiza Plate Solver");
        Require(installed.Value, "NINA plugin manifest initialized");
        var profiles = (IProfileService)app.Resources["ProfileService"];
        profiles.ActiveProfile.Name = "Seiza end-to-end test";
        var settings = profiles.ActiveProfile.PlateSolveSettings;
        settings.PlateSolverProviderId = providerId;
        settings.BlindSolverProviderId = providerId;
        settings.DownSampleFactor = 1;
        settings.MaxObjects = 600;
        profiles.ActiveProfile.TelescopeSettings.FocalLength = 100;
        profiles.ActiveProfile.CameraSettings.PixelSize = 4.848136811;

        var fixture = RequiredEnvironment("SEIZA_NINA_E2E_FIXTURE");
        var raw = File.ReadAllBytes(Path.Combine(fixture, "image.raw"));
        var pixels = new ushort[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, pixels, 0, raw.Length);
        var fitsPath = Path.Combine(root, "artificial-star-field.fits");
        using (var stream = File.Create(fitsPath)) new FITS(pixels, 1024, 768).Write(stream);
        var imageFactory = (IImageDataFactory)main.FramingAssistantVM.GetType()
            .GetField("imageDataFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main.FramingAssistantVM)!;
        var image = await imageFactory.CreateFromFile(fitsPath, 16, false);
        Require(image.Data.FlatArray.SequenceEqual(pixels), "NINA FITS write/load preserves original pixels");
        main.AppVM.ChangeTab(ApplicationTab.IMAGING);
        await main.ImagingVM.PrepareImage(image, new PrepareImageParameters(true, false), CancellationToken.None);
        Require(main.ImagingVM.ImageControl.Image is not null, "NINA image preparation and display");

        var primary = PlateSolverFactory.GetPlateSolver(settings);
        var blind = PlateSolverFactory.GetBlindSolver(settings);
        var solver = new ImageSolver(primary, blind);
        var parameter = Parameter();
        var result = await solver.Solve(image, parameter, null!, CancellationToken.None);
        Validate(result, "NINA primary orchestration");
        parameter.Coordinates = null;
        Validate(await solver.Solve(image, parameter, null!, CancellationToken.None), "NINA blind orchestration");
        parameter.Coordinates = new Coordinates(0, -45, Epoch.J2000, Coordinates.RAType.Degrees);
        parameter.BlindFailoverEnabled = false;
        Require(!(await solver.Solve(image, parameter, null!, CancellationToken.None)).Success, "Wrong primary hint returns a miss");
        parameter.BlindFailoverEnabled = true;
        Validate(await solver.Solve(image, parameter, null!, CancellationToken.None), "NINA automatic blind failover");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await solver.Solve(image, Parameter(), null!, cancelled.Token); throw new InvalidOperationException("Cancellation was ignored"); }
            catch (OperationCanceledException) { Require(true, "NINA cancellation propagates"); }
        }
        Validate(await solver.Solve(image, Parameter(), null!, CancellationToken.None), "NINA solves again after cancellation");

        // This is the real image-context-menu command, including NINA's status window.
        await main.ImagingVM.ImageControl.PlateSolveImageCommand.ExecuteAsync(null!);
        var status = (PlateSolvingStatusVM)main.ImagingVM.ImageControl.GetType()
            .GetField("plateSolveStatusVM", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main.ImagingVM.ImageControl)!;
        Validate(status.PlateSolveResult, "NINA image plate-solve command and result window");
        var resultWindow = app.MainWindow.OwnedWindows.Cast<Window>().Single(w => ReferenceEquals(w.Content, status));
        SaveScreenshot(resultWindow, "nina-solve-result.png");

        var provider = PlateSolverFactory.Providers.Single(p => p.Id == providerId);
        var pluginSettings = provider.GetType().GetProperty("Settings")!.GetValue(provider)!;
        Require(new[] { "CatalogPath", "IndexPath", "CatalogInstallDirectory" }.All(n => pluginSettings.GetType().GetProperty(n) is null),
            "No custom path settings; Seiza default discovery used");
        if (Environment.GetEnvironmentVariable("SEIZA_NINA_E2E_INSTALL") == "1")
        {
            var catalogs = pluginSettings.GetType().GetProperty("Catalogs")!.GetValue(pluginSettings)!;
            var packages = (System.Collections.IEnumerable)catalogs.GetType().GetProperty("Packages")!.GetValue(catalogs)!;
            var compact = packages.Cast<object>().Single(p => (string)p.GetType().GetProperty("Id")!.GetValue(p)! == "compact");
            catalogs.GetType().GetProperty("SelectedPackage")!.SetValue(catalogs, compact);
            var command = (CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)catalogs.GetType().GetProperty("InstallCommand")!.GetValue(catalogs)!;
            await command.ExecuteAsync(null).WaitAsync(TimeSpan.FromMinutes(5));
            var message = (string)catalogs.GetType().GetProperty("Status")!.GetValue(catalogs)!;
            Require(message.StartsWith("Catalog installation complete", StringComparison.Ordinal), "Real catalog installation through NINA: " + message);
            Require(File.Exists(Path.Combine(fixture, "stars-lite-tycho2.bin")), "Installer publishes to Seiza default directory");
        }
        profiles.ActiveProfile.Save();
        main.PluginsVM.SelectedInstalledPlugin = installed;
        main.AppVM.ChangeTab(ApplicationTab.PLUGINS);
        await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        app.MainWindow.UpdateLayout();
        var settingsView = Descendants(app.MainWindow).OfType<UserControl>()
            .Single(v => v.GetType().FullName == "Seiza.Nina.SettingsView");
        Require(ReferenceEquals(settingsView.DataContext, pluginSettings)
            && ((Button)settingsView.FindName("InstallButton")).Command is not null,
            "NINA plugin page displays live catalog installation controls");
        Require(new[] { "CatalogPath", "IndexPath", "InstallDirectory" }.All(n => settingsView.FindName(n) is null),
            "NINA plugin page hides all catalog path fields");
        SaveScreenshot(app.MainWindow, "nina-plugin-page.png");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void SaveScreenshot(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),
            (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(root, name));
        png.Save(stream);
    }

    private static PlateSolveParameter Parameter() => new()
    {
        Coordinates = new(120.1, 25.1, Epoch.J2000, Coordinates.RAType.Degrees),
        PixelSize = 4.848136811, FocalLength = 100, SearchRadius = 1,
        DownSampleFactor = 1, MaxObjects = 600, BlindFailoverEnabled = true
    };

    private static void Validate(PlateSolveResult result, string name)
    {
        Require(result is { Success: true } && Math.Abs(result.Coordinates.RADegrees - 120) < 0.002
            && Math.Abs(result.Coordinates.Dec - 25) < 0.002 && Math.Abs(result.Pixscale - 10) < 0.02, name);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add(new { name, passed = true });
    }

    private static void WriteReport(string status) => File.WriteAllText(Path.Combine(root, "report.json"),
        JsonSerializer.Serialize(new { status, ninaVersion = CoreUtil.Version, checks }, new JsonSerializerOptions { WriteIndented = true }));

    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before starting the NINA end-to-end host.");
}
