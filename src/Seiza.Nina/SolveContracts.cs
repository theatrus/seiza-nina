using NINA.Astrometry;
using System.IO;
using NINA.Image.Interfaces;
using NINA.PlateSolving;

namespace Seiza.Nina;

internal sealed record SolveRequest(double[]? Center, double Scale, double SearchRadius,
    double ScaleTolerance, double MinScale, double MaxScale, double TimeLimitSeconds, int Downsample, int MaxStars)
{
    public static SolveRequest Create(IImageData image, PlateSolveParameter parameter, SolveOptions options)
    {
        double[]? center = null;
        var scale = 0.0;
        if (parameter.Coordinates is { } coordinates)
        {
            scale = AstroUtil.ArcsecPerPixel(parameter.PixelSize, parameter.FocalLength);
            if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(coordinates.RADegrees) ||
                !double.IsFinite(coordinates.Dec) || coordinates.Dec is < -90 or > 90 ||
                !double.IsFinite(parameter.SearchRadius) || parameter.SearchRadius is < 0 or > 180)
            {
                throw new ArgumentException("Seiza primary solving requires valid pointing, pixel size, focal length and search radius.", nameof(parameter));
            }

            center = [((coordinates.RADegrees % 360) + 360) % 360, coordinates.Dec];
        }

        // PlateSolveParameter.PixelSize already includes capture binning.
        // Detection downsampling restores centroids to original pixels in the native adapter.
        var downsample = Math.Clamp(parameter.DownSampleFactor, 1, 8);
        if (image.Properties.IsBayered)
        {
            downsample = Math.Max(2, downsample);
            if (downsample % 2 != 0) downsample++;
        }

        return new(center, scale, parameter.SearchRadius, options.ScaleTolerance,
            options.MinimumScale, options.MaximumScale, options.TimeLimitSeconds, downsample,
            parameter.MaxObjects > 0 ? Math.Clamp(parameter.MaxObjects, 32, 2000) : 600);
    }
}

internal sealed class SolveResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public double[] Center { get; init; } = [];
    public double Scale { get; init; }
    public double Radius { get; init; }
    public double[] Crpix { get; init; } = [];
    public double[][] Cd { get; init; } = [];
    public int MatchedStars { get; init; }
    public double RmsArcsec { get; init; }

    public PlateSolveResult ToResult()
    {
        if (!Success) return new() { Success = false };

        if (MatchedStars < 6 || Center is not { Length: 2 } || Crpix is not { Length: 2 } ||
            Cd is not { Length: 2 } || Cd[0] is not { Length: 2 } || Cd[1] is not { Length: 2 } ||
            Center.Concat(Crpix).Concat(Cd[0]).Concat(Cd[1]).Any(x => !double.IsFinite(x)) ||
            Center[0] is < 0 or >= 360 || Center[1] is < -90 or > 90 ||
            !double.IsFinite(Scale) || Scale <= 0 || !double.IsFinite(Radius) || Radius <= 0 ||
            !double.IsFinite(RmsArcsec) || RmsArcsec < 0 || Math.Abs(Cd[0][0] * Cd[1][1] - Cd[0][1] * Cd[1][0]) < 1e-20)
        {
            throw new InvalidDataException("Seiza returned an invalid or unconfirmed plate solution.");
        }

        var wcs = new WorldCoordinateSystem(Center[0], Center[1], Crpix[0], Crpix[1],
            Cd[0][0], Cd[0][1], Cd[1][0], Cd[1][1]);
        return new()
        {
            Success = true,
            Coordinates = new(Center[0], Center[1], Epoch.J2000, Coordinates.RAType.Degrees),
            Pixscale = Scale,
            Radius = Radius,
            // Same image-axis convention used by NINA's ASTAP WCS adapter.
            PositionAngle = 360 - (wcs.Rotation - 180),
            Flipped = !wcs.Flipped
        };
    }
}
