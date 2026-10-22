# Seiza for N.I.N.A.

This repository is a standalone plugin. Do not modify the N.I.N.A. checkout
under `.deps/` to implement plugin behavior. `upstream.json` pins the host API;
`Cargo.lock` pins the published Seiza implementation.

- Keep astrometry, detection, catalog selection and WCS fitting in Seiza.
  `native/` is a thin host adapter, not another solver implementation.
- A solve must be confirmed by stars. Never accept an unchecked header WCS
  as a success: N.I.N.A. can use that result to synchronize a mount.
- `PlateSolveParameter.PixelSize` already includes capture binning. Native
  downsampling restores original pixel centroids before fitting.
- Primary misses return `Success = false`; N.I.N.A. owns blind failover.
  Cancellation propagates as `OperationCanceledException`.
- Manifests and providers use different MEF containers. Shared engine lifetime
  belongs to `SeizaRuntime` and manifest teardown must cancel, drain and dispose it.
- Plugin ZIPs contain only this plugin, its native DLL and notices. Never
  package N.I.N.A.'s managed dependencies or catalogs.
- Run `cargo fmt --all --check`, `cargo clippy --all-targets --locked -- -D warnings`
  and `./scripts/build.ps1 -Test` after behavior changes. Tests use artificial
  images/catalogs and N.I.N.A.'s actual factory and plugin load context.
