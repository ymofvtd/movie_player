# MyCinema third-party notices

MyCinema uses unmodified third-party packages. Each release includes the exact
package versions, authors, copyright statements, license declarations, repository
commits where supplied, and SHA-256 file hashes in `release-manifest.json`.
Original NuGet metadata and bundled license/notice files are retained under
`licenses/packages` and `licenses/runtimes` in the installed folder.

| Component | License | Upstream |
|---|---|---|
| .NET runtime, Windows Desktop runtime, Microsoft.Extensions.*, Microsoft.Data.Sqlite* | MIT, plus runtime third-party notices | https://github.com/dotnet/dotnet |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| SQLitePCLRaw.* | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite engine packaged by SQLitePCLRaw.lib.e_sqlite3 | Public domain | https://sqlite.org/copyright.html |
| LibVLCSharp and LibVLCSharp.WPF | LGPL-2.1-or-later | https://code.videolan.org/videolan/LibVLCSharp |
| VideoLAN.LibVLC.Windows | LGPL-2.1-or-later (package declaration) | https://code.videolan.org/videolan/libvlc-nuget |

The license texts are included alongside this document. .NET's original
third-party notices cover its embedded dependencies. Copyright remains with the
respective authors; the libraries are provided under their original terms.

VLC and LibVLCSharp remain separate, dynamically loaded DLLs. MyCinema does not
merge or trim them, change their code, or prevent replacing them with compatible
versions. Their original licenses apply to modifications and redistribution.

Source and build information for the bundled VideoLAN versions:

- LibVLCSharp 3.10.2: commit `3082c09ca955131c705fc4767fde03e9c4a14999` at
  https://code.videolan.org/videolan/LibVLCSharp
- VLC 3.0.24 source: https://download.videolan.org/pub/videolan/vlc/3.0.24/vlc-3.0.24.tar.xz
- Native package build scripts: https://code.videolan.org/videolan/libvlc-nuget
- VLC dependency sources/build recipes: https://code.videolan.org/videolan/vlc/-/tree/3.0.24/contrib

These are upstream source references, not a bundled corresponding-source offer.
The current release is a personal local installation. Public redistribution
still requires collecting the exact corresponding native sources/build inputs
and checking notices for the bundled VLC plugins and their dependencies.

License text provenance:

- `LGPL-2.1.txt`: the LibVLCSharp commit above, file `LICENSE`.
- `Apache-2.0.txt`: https://www.apache.org/licenses/LICENSE-2.0.txt
- `CommunityToolkit-MIT.md`: the restored CommunityToolkit.Mvvm 8.4.2 package.
- Runtime licenses and notices: copied from the exact restored runtime packs
  recorded in the published application's runtime configuration.
