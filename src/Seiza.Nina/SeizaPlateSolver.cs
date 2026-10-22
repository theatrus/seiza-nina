using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;

namespace Seiza.Nina;

internal sealed class SeizaPlateSolver(SeizaSettings settings, SeizaEngineHost host) : IPlateSolver
{
    public async Task<PlateSolveResult> SolveAsync(IImageData source, PlateSolveParameter parameter,
        IProgress<ApplicationStatus>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parameter);
        var options = settings.Snapshot();
        var request = SolveRequest.Create(source, parameter, options);
        var pixels = source.Data.FlatArray;
        var width = source.Properties.Width;
        var height = source.Properties.Height;
        if (width <= 0 || height <= 0 || pixels is null || (long)width * height != pixels.LongLength)
        {
            throw new ArgumentException("Seiza requires a complete 16-bit mono or Bayer image.", nameof(source));
        }

        try
        {
            progress?.Report(new() { Status = request.Center is null ? "Blind solving with Seiza" : "Solving with Seiza" });
            var response = await host.SolveAsync(pixels, width, height, request, options, cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                Logger.Info($"Seiza solve failed: {response.Message}");
            }

            return response.ToResult();
        }
        finally
        {
            progress?.Report(new() { Status = string.Empty });
        }
    }
}
