using System.IO;
using Moq;
using NINA.Astrometry;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.Profile;
using NINA.Profile.Interfaces;

namespace Seiza.Nina.Tests;

internal static class TestData
{
    internal static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cargo.toml"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot locate test fixture root.");
        }
    }

    internal static string Fixture => Path.Combine(Root, "artifacts", "test-fixture");

    internal static ushort[] Pixels()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Fixture, "image.raw"));
        var pixels = new ushort[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pixels, 0, bytes.Length);
        return pixels;
    }

    internal static Mock<IProfileService> Profile()
    {
        var profile = new Mock<IProfileService>();
        profile.SetupGet(x => x.ActiveProfile.PluginSettings).Returns(new PluginSettings());
        return profile;
    }

    internal static IImageData Image(ushort[]? pixels = null, bool bayer = false)
    {
        var image = new Mock<IImageData>();
        image.SetupGet(x => x.Properties).Returns(new ImageProperties(1024, 768, 16, bayer, 0, 0));
        image.SetupGet(x => x.Data.FlatArray).Returns(pixels ?? Pixels());
        return image.Object;
    }

    internal static PlateSolveParameter Parameter(bool blind = false) => new()
    {
        Coordinates = blind ? null : new Coordinates(120.1, 25.1, Epoch.J2000, Coordinates.RAType.Degrees),
        PixelSize = 4.848136811,
        FocalLength = 100,
        SearchRadius = 1,
        DownSampleFactor = 1,
        MaxObjects = 600
    };

    internal static SolveOptions Options => new(Fixture, "", 30, 0.25, 8, 12);

    internal static SolveRequest Request(bool blind = false) => SolveRequest.Create(Image(), Parameter(blind), Options);
}
