# Seiza for N.I.N.A.

Offline primary and blind plate solving with [Seiza](https://github.com/theatrus/seiza).
Uses all available CPU cores and shares Seiza's default catalog location.

Requires Windows x64 and a N.I.N.A. develop build with the
[plugin solver API](https://github.com/isbeorn/nina/pull/449). N.I.N.A. 3.2 is unsupported.

## Install

1. Download the Windows plugin ZIP from [Actions](https://github.com/theatrus/seiza-nina/actions), or build it below.
2. Close N.I.N.A. and extract the ZIP into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Seiza`.
3. Restart N.I.N.A. In **Options > Plate Solving**, select **Seiza** as the primary solver, blind solver, or both.
4. Open the **Seiza Plate Solver** plugin page, choose a catalog package, and click **Install / update catalogs**.

Set camera pixel size and telescope focal length in N.I.N.A. as usual.
Catalogs are discovered automatically; solve settings follow the active profile.

| Package | Contents | Installed size |
| --- | --- | --- |
| Standard | Gaia G17 + blind index; recommended | 3.2 GB |
| Compact | Tycho-2; wide-field primary solving only | 26 MB |
| Deep | Gaia G20 + blind index; faint, narrow fields | 10.3 GB |

![Seiza v0.3.1 plugin settings in N.I.N.A.](docs/images/catalog-options.png)

![Plate-solve result in N.I.N.A.](docs/images/nina-solve-result.png)

## Build and test

Requires Git, .NET 10 SDK, stable Rust with the MSVC x64 toolchain, and Visual C++ build tools.

```powershell
./scripts/build.ps1 -Test
```

Builds against the N.I.N.A. revision pinned in [upstream.json](upstream.json),
runs Rust and .NET tests, and writes the plugin ZIP to `artifacts/`.
Use `-NinaSourcePath C:\src\nina` to build against an existing checkout.

To run the full N.I.N.A. app tests:

```powershell
./scripts/test-nina.ps1 -InstallCatalogs
```

Requires an interactive Windows desktop, PowerShell 7.4+, and N.I.N.A.'s vendor SDK assets
in the source checkout. Tests use isolated profiles and synthetic images;
`-InstallCatalogs` also checks a real Compact catalog install. Reports and screenshots
are saved under `artifacts/nina-e2e/`. Add `-KeepOpen` to inspect the app.

## License

[Apache-2.0](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) for dependencies and catalog licenses.
