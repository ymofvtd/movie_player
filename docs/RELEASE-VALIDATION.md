# MyCinema release validation

Validation date: 2026-11-09. Target: Windows x64, self-contained .NET 10 WPF.

Every release is built and tested by `scripts/Publish-Release.ps1`, then installed
to `%LOCALAPPDATA%\Programs\MyCinema\MyCinema.App.exe`. Desktop and Start menu
shortcuts always target that executable. The user's data remains separately
under `%LOCALAPPDATA%\MyCinema`.

## Automated evidence

| Area | Verification |
|---|---|
| Large library | 10,000 real fixture paths: 1,000 movies and 9,000 episodes in 100 shows/300 seasons. Scan, SQLite commit, catalog indexing, search, paging, service restart, and unchanged rescan preserve all file/item IDs and saved progress. |
| Scan accuracy | Roadmap examples, mixed folders, all supported extensions, numeric seasons/episodes/ranges, editions, parts, extras, manual overrides, overlapping roots, cancellation, rollback, and renamed/missing/returning media. |
| Disconnected storage | Missing roots and simulated unreadable subtrees retain saved data and last successful scan timestamps. Tests do not physically disconnect a device. |
| Database persistence | Actual SQLite files reopened after container disposal; migrations, integrity constraints, independent per-file history, backups, invalid/newer databases, and transaction rollback. |
| Native playback | Packaged VLC decodes generated MKV, MP4, MOV, AVI, and WMV video; actual seek position, pause/replay, speeds, aspect ratios, track selection, subtitles, missing/corrupt media, and recovery. |
| Resume and episodes | Actual native player plus SQLite reopening resumes at approximately eight seconds. Cross-season episode switching retains one player, independent history, and each episode's resume position. |
| Resource lifetime | 30 decoded video opens across three engine lifetimes, with one native player per lifetime. Every fixture can be opened exclusively for writing after each disposal. This checks player reuse and file-handle release, not a proof of zero long-term native heap growth. |
| Interface | WPF layout/interaction checks, dark controls, keyboard routing, loading/error/empty states, artwork cache limits/invalidation, and renders at 100/125/150/200% DPI. |
| Repeated launch | Primary/secondary launch notification, listener recovery after a disconnected client, and primary ownership after shutdown. |

The Release test report is written to
`.artifacts\official-release\test-results\release-tests.trx`.
Generated scan/playback fixtures stay in temporary test directories; tests do not
write fixture media into the user's library.

## Desktop evidence on this computer

The installed app loads the saved catalog and artwork. A local 4K H.265 MKV
renders through the native Direct3D11 output. Two fullscreen/Escape cycles keep
playback advancing, restore the original window bounds, and retain the picture.
Fullscreen controls hide after inactivity. These checks supplement earlier
desktop checks recorded in `plan.md`; they do not certify every GPU, monitor,
codec, audio device, or physical DPI transition.

## Package evidence

The release contains the .NET and Windows Desktop runtimes, SQLite native DLL,
LibVLC core, and its plugin tree. The release script verifies key decoder,
demuxer, and Direct3D11 output plugins before installing. `release-manifest.json`
records exact dependencies and SHA-256 hashes for all packaged files; original
NuGet metadata, available package notices, and runtime licenses/notices are
copied from the packages used for that build. See `THIRD-PARTY-NOTICES.md` for
license text provenance and upstream source references.

## Remaining release gates

- Run on a clean Windows 10/11 x64 machine without Visual Studio, .NET, or VLC
  installed. Copy the complete published folder; open representative local
  media, seek, switch tracks, enter/exit fullscreen, close, restart, and resume.
  This development computer cannot establish clean-machine acceptance.
- Before public redistribution, collect the exact corresponding native source
  and build inputs and complete the VLC plugin/dependency notice review. The
  package metadata inventory and upstream links do not establish that gate.
- Complete extended desktop acceptance for manual organization, cross-season
  sidebar interaction, audio-device behavior, physical monitor DPI changes, and
  long playback sessions. Native and WPF automation cover the underlying flows.

The installed build is suitable for continued personal testing. Phase 10 is
kept open until its remaining release gates are verified.
