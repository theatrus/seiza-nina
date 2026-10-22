# Third-party notices

This repository's plugin and native adapter are Copyright 2026 Yann Ramin,
licensed under Apache-2.0; see LICENSE.

The native DLL statically incorporates the published `seiza` crate (0.40.1)
and its Cargo.lock dependencies. Seiza code is Apache-2.0. Its embedded
constellation figures are CC BY 4.0: Constellation Lines by Marc van der Sluys
(2005–2023), hemel.waarnemen.com, DOI: 10.5281/zenodo.10397192.
The adapter does not use those
figures in plate solving. Seiza source and its full notices are available at
https://github.com/theatrus/seiza.

Other native dependencies include Rayon (Apache-2.0 OR MIT), serde and
serde_json (Apache-2.0 OR MIT), image (Apache-2.0 OR MIT), nalgebra
(Apache-2.0), memmap2 (Apache-2.0 OR MIT), and the transitive dependencies
identified by Cargo.lock. The build includes their license and notice texts
in `THIRD-PARTY-LICENSES.txt` inside the plugin ZIP, collected from the locked
crate archives. `cargo metadata --locked --format-version 1` lists the
complete resolved set, source locations and declared licenses.

N.I.N.A. host assemblies are MPL-2.0, Copyright Stefan Berg and the
N.I.N.A. contributors. They are used as compile references and supplied by
the installed host, rather than redistributed in the plugin ZIP.
https://github.com/isbeorn/nina

Catalogs and blind indexes are separate downloads and are not distributed
in this plugin package. Their source attributions and data licenses remain
applicable. The artificial test fixture is generated locally and is CC0.
