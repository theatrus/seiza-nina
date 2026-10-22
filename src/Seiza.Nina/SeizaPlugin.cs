using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace Seiza.Nina;

[Export(typeof(IPluginManifest))]
public sealed class SeizaPlugin : PluginBase
{
    // NINA creates manifests and providers in separate MEF containers.
    // Engine ownership therefore lives in the assembly's shared runtime.
    public override Task Teardown() => SeizaRuntime.Host.DisposeAsync().AsTask();
}
