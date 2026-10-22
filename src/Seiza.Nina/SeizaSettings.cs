using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using NINA.Profile;
using NINA.Profile.Interfaces;

namespace Seiza.Nina;

public sealed class SeizaSettings : INotifyPropertyChanged
{
    internal static readonly Guid PluginId = new("66d7411d-b4b6-470b-9f34-d1578699c834");
    private readonly PluginOptionsAccessor options;

    public SeizaSettings(IProfileService profileService)
    {
        options = new PluginOptionsAccessor(profileService, PluginId);
        Catalogs = new CatalogInstallerViewModel(SeizaRuntime.Host, new NativeCatalogInstaller());
        WeakEventManager<IProfileService, EventArgs>.AddHandler(profileService, nameof(IProfileService.ProfileChanged), ProfileChanged);
    }

    public CatalogInstallerViewModel Catalogs { get; }

    public double TimeLimitSeconds
    {
        get => options.GetValueDouble(nameof(TimeLimitSeconds), 60);
        set { options.SetValueDouble(nameof(TimeLimitSeconds), value); Changed(); }
    }

    public double ScaleTolerance
    {
        get => options.GetValueDouble(nameof(ScaleTolerance), 0.25);
        set { options.SetValueDouble(nameof(ScaleTolerance), value); Changed(); }
    }

    public double MinimumScale
    {
        get => options.GetValueDouble(nameof(MinimumScale), 0.1);
        set { options.SetValueDouble(nameof(MinimumScale), value); Changed(); }
    }

    public double MaximumScale
    {
        get => options.GetValueDouble(nameof(MaximumScale), 20);
        set { options.SetValueDouble(nameof(MaximumScale), value); Changed(); }
    }

    internal SolveOptions Snapshot()
    {
        // Catalog discovery and installation use Seiza's own defaults. Ignore
        // obsolete plugin path overrides retained in profiles from earlier builds.
        var value = new SolveOptions("", "", TimeLimitSeconds, ScaleTolerance, MinimumScale, MaximumScale);
        value.Validate();
        return value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    private void ProfileChanged(object? sender, EventArgs args)
    {
        Catalogs.ProfileChanged();
        Changed("");
    }
}

internal sealed record SolveOptions(string CatalogPath, string IndexPath, double TimeLimitSeconds,
    double ScaleTolerance, double MinimumScale, double MaximumScale)
{
    public void Validate()
    {
        if (!double.IsFinite(TimeLimitSeconds) || TimeLimitSeconds is <= 0 or > 3600 ||
            !double.IsFinite(ScaleTolerance) || ScaleTolerance is <= 0 or >= 1 ||
            !double.IsFinite(MinimumScale) || MinimumScale <= 0 ||
            !double.IsFinite(MaximumScale) || MaximumScale < MinimumScale)
        {
            throw new InvalidOperationException("Seiza: set a time limit from 0 to 3600 seconds (exclusive of 0), a scale tolerance between 0 and 1, and positive ordered blind scale bounds in Options > Plate Solving.");
        }
    }
}
