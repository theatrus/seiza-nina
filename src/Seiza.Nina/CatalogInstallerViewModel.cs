using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Seiza.Nina;

public sealed partial class CatalogInstallerViewModel : ObservableObject
{
    private readonly SeizaEngineHost host;
    private readonly ICatalogInstaller installer;

    internal CatalogInstallerViewModel(SeizaEngineHost host, ICatalogInstaller installer)
    {
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
    private string status = "Choose a package. Seiza installs and verifies catalogs in its standard location.";

    [ObservableProperty]
    private string catalogStatus = "Select Refresh to check local catalogs.";

    [ObservableProperty]
    private double percent;

    [ObservableProperty]
    private bool isIndeterminate;

    internal void ProfileChanged() => CatalogStatus = "Select Refresh to check local catalogs.";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var available = await Task.Run(installer.Status);
            CatalogStatus = available.Summary;
        }
        catch (Exception error) { CatalogStatus = $"Could not check catalogs: {error.Message}"; }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        var package = Package.Id;
        try
        {
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
            await host.RunMaintenanceAsync(token => installer.Install(package, progress, token), cancellationToken);
            await RefreshAsync();
            Status = "Catalog installation complete. Seiza will discover these catalogs automatically.";
            Percent = 100;
        }
        catch (OperationCanceledException) { Status = "Installation cancelled. Completed cache downloads are kept for reuse."; }
        catch (Exception error) { Status = $"Installation failed: {error.Message}"; }
        finally { IsInstalling = false; IsIndeterminate = false; }
    }
}
