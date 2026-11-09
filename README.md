# MyCinema

A native, offline-first Windows application for a personal movie and TV library, built with C#, .NET 10, and WPF.

## Current milestone

**Phase 10 — Testing and Release is in progress.** The self-contained Windows x64 release installs to one permanent location. Release coverage includes a 10,000-video library, native decoding across common containers, resume across database/player restarts, and repeated video switching and engine disposal. License notices, exact dependency metadata, and a SHA-256 file manifest ship with the app.

Browse movies, shows, collections, and recent additions; search and filter the catalog; choose a season, episode, or movie edition; and play the selected file. Background scanning, manual organization, and the Settings hierarchy preview remain available. Settings includes a saved appearance preference; editing the remaining playback preferences is still pending.

The remaining release gates are clean-Windows playback and the complete native dependency/source review for public redistribution. See [release validation](docs/RELEASE-VALIDATION.md) for evidence and limits, and [plan.md](plan.md) for the roadmap.

## Browsing and playing your library

- **Home** shows recent additions, movies, TV shows, franchises, and any uncategorized videos. Each row previews up to eight titles; **View all →** opens its full section, including the Uncategorized filter.
- **Movies** includes standalone films and films inside collections. **Franchises** opens a collection's movies in the saved folder/ordinal order. Collection folders themselves are never playable.
- **TV Shows** opens show details with a season selector and an episode list ordered by numeric season/episode numbers. Multi-episode files show their episode range. Select an episode to open its file details.
- Open a movie or episode, choose its file/edition, and press **Play**. File paths, format, cached runtime, and size help distinguish editions and parts. Runtime shows as unknown when it has not been cached. **Back** returns through details to the original listing, retaining search/filter values and season/file selections during this session.
- Search matches titles and their parent show/collection names, ignoring case. Home and TV Shows search also include matching episodes. Home's **Episodes** type filter provides a complete episode listing; **Uncategorized** exposes uncertain detections.
- Combine availability, watch state, release year, and applicable media-type filters. Sort by title in either direction, newest release year, recently added, recently watched, or unwatched first. **Reset** restores the page defaults. Unknown years remain visible under **All years**.
- Watch labels and watch-based filtering/sorting update from saved progress. One completed edition marks its movie watched; shows and collections require all their playable titles to be completed. Each file/edition retains its own saved position.
- Unavailable records remain visible for inspection and can be filtered separately; their Play action is disabled. If a file disappears after the last scan, the player reports that it cannot be opened. Reconnect the drive or rescan in Settings.

Search is debounced and runs on a worker against an indexed catalog. SQLite loads items, files, and progress in one consistent read transaction. Listings initially create 60 cards, with **Show more** loading another 60; Home rows are bounded previews. Cards display assigned posters with offline fallback artwork. Scans automatically reload the browser; **Refresh** reloads the saved database without rescanning media. The saved catalog loads again on startup.

Loading placeholders appear before results arrive. Empty views distinguish a new library, a search with no matches, and an empty Continue Watching list, with actions to scan, clear filters, or browse movies. Refresh errors offer **Try again** and retain any already loaded catalog. Unavailable cards stay browsable and show an availability badge.

Use **Ctrl+F** to focus and select the current library search, **Escape** in search to clear it, **Alt+Left** to go back, and **F6** to move between navigation and page content. **Tab / Shift+Tab**, arrow keys, and **Enter / Space** navigate and activate controls. Focus outlines remain visible in the dark theme. Focused player sliders, buttons, and episode lists keep their normal Space/arrow behavior; letter shortcuts such as **F** still work from buttons.

Under **Settings → Appearance**, toggle **Enable page animations** to preview a short page fade, then **Save appearance** to keep the choice after restart. Windows' client-area animation preference also controls whether fades play. Animations affect library/details pages; the native video surface remains in place.

## Posters and manual organization

Put **poster.png** in a movie folder, a show's top-level folder, or a franchise's top-level folder, then use **Settings → Scan library**. The scanner picks it up automatically. It also recognizes **poster.jpg** and **poster.jpeg**, in that priority order after PNG; names are case-insensitive on Windows. For example, `Interstellar (2014)\poster.png` appears on the movie's card and details, and `Loki\poster.png` applies to the show. Each movie inside a franchise can have its own poster. Folder images stay in place; replacing or removing them is reflected on the next scan. Live filesystem monitoring remains a later feature.

For a separate image, open the title and select **Choose poster…**. Pick a local JPG, PNG, BMP, GIF, or TIFF image (up to 32 MB). MyCinema stores a decoded PNG copy under `%LOCALAPPDATA%\MyCinema\Cache\Posters`, so moving or deleting the chosen source does not remove it. A chosen poster takes priority over folder artwork. **Use folder poster** clears that choice and returns to automatic folder artwork. **Remove poster** explicitly hides artwork until you choose another poster or use folder artwork again. Changes save immediately and survive restarts and rescans.

Artwork appears on portrait library cards and details; episode cards can inherit their show's poster. Missing or unreadable images fall back safely. The app makes no artwork network requests.

Decoded thumbnails are shared across Home rows, cards, and details in a memory cache capped at 32 MiB and 128 entries. Thumbnails fit within 480×960 pixels, release their source-file handles, and reload when file size or modification time changes. Invalid files are cached as fallbacks until their stamp changes. Disk poster copies are retained independently of this memory cache.

Expand **Edit details & organization** on the details page to change a title, release year, description, or classification. Episode details also let you choose its show, season, episode number, and optional range endpoint. **Save details** commits metadata; **Cancel** discards the draft. A movie's franchise selector reassigns it or returns it to the standalone library.

- **Create a franchise** from movie details by entering a collection title. That movie is added automatically. On franchise details, add or remove movies, change their order, rename the collection, or remove its grouping while keeping all movies and files.
- Changing a Movie/Show/Franchise classification reshapes its catalog hierarchy. A show conversion puts playable titles in Season 01 for subsequent episode-number editing. A franchise conversion makes them movies. A movie conversion combines the hierarchy's files as editions or parts of one title. These operations retain physical file IDs and independent progress.
- **Combine duplicate titles** moves one playable title's files into another title with the same classification. The destination metadata is kept, with source artwork used if the destination has none. **Separate an incorrectly grouped file** creates a new title for one file when the original has multiple files. Each file keeps its saved progress.

Organization writes are transactional and never rename, move, edit, or delete media. Scanner aliases preserve merged identities and remember removed franchise groupings. New children of corrected containers follow the corrected classification. Manual metadata edits protect the item from classification/title changes during scanning; folder artwork continues updating unless a poster choice or explicit removal overrides it.

## Scanning your library

Open **Settings** and choose **Scan library**. On first setup, MyCinema registers `D:\Entertaiment` as its default library root. Startup loads the saved library without automatically rescanning it. Choose **Scan library** again after adding or removing media.

The scanner reads directory entries, video extensions, sizes, and modification times. It never renames, moves, edits, or deletes media. **Cancel scan** cancels discovery or rolls back the unfinished root's database transaction; previously completed roots remain saved. Closing the app also cancels and waits for an active scan.

Detection supports:

- Movie folders, release years, common technical-tag cleanup, and edition labels such as Extended Edition, Director's Cut, Theatrical Cut, and Remastered.
- Season folders named `Season 01`, `Season 001`, `S01`, or `Series 1`; episode names such as `S01E02`, `1x02`, and `E02` inside a known season.
- Multi-episode files such as `S01E01-E02`, saved as one file and one episode item with an explicit episode range.
- Franchise candidates containing multiple movie folders. Shows within mixed collections remain independent shows. Numeric folder prefixes are retained for sorting while being removed from display titles.
- Sample, trailer, and extras exclusions; matching alternate cuts and numbered parts share a movie item. Ambiguous videos go to Uncategorized with a warning.

Stable canonical paths retain file/item identities and saved progress on rescans. Unchanged item metadata is reused; known duration is retained for unchanged file size/timestamps. Changed files invalidate cached duration without erasing progress. Files confirmed missing after a successful enumeration are removed from library browsing, along with shows, seasons, and collections left empty by those files. Their database records and saved history remain available if the same paths return. An inaccessible root retains all its saved records; inaccessible subtrees retain their previous availability and do not advance the last-successful-scan time. Linked folders/files are skipped to avoid cycles and scanning outside the selected tree. Overlapping enabled roots are scanned through their available parent once.

Existing manual item edits and metadata-override records take precedence over automatic detection. Additional-root editing belongs to a later phase. Identity follows paths; a moved or renamed file is treated as a new path, with its old record retained outside library browsing. Saved progress is not automatically transferred between different paths.

## Requirements

- Windows 10 or Windows 11, x64
- .NET 10 SDK for development
- Visual Studio with the .NET desktop development workload, if using the IDE
- Internet access for the initial NuGet restore; the application itself requires no network connection

## Build and run

From the repository directory:

```powershell
dotnet build "Person Movie Player.slnx"
dotnet test "Person Movie Player.slnx" --no-build
dotnet run --project "Person Movie Player.csproj"
```

## Install or update the everyday app

Use this command from the repository to build, test, and install the latest Release version:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1
```

The permanent executable is **`%LOCALAPPDATA%\Programs\MyCinema\MyCinema.App.exe`**, which on this computer is `C:\Users\myasg\AppData\Local\Programs\MyCinema\MyCinema.App.exe`. Use the **MyCinema** Desktop or Start menu shortcut for everyday use. Every release updates that same location; development outputs under `bin` and `.artifacts` are not the installed app.

The installed app includes the .NET desktop runtime, SQLite, and VLC. It runs without Visual Studio or a separate .NET installation. Keep the executable with the other files in its installation folder. Your library, settings, artwork, and history remain under `%LOCALAPPDATA%\MyCinema`, separately from the executable. Close the installed app before running the update script; it refuses to replace a running installation.

MyCinema runs one instance per Windows user/session. Opening its shortcut or executable again restores the existing window, including when minimized, instead of opening another player or touching playback. Launches received during startup are applied when the window is ready.

The publish settings are in `Properties\PublishProfiles\WindowsRelease.pubxml`. The script tests and publishes before replacing the installed folder, retaining the previous installation if the replacement fails.

The installed folder also contains this README, `docs\RELEASE-VALIDATION.md`, `THIRD-PARTY-NOTICES.md`, license texts, original dependency metadata, runtime notices, and `release-manifest.json`. The manifest records every packaged file's size and SHA-256 hash, plus exact package/runtime versions. Publishing rejects missing key VLC codec/demux/output plugins, missing notice files, or a dependency with an unreviewed license declaration. The full test report is saved to `.artifacts\official-release\test-results\release-tests.trx`.

Alternatively, open `Person Movie Player.slnx` in Visual Studio, select the root WPF project as the startup project, and press F5. Both AnyCPU and x64 solution configurations compile the projects for x64, as configured in `Directory.Build.props`.

## Watching a local video

Choose **Open video** from Home or the Player page, or press **Ctrl+O**. Select a local file in the Windows file picker. The player accepts MKV, MP4, AVI, MOV, WMV, WebM, M4V, MPG, MPEG, TS, and M2TS; decoding depends on the VLC runtime and the file's codecs.

Drag the seek bar to jump to a time. **Stop** resets playback to the beginning; **Play** starts it again. Fullscreen fills the current monitor and restores the previous window bounds and state when exited. Controls appear over the video without changing its size or aspect ratio, and stay visible in windowed mode. In fullscreen, the header, episode panel, and controls hide after three seconds of inactivity; moving the pointer, clicking the picture, or pressing a key reveals them. Pause, seeking, errors, open menus, and pointer/keyboard interaction with controls keep them visible. **Lock controls** (or **L**) keeps the controls hidden and ignores accidental playback keys and double-clicks. Use **Unlock (L)** or press **L** to unlock; **Escape** exits fullscreen. Pausing, ending playback, or an error also releases the lock. The video host and native player are retained throughout these changes.

Expand **Audio, subtitles & picture** below the video to access the playback options. It starts collapsed to leave room for the picture and episode list at small window sizes. Use **Audio language** to choose any available language or dub. Track names and language tags come from the file; choosing English requires an English track to be included. Tracks with missing language tags retain VLC's track descriptions. Selections become available once playback is ready and can be changed while playing or paused.

Use **Subtitles** to select an embedded track or **Off**. **Load subtitles…** adds and selects a local SRT, ASS, SSA, VTT, SUB, or IDX file. External subtitle support depends on VLC and the subtitle format. Audio and subtitle choices apply to the current playback session; the original media is unchanged.

**Speed** offers 0.5×, 0.75×, 1×, 1.25×, 1.5×, 1.75×, and 2×. **Aspect ratio** defaults to **Original**, fitting the picture with its source proportions. Select 16:9, 4:3, or 2.35:1 to correct a file's picture shape; choose Original to reset. Speed and aspect-ratio choices survive Stop/replay and file switches within this session. They do not reset progress, reopen media, or change the source file. They return to defaults after application restart.

### Progress and resume

For files already in the scanned library, progress saves every ten seconds during playback and on pause, Stop, file switching, navigation away, natural end, and application close. Stop preserves the position before resetting the player; pressing Play after Stop starts at the beginning. Files opened outside the scanned library play normally and do not create library/history records.

Opening an unfinished file after at least 5% watched offers **Resume**, **Start Over**, or **Cancel**. Resume starts from the saved timestamp when VLC is ready. Start Over resets that file's progress. Cancel keeps the current page and leaves any prior playback paused. Existing persisted `ResumePlayback` and `ShowResumePrompt` settings are respected; preference editing is still planned.

**Continue Watching** on Home and in the sidebar lists available unfinished movies and episodes, newest played first, with timestamps and progress bars. Multiple editions are grouped by title; opening a Continue Watching card selects the most recently played resumable edition. Completed or unavailable files are excluded. A file below 5% watched still saves its position without appearing in Continue Watching or prompting to resume.

Natural end marks a file watched. Seeking into the credits alone does not. File details provide **Mark watched**, **Mark unwatched**, **Reset progress**, and **Start Over**. Mark unwatched clears completion and retains an unfinished position, or resets a completed file to zero. Reset progress removes that file's saved position and watch state. Completion stays watched during replay until explicitly changed or reset. Changes update library badges, filters, and Continue Watching. Failed history writes show a warning while playback remains usable.

### Watching a series

Playing a scanned episode opens its show's panel on the right, including when the file is opened through **Open video**. Choose a season and click an episode to play it. Seasons and episodes use numeric order; multi-episode files appear once with their range. The current episode is highlighted and brought into view when switching episodes, changing to its season, or expanding the panel. Rows show watched state, saved progress, and live progress for the current episode. Unavailable rows remain visible with playback disabled. Movies and unscanned files hide the panel.

The previous/next buttons in the player header, or **P** / **N**, navigate through available episodes across seasons and skip missing files and empty seasons. Navigation is disabled at the first/last available episode. Natural end marks the current episode watched; automatic next-episode playback remains planned. Browsing another season leaves playback running until an episode is clicked.

Episode switches use the existing Resume / Start Over / Cancel behavior and save the previous episode's position first. Cancel retains the current episode, paused. Alternate files prefer the current edition or the most recently played available edition. **Hide episodes** / **Show episodes** works in windowed and fullscreen playback. Its state is saved separately in `UserSettings` and restored on startup when the stored `RememberSidebarState` preference is enabled. Playback controls overlay the bottom of the video and span the full player width; the episode panel overlays the right side without shrinking the picture.

| Shortcut | Action |
|---|---|
| Ctrl+O | Open a local video from any page |
| Space | Play / pause on the Player page |
| Left / Right | Rewind / forward ten seconds |
| Up / Down | Raise / lower volume by five points |
| M | Mute / unmute |
| P / N | Previous / next available episode, across seasons |
| S | Reveal playback options and open the subtitle selector |
| Tab from the video | Reveal and focus playback controls |
| F | Toggle fullscreen |
| L | Lock / unlock controls during fullscreen playback |
| Escape | Exit fullscreen |
| Double-click video | Toggle fullscreen |

Playback shortcuts avoid editable text fields, selectors, and modal dialogs. Escape closes an open selector first; otherwise it exits fullscreen, including when a selector has focus.

Browsing away pauses playback. Returning to Player preserves the picture and position within the current application session. Scanned library progress survives application restarts; volume and track choices are not yet saved across restarts.

Unavailable and malformed files produce a readable player error. Expand **Details** for the attempted file path or available VLC diagnostics. **Dismiss** clears the message without stopping playback. A failed session offers **Retry** in the transport controls; another local file can also be opened. Selecting an unavailable file while valid media is playing preserves the existing session. Audio/subtitle, speed, and picture-option failures also preserve current playback.

## Structure

```text
Person Movie Player.csproj                 WPF app; output assembly MyCinema.App
App.xaml / App.xaml.cs                     Resources, startup, shutdown, DI ownership
MainWindow.xaml / MainWindow.xaml.cs       Navigation shell
Services/                                 App service registration and navigation
ViewModels/                               Observable page and shell state
Views/                                    Library browser, details, Home, Player, Settings scan preview
Themes/                                   Shared colors and control styles
src/MyCinema.Core/                        Models and framework-independent contracts
src/MyCinema.Infrastructure/              SQLite, repositories, scanning, settings, VLC, paths, logging
tests/MyCinema.Tests/                     xUnit, SQLite, and native playback integration tests
```

The original app project remains at the repository root to preserve the existing Visual Studio entry point. The app references Core and Infrastructure; Infrastructure references Core. Core has no WPF, SQLite, or VLC dependency. Infrastructure owns the VLC engine and media player behind `IPlaybackService`; only the WPF view adapter consumes the native video output handle. The WPF and native VLC packages remain in the app.

One shared playback service serializes open, play, and stop operations off the UI thread. Native callbacks publish immutable state, and the player ViewModel dispatches updates onto WPF's UI thread. The application waits for playback operations to finish and stops VLC before disposing the window and service container.

The video host stays alive during page navigation and fullscreen transitions. Mouse input, playback controls, and the episode panel use the supported `VideoView` content overlay, following [LibVLCSharp's WPF guidance](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp.WPF/README.md). The native viewport has its own fixed layout, so showing, hiding, locking, or expanding the overlay never changes the video dimensions.

## Local data

Application data is reserved under `%LOCALAPPDATA%\MyCinema`:

- `Logs\mycinema-YYYY-MM-DD.log`: UTC diagnostic logs for startup, navigation, engine initialization, media opening, errors, and shutdown.
- `Data\library.db`: SQLite database, created and migrated during startup.
- `Cache\Posters`: imported local poster copies. Include this directory when backing up custom artwork.

The default media folder is `D:\Entertaiment`, registered when no library roots exist. Diagnostic log failures fall back to debug output rather than crashing the application. Logs remain local and are not uploaded.

### Database behavior

Schema version 4 contains `LibraryRoots`, `LibraryItems`, `MediaFiles`, `PlaybackProgress`, `UserSettings`, `MetadataOverrides`, scanner source identities, and migration history. Migration 2 adds episode-range endpoints and media-root ownership while retaining version 1 records. Migration 3 allows multiple scanner identities to refer to a merged title and null identities to remember removed groupings, retaining all existing source/file/progress records. Migration 4 distinguishes explicit poster choices from automatically discovered folder images and preserves previously selected manual artwork. Foreign keys, hierarchy checks, indexes, and unique canonical Windows paths protect relationships and duplicate detection. Path matching normalizes case, separators, dot segments, and extended-length path prefixes; it does not resolve symbolic links or other filesystem aliases.

Repository writes use transactions and parameterized SQL. File updates retain their IDs and saved progress, including when marked unavailable. One current progress record is stored per media file; older queued updates cannot replace newer progress. Metadata overrides are stored independently of ordinary metadata updates. Grouped library-item changes either commit together or roll back together.

Database operations run on worker threads because [Microsoft.Data.Sqlite's async ADO methods execute synchronously](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async). Connections enable foreign keys, use a five-second lock timeout, and close after each operation. No live connection is shared across threads.

`ISettingsService` validates and persists typed JSON preferences in `UserSettings`. Missing or malformed preferences restore safe defaults without resetting the library. Library roots have their own repository. Resume and sidebar-state preferences are applied; other playback preferences are not yet applied or editable through Settings.

Startup checks database integrity and refuses a schema from a newer application version. Database failures are logged and reported with the database path; the application does not erase or recreate an existing damaged database.

### Backup and recovery

`IDatabaseService.BackupAsync` creates a consistent backup through [SQLite's online backup API](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup). Supply an absolute destination filename in an existing directory. It refuses the active database path and never replaces an existing backup. A backup button and automatic backup scheduling are not yet part of the interface.

To recover from a damaged database, close MyCinema and preserve a copy of the entire `Data` directory first. Restore a known good database backup to `%LOCALAPPDATA%\MyCinema\Data\library.db`, retaining the damaged copy separately. Relaunch MyCinema to validate and migrate the restored database, then rescan in Settings to discover current media. A fresh database can rebuild the media catalog through scanning, but cannot reconstruct watch progress or manual metadata from the media files alone.

## Verification

Verified on Windows on 2026-11-09 (earlier milestone checks were on 2026-11-08):

- Phase 9: the isolated Release solution builds with zero warnings/errors and all 147 tests pass. New presentation tests cover first-load/failure/retry state transitions, retaining cached cards on refresh failure, contextual recovery commands, rapid query replacement, Home links, and saving/reopening the animation preference without resetting playback settings. WPF checks cover shared/frozen poster reuse, released source handles, replacement/removal/corrupt-image recovery, LRU eviction and byte limits, oversized dimensions, and reduced-motion behavior. Actual slider-thumb events update the seek binding; routed key events preserve focused-button Space and the F shortcut.
- Actual WPF library layouts render at widths 604 and 956 at 100%, 125%, 150%, and 200% DPI; details, Settings, and player render at all four scales. Controls, popup template parts, card bounds, accessibility descriptions, and mutually exclusive loading/empty/error states are asserted. PNGs are under `.artifacts/phase9-final/bin/MyCinema.Tests/release/Artifacts`. This follows Microsoft's [WPF DPI rendering guidance](https://github.com/microsoft/WPF-Samples/blob/main/PerMonitorDPI/readme.md). Physical monitor transitions and native video scaling still require desktop acceptance. The desktop tool launched the intermediate Phase 9 build but assigned both application instances the same app identity and refused to bind the new window; live input checks stopped after recovery also failed. The original application was left open. The final executable is in the isolated `phase9-final` output above.
- Phase 8: Debug and isolated Release builds succeed with zero warnings/errors. All 144 tests pass in Release. Nineteen new organization cases cover metadata/artwork persistence, all six Movie/Show/Franchise conversions, stable per-file progress, repeated rescans, newly added children, naming changes without duplicate seasons/items, franchise ordering/reassignment/removal, duplicate merging, file separation, episode reparenting/ranges, invalid-write rollback, cancellation, editor validation, and v2 migration. Six additional folder-artwork cases cover movie/show/franchise roots, restart persistence, episode inheritance, PNG/JPG/JPEG precedence, addition/replacement/removal through rescans, metadata/progress preservation, explicit poster/removal priority, returning to folder artwork, converted playable children, and v3 migration preserving existing manual artwork.
- WPF checks use the actual poster picker command with an injected file choice, import an image into the cache, delete its source, reopen the database, and verify image bindings on movie/show/franchise cards and details. They render expanded editors at 604 and 956 pixels wide, check poster removal, corrupt/missing/remote-image fallback, and retaining the original target if navigation changes during import. Images are written under the test output's `Artifacts` directory. The running application was left uninterrupted; live file-picker and organization acceptance remain pending.
- Phase 7: Release builds with zero warnings/errors; all 119 tests pass. New cases exercise all seven speed values and all four picture ratios against native VLC, invalid option rejection, reset to Original, pause/seek continuity, Stop/replay, file switching on the same player, and recoverable error details/dismissal. Five generated MKV, MP4, MOV, AVI, and WMV fixtures must decode video frames, seek, pause/resume, and accept speed changes. These cover H.264/AAC, MPEG-4 Visual/MP3, and WMV2/WMA2; they do not establish support for every codec combination.
- Fullscreen inactivity tests use a controlled clock to verify hiding, input wake-up, pause, seeking, menus/focus, errors, windowed visibility, and locking. Actual WPF bindings verify that the full video area stays fixed as controls hide, reappear, lock, and expand. A native WPF/VLC check exercises the detached overlay, keyboard unlock, unchanged viewport/handle/aspect ratio, and clean unload/reload. Templates render at small and larger sizes, with a minimum episode-list height and all selectors checked. Interactive desktop acceptance remains separate from these automated checks.
- Phase 6: Release builds without warnings/errors and all 110 tests pass. Eleven sidebar cases cover numeric seasons/episode ranges, watched/live progress, independent per-episode resume, Cancel/Start Over, missing-file skipping, season transitions, concurrent-open prevention, refreshed availability, manual resets, direct file opening with canonical paths, movie/unscanned context clearing, and saved collapse state after reopening SQLite. An additional native VLC case switches between two seasons and returns to the previous episode at its saved eight-second position using the same native player.
- The WPF player and sidebar render at 604×400, 956×540, and 1180×540; the active row, season selector, navigation states, and full-width playback controls are checked. Images are written under the test output's `Artifacts` directory. Computer Use again rejected the live application launch with “Computer Use was not approved to use app,” so interactive sidebar, keyboard, auto-scroll, and fullscreen acceptance remain pending.
- Phase 5 plus audio/subtitle selection: Release builds without warnings/errors and all 98 tests pass. Added cases cover periodic saves, pause/Stop positions, file switching, stale callbacks, completion/reset behavior, resume choices/preferences/thresholds, Continue Watching episodes and edition selection, and persistence after reopening SQLite. A native integration case opens a scanned fixture, pauses/seeks, saves and disposes the player/container, reopens the same database with a new player, resumes at the saved timestamp, and verifies Stop/replay starts at zero.
- A generated 20-second H.264/AAC MKV exercises actual Russian-to-English audio switching, subtitle enable/Off, external SRT loading, invalid track IDs, unavailable subtitle files, and clearing stale tracks on file changes. Tests use dummy outputs with hardware decoding explicitly disabled on MediaPlayer.
- The actual WPF player template is laid out and rendered at its minimum available width (676 pixels beside the sidebar); selectors display readable current choices and fit with Load subtitles. A live desktop launch was rejected by Computer Use with “Computer Use was not approved to use app,” so interactive desktop verification for these changes remains pending. Earlier desktop checks below describe prior phases.
- Release solution build succeeds.
- Phase 4 originally passed 83 tests: the 68 foundation, player, database, and scanner cases, plus 15 browser, details-navigation, and WPF template-loading cases.
- Browser tests cover collection movies, unavailable/uncategorized records, empty states, rapid search cancellation, episode search and parent context, combined filters, all six sort options, numeric season/episode order, multi-episode ranges, edition selection, shared-player navigation, Back/selection restoration, saved snapshot reopening, watch-state aggregation, batched cards, refresh after availability changes, and automatic refresh after scanning.
- Phase 4 desktop checks browse the real saved catalog (50 movies, two shows, two franchises), search Movies, open Ant-Man and play its 4K MKV, switch Loki to Season 02 and play Ouroboros, return through Back with the season preserved, and inspect Harry Potter's eight movies in ordinal order. The file/season selectors display readable labels. Closing during playback and after browsing exits with code 0. The browser requires no database migration and does not modify media files or watch history.
- SQLite checks cover every record type and typed settings across service-container disposal/reopening; record updates; numeric episode ordering; foreign keys and hierarchy validation; canonical path uniqueness; progress preservation and stale saves; grouped rollback; parameterized text; settings recovery; concurrent independent connections; cancellation; migration idempotence and rollback; newer-schema refusal; corrupt/unwritable database handling; and backup restoration without overwriting existing backups.
- Scanner checks cover the roadmap's Lord of the Rings/Game of Thrones examples, mixed collections, numeric episode order, multi-episode ranges, all 11 extensions, editions, parts, extras, uncertain media, unchanged rescans, changed/missing/returning files, unavailable roots, simulated unreadable subtrees, preserved manual edits/progress, cancellation, rejected concurrent scans, overlapping/disabled roots, transaction rollback, v1-to-v2 upgrades, and saved previews.
- Phase 3 desktop checks verify automatic upgrade of the existing version 1 database, Settings controls, a real scan of `D:\Entertaiment`, collection expansion, saved counts on restart, and clean shutdown (exit code 0). The scan found 73 files: 50 movies and 23 episodes across two shows, with two franchise candidates and three seasons; no warnings or uncategorized files. Harry Potter and Marvel are franchise candidates; BAYM and Loki are shows. The real database passes `integrity_check` with two migration records. No fixture media was inserted into the user's media folders.
- Native tests use generated local WAV media with dummy audio/video outputs to check engine loading, pause, actual native seek position, stop, replay, natural end, file switching, resource disposal, missing/corrupt files, and recovery. They require the packaged native VLC binaries and make no network requests.
- Desktop playback verified with local 4K H.265 MKV media: play/pause, seek-bar dragging, ten-second skips, volume/mute, stop/replay, fullscreen, Escape restoration, double-click toggling, visible controls, and navigation away/back without losing the picture or session position.
- Phase 1 verification confirmed closing the window during active playback succeeds with exit code 0.
- Startup diagnostics confirm the running application is 64-bit.

The native integration tests exercise the engine without measuring audible output or GPU rendering. Desktop smoke checks cover the rendered picture and controls on this machine. Broader codec coverage, audio-device testing, physical display scaling, and clean-machine deployment remain acceptance checks. See docs/RELEASE-VALIDATION.md for the packaged release evidence and remaining gates.
