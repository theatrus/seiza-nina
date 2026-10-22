using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Seiza.Nina;

internal interface ICatalogInstaller
{
    string Install(string package, IProgress<CatalogProgress> progress, CancellationToken cancellationToken);
    CatalogAvailability Status();
}

public sealed record CatalogPackage(string Id, string Name, string Description);

internal sealed record CatalogProgress(string Message, ulong Downloaded = 0, ulong Total = 0);

internal sealed record CatalogAvailability(string? StarCatalog, string? BlindIndex)
{
    public string Summary => StarCatalog is null ? "No star catalog found. Install a package above to start solving."
        : BlindIndex is null ? $"Primary solving ready: {Path.GetFileName(StarCatalog)}. Install a blind package to enable blind solving."
        : $"Primary and blind solving ready: {Path.GetFileName(StarCatalog)} + {Path.GetFileName(BlindIndex)}.";
}

internal sealed class NativeCatalogInstaller : ICatalogInstaller
{
    private sealed record InstallResult(bool Success, string? Directory, string? Message);

    public string Install(string package, IProgress<CatalogProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var signal = new CancelHandle(NativeMethods.CreateCancel());
        if (signal.IsInvalid) throw new InvalidOperationException("Seiza could not allocate installation cancellation state.");
        using var registration = cancellationToken.UnsafeRegister(static state => ((CancelHandle)state!).Cancel(), signal);
        long lastReport = 0;
        NativeMethods.CatalogProgress callback = (json, _) =>
        {
            // Exceptions must never unwind through a Rust/C callback.
            try
            {
                var value = JsonSerializer.Deserialize<CatalogProgress>(Marshal.PtrToStringUTF8(json)!, NativeSeizaEngine.Json);
                if (value is null) return;
                var now = Stopwatch.GetTimestamp();
                if (value.Total > 0 && (now - lastReport) < Stopwatch.Frequency / 10) return;
                lastReport = now;
                progress.Report(value);
            }
            catch (Exception) { /* A progress subscriber cannot interrupt native cleanup. */ }
        };
        var response = NativeMethods.InstallCatalogs(JsonSerializer.Serialize(new { package }, NativeSeizaEngine.Json), signal, callback, 0);
        GC.KeepAlive(callback);
        var text = NativeSeizaEngine.ReadAndFree(response);
        cancellationToken.ThrowIfCancellationRequested();
        var result = JsonSerializer.Deserialize<InstallResult>(text, NativeSeizaEngine.Json);
        if (result is not { Success: true, Directory: not null })
        {
            throw new InvalidOperationException(result?.Message ?? "Seiza returned no installation result.");
        }
        return result.Directory;
    }

    public CatalogAvailability Status()
    {
        var json = NativeMethods.CatalogStatus("{\"catalogPath\":\"\",\"indexPath\":\"\"}");
        return JsonSerializer.Deserialize<CatalogAvailability>(NativeSeizaEngine.ReadAndFree(json), NativeSeizaEngine.Json)
            ?? throw new InvalidDataException("Seiza returned no catalog status.");
    }
}
