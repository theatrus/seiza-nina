using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NINA.Profile.Interfaces;

namespace Seiza.Nina;

public sealed partial class CatalogInstallerViewModel : ObservableObject
{
    private readonly SeizaSettings settings;
    private readonly IProfileService profiles;
    private readonly SeizaEngineHost host;
    private readonly ICatalogInstaller installer;

    internal CatalogInstallerViewModel(SeizaSettings settings, IProfileService profiles, SeizaEngineHost host, ICatalogInstaller installer)
    {
        this.settings = settings;
        this.profiles = profiles;
        this.host = host;
        this.installer = installer;
    }

    private static readonly CatalogPackage[] packages =
    [
        new("standard", "Standard — Gaia G17 + blind index", "Recommended for primary and blind solving. About 3.2 GB installed; downloaded files are cached for reuse."),
        new("compact", "Compact — Tycho-2, primary only", "Small catalog for wide fields with a pointing hint. About 26 MB installed. Does not include a blind index."),
        new("deep", "Deep — Gaia G20 + blind index", "For very faint, narrow fields. About 10.3 GB installed; requires substantially more disk space and download time.")
    ];

    public IReadOnlyList<CatalogPackage> Packages => packages;

    [ObservableProperty]
    private CatalogPackage selectedPackage = packages[0];

    public CatalogPackage Package => SelectedPackage;

    partial void OnSelectedPackageChanged(CatalogPackage value) => OnPropertyChanged(nameof(Package));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool isInstalling;

    public bool IsIdle => !IsInstalling;

    [ObservableProperty]
    private string status = "Choose a package and installation folder. Catalogs are downloaded and SHA-256 verified by Seiza.";

    [ObservableProperty]
    private string catalogStatus = "Select Refresh to check local catalogs.";

    [ObservableProperty]
    private double percent;

    [ObservableProperty]
    private bool isIndeterminate;

    internal void ProfileChanged() => CatalogStatus = "Active profile changed. Select Refresh to check its catalogs.";

    [RelayCommand]
    private void BrowseInstallDirectory()
    {
        var picker = new OpenFolderDialog { Title = "Choose the Seiza catalog installation folder" };
        if (Directory.Exists(settings.CatalogInstallDirectory)) picker.InitialDirectory = settings.CatalogInstallDirectory;
        if (picker.ShowDialog() == true) settings.CatalogInstallDirectory = picker.FolderName;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var catalogPath = settings.CatalogPath;
        var indexPath = settings.IndexPath;
        var profileId = profiles.ActiveProfile.Id;
        try
        {
            var available = await Task.Run(() => installer.Status(catalogPath, indexPath));
            if (profiles.ActiveProfile.Id == profileId) CatalogStatus = available.Summary;
        }
        catch (Exception error) { CatalogStatus = $"Could not check catalogs: {error.Message}"; }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        var profileId = profiles.ActiveProfile.Id;
        var directory = settings.CatalogInstallDirectory;
        var package = Package.Id;
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                throw new InvalidOperationException("Choose an absolute installation folder first.");
            if (File.Exists(directory)) throw new InvalidOperationException("The installation folder points to a file.");

            IsInstalling = true;
            IsIndeterminate = true;
            Percent = 0;
            Status = "Waiting for active solves to finish. Solves wait while catalogs are installed.";
            var progress = new Progress<CatalogProgress>(value =>
            {
                if (!IsInstalling) return;
                IsIndeterminate = value.Total == 0;
                Percent = value.Total == 0 ? 0 : 100.0 * value.Downloaded / value.Total;
                Status = value.Total == 0 ? value.Message : $"{value.Message} — {value.Downloaded / 1048576.0:N1} / {value.Total / 1048576.0:N1} MiB";
            });
            var installed = await host.RunMaintenanceAsync(token => installer.Install(directory, package, progress, token), cancellationToken);
            if (profiles.ActiveProfile.Id == profileId)
            {
                settings.CatalogPath = installed;
                settings.IndexPath = "";
                await RefreshAsync();
                Status = $"Installed in {installed}. This profile now uses these catalogs.";
            }
            else
            {
                Status = $"Installed in {installed}. The active profile changed; select this folder in the intended profile to use it.";
            }
            Percent = 100;
        }
        catch (OperationCanceledException) { Status = "Installation cancelled. Completed cache downloads are kept for reuse."; }
        catch (Exception error) { Status = $"Installation failed: {error.Message}"; }
        finally { IsInstalling = false; IsIndeterminate = false; }
    }
}
