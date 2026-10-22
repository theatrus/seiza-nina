using System.ComponentModel.Composition;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;

namespace Seiza.Nina;

[Export(typeof(IPlateSolverProvider))]
public sealed class SeizaSolverProvider : IPlateSolverProvider
{
    [ImportingConstructor]
    public SeizaSolverProvider(IProfileService profileService)
    {
        Settings = SeizaRuntime.SettingsFor(profileService);
    }

    public string Id => "fyi.seiza.nina.plate-solver";

    public string Name => "Seiza";

    public bool SupportsPrimary => true;

    public bool SupportsBlind => true;

    public SeizaSettings Settings { get; }

    public IPlateSolver CreateSolver(IPlateSolveSettings settings) => new SeizaPlateSolver(Settings, SeizaRuntime.Host);
}
