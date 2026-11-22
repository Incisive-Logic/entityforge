# Dependency licence texts

The build copies package-supplied licence and notice files, along with each package's NuGet metadata (including its original author/copyright fields), into package-specific directories. `packages.json` identifies the restore closure; it may include build-only dependencies. Runtime pack notices are under `runtime-packs/`.

The common MIT, Apache-2.0 and BSD-3-Clause texts are from [SPDX licence-list-data v3.27.0](https://github.com/spdx/license-list-data/tree/v3.27.0/text). Read them with the copyright notices in the corresponding package metadata and bundled files. Project-owned code's Apache licence does not replace these licences.

`Avalonia-MIT.txt` is the [Avalonia licence at the package source commit](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/licence.md). `Inter-OFL.txt` is the [Inter font licence](https://github.com/rsms/inter/blob/master/LICENSE.txt); the font retains its SIL Open Font License even though its Avalonia wrapper package declares MIT.

Review this inventory whenever dependency versions change, particularly embedded native and font assets. Preserve the package-specific notices as well as these common texts.
