# Seiza for N.I.N.A.

Native, offline plate solving with [Seiza](https://github.com/theatrus/seiza),
available as both a primary solver and a blind solver in N.I.N.A.
Uses the upstream plugin provider API from
[isbeorn/nina#449](https://github.com/isbeorn/nina/pull/449).

**Requires a Windows x64 N.I.N.A. develop build containing that merge.**
The initial reference build is 3.3.0.1068 at commit
`83e4a4fc4a739a1a174a4ad3262f5cdc34b2541b`.
A version number alone does not establish compatibility: an earlier binary
with the same number can lack the new interfaces. Stable N.I.N.A. 3.2 is
not supported. This plugin does not require a fork of N.I.N.A.

## Install

1. Build the plugin below, or download the Windows artifact from this
   repository's [Actions](https://github.com/theatrus/seiza-nina/actions).
2. Close N.I.N.A. Copy the contents of the plugin ZIP into a `Seiza`
   subdirectory of your develop build's versioned plugin directory.
   For the reference develop build this is
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Seiza` (the folder follows N.I.N.A.'s
   plugin compatibility version).
   Keep `Seiza.Nina.dll` and `seiza_nina_native.dll` together.
3. Restart N.I.N.A. In **Options > Plate Solving**, choose **Seiza** for
   the primary solver, blind solver, or both. Its settings appear underneath
   the selectors, once when both roles select Seiza.
4. Install the separate [Seiza CLI](https://github.com/theatrus/seiza/releases)
   and run `seiza setup` to install solver catalogs. The CLI is only needed
   for catalog setup; the plugin bundles its native solver.

Catalogs are shared with other Seiza applications. Leave the catalog path
blank for Seiza's normal discovery, or enter a catalog directory or star
tile file explicitly. The deepest available star catalog is selected. For
blind solves the adapter opens the index beside that catalog; an explicit
index file or directory overrides discovery. A primary solve needs only
the star catalog. A blind solve also needs the prebuilt blind index;
the plugin does not build an all-sky index during a solve.

Settings belong to the active N.I.N.A. profile. The default solve time limit
is 60 seconds, primary scale tolerance is 0.25 (±25%), and blind scale bounds
are 0.1–20 arcsec/pixel. Set telescope focal length, camera pixel size and
pointing in N.I.N.A. as usual. N.I.N.A. controls retries, blind failover,
centering and synchronization.

## Build and verify

Install Git, the .NET 10 SDK, and stable Rust with the MSVC x64 toolchain
and Visual C++ build tools. Run from this repository:

```powershell
./scripts/build.ps1 -Test
```

The script clones the exact N.I.N.A. revision from `upstream.json` into
the ignored `.deps/nina` directory, builds Seiza from the locked published
crates, runs native and .NET tests, then writes a plugin ZIP and SHA-256
checksum under `artifacts/`. It packages only the plugin and native bridge;
N.I.N.A. supplies the managed host assemblies. The ZIP is a development
artifact and is not Authenticode signed or submitted to the plugin registry.

To use an existing upstream checkout:

```powershell
./scripts/build.ps1 -Test -NinaSourcePath C:\src\nina
```

The tests generate an artificial star field, catalog and blind index. They
exercise actual pixel detection and primary/blind solving, the upstream
factory, MEF composition, N.I.N.A.'s real plugin assembly load context,
embedded WPF settings, profiles, cancellation and shutdown. They do not
operate astronomy equipment or need external catalogs.

## Implementation

The MEF export implements `IPlateSolverProvider` with the stable provider ID
`fyi.seiza.nina.plate-solver`. A separate `PluginBase` manifest owns shutdown.
The plugin GUID is `66d7411d-b4b6-470b-9f34-d1578699c834`.

The C# adapter passes N.I.N.A.'s linear 16-bit mono/Bayer pixels directly
to a small Rust bridge. Seiza 0.40.1 owns detection, matching, WCS fitting,
catalog resolution and memory mapping. No temporary FITS files or solver
processes are created. Catalog mappings are reused, the blind index opens
on first use, and work is serialized across both solver roles. Changing
catalog paths replaces the cached engine on the next solve. Replacing
catalog files in place requires restarting N.I.N.A.

Primary solves use N.I.N.A.'s J2000 coordinates, search radius and effective
pixel size (including capture binning). They return an unsuccessful result
on no match so N.I.N.A. can run its configured blind failover. Blind solves
use the configured scale range without pointing hints. Detection honours
the requested downsampling (1–8) and maximum objects (32–2000, 600 by
default); Bayer frames use even-sized averaging blocks. Detection centroids
are restored to original pixel coordinates before fitting, so output scale
remains in captured-image pixels. Initial solutions are linear TAN without
SIP distortion fitting.

The native solver uses up to four worker threads. Cancellation and deadlines
are cooperative: decoding is unnecessary, averaging checks each row, and
Seiza checks between search stages. Detection and initial catalog/index
mapping cannot be interrupted mid-operation, so cancellation can take one
stage to return. Teardown cancels active and queued solves before freeing
the engine. Only star-confirmed solutions are returned; image header WCS
is never accepted as a successful solve by itself.

The result adapter uses N.I.N.A.'s `WorldCoordinateSystem` and the same
position-angle/parity convention as its ASTAP adapter. Tests cover these
conventions and binning/downsampling independently of file header metadata.

Code is Apache-2.0. See [third-party notices](THIRD-PARTY-NOTICES.md) for
Seiza and host licenses. Catalog downloads retain their own data licenses.
