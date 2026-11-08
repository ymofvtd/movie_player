# MyCinema — Local Movie & TV Series Player

## 1. Project Overview

**Project Name:** MyCinema  
**Application Type:** Native Windows Desktop Application  
**Platform:** Windows 10 / Windows 11 (64-bit)  
**Programming Language:** C#  
**Framework:** .NET 10 LTS + WPF  
**Architecture:** MVVM + Service-Based Architecture  
**Video Engine:** LibVLCSharp  
**Database:** SQLite  
**Primary Library Folder:** `D:\Entertaiment`  
**Internet Requirement:** None for normal use

### Project Description

MyCinema is a modern, offline-first Windows desktop application for organizing, browsing, and watching locally stored movies and television series.

The application combines a personal media library with a fully functional video player.

It should automatically detect movies, television shows, seasons, episodes, and movie franchises from existing folder structures.

The application must support playback progress tracking, automatic resume, subtitles, audio selection, full-screen playback, episode navigation, and manual library organization.

The design should feel like a modern streaming platform while functioning entirely on the user's local computer.

### Primary Goals

1. Build a beautiful and responsive movie library.
2. Automatically scan and organize local video files.
3. Support movies, franchises, television shows, seasons, and episodes.
4. Provide smooth, reliable video playback.
5. Remember playback progress across application restarts.
6. Allow convenient navigation between episodes and movies.
7. Support commonly used video formats and subtitle tracks.
8. Allow users to manually correct detected metadata and organization.
9. Work completely offline without a server or user account.
10. Remain maintainable and expandable for future features.

---

## 2. Technology Stack

### Core Technologies

| Technology | Purpose |
|---|---|
| C# | Main application programming language |
| .NET 10 LTS | Application runtime |
| WPF | Windows desktop user interface |
| XAML | Interface layouts and styling |
| MVVM | Separation of UI and application logic |
| LibVLCSharp.WPF | Video playback integration |
| VideoLAN.LibVLC.Windows | Native VLC video engine |
| SQLite | Local application database |
| Microsoft.Data.Sqlite | Database access |
| CommunityToolkit.Mvvm | Observable properties and commands |
| Microsoft.Extensions.DependencyInjection | Dependency injection |
| Microsoft.Extensions.Logging | Application logging |

### Visual Studio Requirements

Install the following workload:

- [x] .NET desktop development

Create a project using:

- Template: WPF Application
- Language: C#
- Framework: .NET 10
- Architecture: x64
- Nullable reference types: Enabled

### Required NuGet Packages

Install these packages in their appropriate projects:

```text
LibVLCSharp.WPF
VideoLAN.LibVLC.Windows
Microsoft.Data.Sqlite
CommunityToolkit.Mvvm
Microsoft.Extensions.DependencyInjection
Microsoft.Extensions.Logging
```

Use compatible stable package versions and test the native VLC runtime on the intended target architecture.

### Optional Development Dependencies

```text
Serilog
Serilog.Sinks.File
Microsoft.Extensions.Configuration.Json
xunit
xunit.runner.visualstudio
Microsoft.NET.Test.Sdk
```

Do not add unnecessary libraries until the associated functionality is implemented.

---

## 3. Application Scope

### Version 1 — Required

- Local movie playback
- Local television-series playback
- Recursive library scanning
- Folder-based media classification
- Movies, shows, seasons, episodes, and franchises
- Movie and episode browser
- Integrated player
- Resume playback
- Rewind and fast-forward
- Seek bar
- Volume controls
- Full-screen playback
- Playback speed controls
- Subtitle selection
- Audio track selection
- Episode sidebar
- Previous/next episode navigation
- Continue Watching section
- SQLite progress persistence
- Manual library organization
- Dark-themed interface
- Search and filtering
- Persistent application settings
- Manual library refresh

### Version 2 — Enhancements

- Movie posters and backdrops
- Automatic metadata lookup
- Custom movie collections
- Multiple editions of the same movie
- Watch history
- Favorites and watchlists
- Episode thumbnails
- Automatic next-episode playback
- File-system monitoring
- Library statistics
- Advanced subtitle customization
- Chapter selection
- Recently added collections
- Keyboard shortcut customization

### Out of Scope for Version 1

- Cloud streaming
- Internet video hosting
- User authentication
- Paid subscriptions
- Remote server hosting
- Mobile applications
- Downloading movies
- Torrent integration
- Video transcoding
- Multi-user synchronization

The first version should prioritize a stable local player over unnecessary features.

---

## 4. Media Library and Folder Structure

### Primary Library Root

```text
D:\Entertaiment\
```

The application must support recursive directory scanning.

The user should be able to add, remove, enable, and disable additional media roots through Settings.

### Example Folder Organization

```text
D:\Entertaiment\
│
├── The Lord of the Rings - Complete\
│   │
│   ├── The Lord Of The Rings The Fellowship
│   │   Of The Ring (2001) [EXTENDED]\
│   │   └── Fellowship.mkv
│   │
│   ├── The Lord Of The Rings The Two Towers
│   │   (2002) [EXTENDED]\
│   │   └── TwoTowers.mkv
│   │
│   └── The Lord Of The Rings The Return Of
│       The King (2003) [EXTENDED]\
│       └── ReturnOfTheKing.mkv
│
├── Game of Thrones\
│   │
│   ├── Season 01\
│   │   ├── Game.of.Thrones.S01E01.mkv
│   │   ├── Game.of.Thrones.S01E02.mkv
│   │   └── Game.of.Thrones.S01E03.mkv
│   │
│   ├── Season 02\
│   │   ├── Game.of.Thrones.S02E01.mkv
│   │   └── Game.of.Thrones.S02E02.mkv
│   │
│   └── Season 03\
│       └── Game.of.Thrones.S03E01.mkv
│
├── Interstellar (2014)\
│   └── Interstellar.mkv
│
└── Breaking Bad\
    ├── Season 01\
    └── Season 02\
```

### Library Organization Types

The application recognizes five hierarchical item types.

| Type | Description |
|---|---|
| Franchise | A collection of related movies |
| Movie | An individual feature film |
| Show | A television series |
| Season | A season belonging to a show |
| Episode | An individual episode |

### Example Detection Results

**The Lord of the Rings**

```text
Franchise: The Lord of the Rings
    Movie: The Fellowship of the Ring
    Movie: The Two Towers
    Movie: The Return of the King
```

**Game of Thrones**

```text
Show: Game of Thrones
    Season 01
        Episode 01
        Episode 02
        Episode 03
    Season 02
        Episode 01
        Episode 02
```

**Interstellar**

```text
Movie: Interstellar
    Year: 2014
    File: Interstellar.mkv
```

The application must not modify, rename, move, or delete original media files during scanning.

---

## 5. Media Scanner System

### 5.1 Scanner Responsibilities

Create a `LibraryScannerService`.

This service must:

1. Read configured library roots.
2. Recursively enumerate directories.
3. Identify supported video files.
4. Examine directory relationships.
5. Detect probable media types.
6. Extract available metadata.
7. Generate normalized media records.
8. Store records in SQLite.
9. Update records when files change.
10. Preserve existing playback history.

### 5.2 Supported Video Extensions

Initial support:

```text
.mkv
.mp4
.avi
.mov
.wmv
.webm
.m4v
.mpg
.mpeg
.ts
.m2ts
```

Actual format and codec support depends on the LibVLC runtime and installed engine capabilities.

### 5.3 Media Classification Rules

Apply classification rules in priority order.

**Rule 1: Television Show**

A directory containing season directories is a strong television-show candidate.

Recognized season directory names include:

```text
Season 1
Season 01
Season 001
S01
Series 1
```

Example:

```text
Game of Thrones\
    Season 01\
    Season 02\
```

Result:

```text
Type: Show
Name: Game of Thrones
```

**Rule 2: Episodes**

Recognize episode patterns such as:

```text
S01E01
S1E1
1x01
S01E01-E02
```

When the season folder is known, an `E01` filename may also be classified as an episode.

Extract:

- Show name
- Season number
- Episode number
- Episode title, if available
- File path

Handle multi-episode files explicitly so they do not appear as unrelated duplicate episodes.

**Rule 3: Movies**

A folder containing one main feature-length video file is a movie candidate.

Example:

```text
Interstellar (2014)\
    Interstellar.mkv
```

Extract:

```text
Title: Interstellar
Year: 2014
Type: Movie
```

**Rule 4: Movie Franchises**

A parent folder containing multiple distinct movie directories may represent a franchise.

Example:

```text
The Lord of the Rings - Complete\
    The Fellowship of the Ring\
    The Two Towers\
    The Return of the King\
```

Result:

```text
Type: Franchise
Title: The Lord of the Rings
Movies: 3
```

Franchise detection should produce a candidate classification, not an irreversible decision.

**Rule 5: Uncertain Media**

If automatic classification is uncertain, place the item into an `Uncategorized` group.

Allow the user to choose:

```text
Set as Movie
Set as Show
Set as Franchise
```

Manual classification overrides automatic detection and persists through rescans.

### 5.4 Filename Cleanup

Normalize display names by:

- Removing video file extensions
- Replacing unnecessary dots and underscores
- Extracting release years
- Detecting edition labels
- Removing common technical tags when appropriate

Examples:

```text
Movie.Name.2001.1080p.BluRay.x264.mkv
```

Becomes:

```text
Movie Name (2001)
```

Preserve useful information like:

```text
Extended Edition
Director's Cut
Theatrical Cut
Remastered
```

### 5.5 Multiple Video Files

Do not assume all video files in one directory are separate movies.

Account for:

- Sample files
- Trailers
- Featurettes
- Bonus features
- Part 1 / Part 2 files
- Different cuts of the same movie
- Duplicate encodes

The scanner should distinguish main content from extras and provide manual correction options.

### 5.6 Scanning Performance

Requirements:

- Scan on a background task.
- Never freeze the WPF interface.
- Support cancellation.
- Show scanning progress.
- Reuse unchanged database records.
- Use file size and modification timestamps for quick change detection.
- Avoid full-file hashing during every scan.
- Detect unreadable directories gracefully.
- Preserve records for temporarily unavailable drives.

---

## 6. Video Playback System

### 6.1 Playback Engine

Implement video playback using:

```text
LibVLCSharp.WPF
VideoLAN.LibVLC.Windows
```

Create a dedicated `PlaybackService` that manages the VLC player.

The playback service must not directly depend on page navigation or individual UI buttons.

### 6.2 Required Player Controls

- Play
- Pause
- Stop
- Rewind
- Fast-forward
- Seek to a selected timestamp
- Previous episode
- Next episode
- Volume adjustment
- Mute/unmute
- Full-screen toggle
- Exit full-screen
- Playback speed
- Audio track selector
- Subtitle track selector
- Subtitle enable/disable
- Current playback time
- Total duration
- Remaining time
- Playback progress indicator

### 6.3 Rewind and Forward

Default actions:

```text
Rewind:  -10 seconds
Forward: +10 seconds
```

Seeking must clamp within the valid playback range.

Do not allow negative playback positions or positions beyond the media duration.

### 6.4 Playback Speed

Initial speed options:

```text
0.5x
0.75x
1.0x
1.25x
1.5x
1.75x
2.0x
```

Default speed:

```text
1.0x
```

### 6.5 Video Control Bar

The player should contain a seek bar and a bottom control panel.

Conceptual layout:

```text
┌────────────────────────────────────────────────────┐
│                                                    │
│                                                    │
│                    VIDEO                           │
│                                                    │
│                                                    │
│                                                    │
├────────────────────────────────────────────────────┤
│ 00:23:15   ━━━━━━━━━●━━━━━━━━━━━   01:45:30       │
│                                                    │
│ ⏮  -10  ▶/Ⅱ  +10  ⏭    🔊  CC  ⚙  ⛶             │
└────────────────────────────────────────────────────┘
```

Controls should appear when the mouse moves over the player.

In full-screen mode, controls may fade after several seconds of mouse inactivity.

They must reappear on user interaction.

### 6.6 Full-Screen Playback

Requirements:

- Fill the active display.
- Hide unnecessary library navigation.
- Preserve video aspect ratio.
- Avoid unintended stretching.
- Support Escape to exit.
- Support double-click to toggle.
- Keep playback uninterrupted when switching modes.
- Restore the previous window state.

**Important WPF Note:**

LibVLCSharp.WPF has video airspace limitations.

Design the controls using the officially supported `VideoView` overlay/content approach, or keep the controls outside the video region.

Test full-screen overlays, z-order, focus, and mouse input early in development.

### 6.7 Keyboard Shortcuts

| Key | Action |
|---|---|
| Space | Play / Pause |
| Left Arrow | Rewind 10 seconds |
| Right Arrow | Forward 10 seconds |
| Up Arrow | Increase volume |
| Down Arrow | Decrease volume |
| F | Toggle fullscreen |
| Escape | Exit fullscreen |
| M | Mute / Unmute |
| N | Next episode |
| P | Previous episode |
| S | Subtitle menu |
| Ctrl + O | Open video file |

Keyboard handling must avoid interfering with text input fields and modal dialogs.

---

## 7. Playback Resume System

### Objective

Remember exactly where the user stopped watching a movie or episode.

### 7.1 Progress Tracking

Store:

```text
Media ID
File ID
Last playback position
Media duration
Last played date/time
Completion status
```

Example:

```text
Title: The Fellowship of the Ring
Position: 01:34:25
Duration: 03:48:18
Completed: false
```

### 7.2 Save Conditions

Save playback progress:

- Every 10 seconds during playback
- When pausing
- When stopping
- When changing episodes
- When navigating away
- When closing the application
- When playback ends

Write progress asynchronously when practical, without losing the last confirmed position.

### 7.3 Resume Behavior

When opening unfinished content:

```text
Resume from 01:34:25?

[Resume]  [Start Over]
```

If the user selects Resume:

- Restore the saved timestamp.
- Wait until the media is ready for seeking.
- Seek to the saved position.
- Start playback.

### 7.4 Completion Rules

Suggested defaults:

```text
Below 5% watched:
    Do not automatically offer resume.

5%–95% watched:
    Save progress and offer resume.

95% or more watched:
    Mark completed when playback naturally ends
    or when the user confirms completion.
```

Do not accidentally mark a film completed just because the user manually seeks near its end.

Allow manual actions:

- Mark as watched
- Mark as unwatched
- Reset progress
- Start from beginning

---

## 8. Television Show and Episode System

### 8.1 Show Details Page

Display:

- Show title
- Poster or fallback artwork
- Description
- Available season count
- Total episode count
- Watched episode count
- Continue Watching button
- Season selector
- Episode list

### 8.2 Season Selection

Example:

```text
Game of Thrones

[ Season 01 ▼ ]

Episode 01 — Winter Is Coming
Episode 02 — The Kingsroad
Episode 03 — Lord Snow
Episode 04 — Cripples, Bastards, and Broken Things
```

The list must sort by season and episode number rather than filename alphabetical order.

### 8.3 Episode Sidebar

When watching an episode, provide a collapsible sidebar on the right side of the player.

```text
┌────────────────────────────┬───────────────────────┐
│                            │ Game of Thrones       │
│                            │                       │
│                            │ Season 01             │
│                            │                       │
│                            │ E01 Winter Is Coming  │
│          VIDEO             │ E02 The Kingsroad     │
│                            │ E03 Lord Snow         │
│                            │ E04 Episode 04        │
│                            │                       │
│                            │ [Next Episode]        │
└────────────────────────────┴───────────────────────┘
```

Requirements:

- Highlight the currently playing episode.
- Display watched indicators.
- Show partial-progress indicators.
- Allow selecting any episode.
- Allow changing seasons.
- Auto-scroll to the active episode.
- Preserve the sidebar open/closed state.

When selecting another episode:

1. Save the current episode's progress.
2. Stop or replace the current media safely.
3. Load the selected episode.
4. Restore its individual resume position when applicable.
5. Update the active sidebar selection.

### 8.4 Episode Navigation

Provide:

```text
Previous Episode
Next Episode
```

At the end of a season, the Next Episode action should select the first available episode of the following season.

Skip missing episode files without displaying broken navigation targets.

### 8.5 Continue Series

The Continue Series action should select the appropriate unfinished or next unwatched episode.

If an episode is partially watched, resume that episode.

If the latest episode is completed, select the next available unwatched episode.

If the show is fully watched, offer to restart from Episode 1.

---

## 9. Movie Franchise System

### 9.1 Franchise Details Page

Example:

```text
The Lord of the Rings

Movies: 3

1. The Fellowship of the Ring (2001)
2. The Two Towers (2002)
3. The Return of the King (2003)
```

Each franchise can contain:

- Franchise title
- Franchise artwork
- List of movies
- Release years
- Movie editions
- Watch progress
- Watched/unwatched indicators

### 9.2 Franchise Browsing

Clicking a franchise opens its details page.

Selecting a movie opens the movie details page.

The application must not treat franchise folders themselves as playable videos.

### 9.3 Manual Franchise Management

Support:

- Create franchise
- Rename franchise
- Add movie to franchise
- Remove movie from franchise
- Change movie ordering
- Delete franchise grouping without deleting video files

### 9.4 Multiple Movie Editions

Support separate media files for editions such as:

```text
Theatrical
Extended
Director's Cut
Remastered
```

Example:

```text
The Fellowship of the Ring
    ├── Theatrical Edition
    └── Extended Edition
```

Each physical edition should have its own playback progress because durations may differ.

---

## 10. User Interface Design

### Design Direction

Create a polished, dark-themed desktop application with a modern streaming-library appearance.

Visual priorities:

- Clean typography
- Dark background
- Large poster artwork
- Rounded cards
- Subtle hover animations
- Comfortable spacing
- Clear navigation
- Smooth scrolling
- Minimal visual clutter
- Readable playback controls

### Suggested Color System

```text
Background:        #101014
Surface:           #1B1B22
Elevated Surface:  #25252E
Primary Text:      #FFFFFF
Secondary Text:    #A0A0AC
Accent:            #8B5CF6
Border:            #33333D
Success:           #22C55E
```

Use theme resources rather than hard-coded colors throughout the XAML files.

### 10.1 Main Navigation

```text
MyCinema
│
├── Home
├── Movies
├── TV Shows
├── Franchises
├── Continue Watching
├── Recently Added
└── Settings
```

### 10.2 Home Page

Sections:

1. Continue Watching
2. Recently Added
3. Movies
4. TV Shows
5. Franchises

Display poster cards in horizontally scrollable rows or responsive grids.

### 10.3 Movie Details Page

Display:

- Movie poster
- Title
- Release year
- Runtime
- Edition
- Description
- Video format
- Watch progress
- Play button
- Resume button
- Start Over button
- Mark Watched button

### 10.4 TV Show Details Page

Display:

- Show banner
- Poster
- Title
- Description
- Available seasons
- Episodes
- Watched indicators
- Continue Watching button

### 10.5 Player Page

Layout:

```text
PlayerPage
│
├── PlayerHeader
│
├── VideoView
│
├── PlaybackControls
│   ├── SeekBar
│   ├── PlayPause
│   ├── Rewind
│   ├── Forward
│   ├── Volume
│   ├── AudioSelector
│   ├── SubtitleSelector
│   ├── SpeedSelector
│   └── Fullscreen
│
└── EpisodeSidebar
```

### 10.6 Settings Page

Include settings for:

- Library root folders
- Manual rescan
- Theme
- Default volume
- Default playback speed
- Resume behavior
- Seek interval
- Next-episode autoplay
- Subtitle preferences
- Hardware acceleration
- Database and cache location
- Clear artwork cache
- Reset watch history
- About application

Avoid placing destructive actions immediately beside ordinary settings.

---

## 11. SQLite Database Design

### Database Location

```text
%LOCALAPPDATA%\MyCinema\Data\library.db
```

Store logs, settings, and artwork separately.

Never store the SQLite database inside the movie library directory by default.

### Table: LibraryRoots

| Field | Purpose |
|---|---|
| Id | Primary key |
| Path | Library directory |
| IsEnabled | Enable/disable root |
| LastScanUtc | Last successful scan |
| CreatedUtc | Date added |

### Table: LibraryItems

| Field | Purpose |
|---|---|
| Id | Primary key |
| ParentId | Parent item ID, nullable |
| ItemType | Franchise/Movie/Show/Season/Episode |
| Title | Display name |
| SortTitle | Normalized sorting title |
| ReleaseYear | Optional year |
| SeasonNumber | Optional |
| EpisodeNumber | Optional |
| Description | Optional |
| PosterPath | Local cached artwork |
| IsManualOverride | Protect classification edits |
| CreatedUtc | Created timestamp |
| UpdatedUtc | Updated timestamp |

The `ParentId` relationship supports:

```text
Franchise → Movie
Show → Season → Episode
```

### Table: MediaFiles

| Field | Purpose |
|---|---|
| Id | Primary key |
| LibraryItemId | Linked library item |
| FilePath | Absolute file location |
| FileSize | File size |
| LastModifiedUtc | Modification timestamp |
| DurationMs | Video duration |
| Edition | Edition information |
| VideoFormat | File extension or container |
| IsAvailable | File availability |
| LastSeenUtc | Last detected during scan |

Create appropriate indexes and enforce unique canonical file paths.

### Table: PlaybackProgress

| Field | Purpose |
|---|---|
| Id | Primary key |
| MediaFileId | Referenced media file |
| PositionMs | Last playback position |
| DurationMs | Last known duration |
| IsCompleted | Completion state |
| LastPlayedUtc | Last played timestamp |
| UpdatedUtc | Last update |

Maintain one current progress record per media file for the default single-user profile.

### Table: UserSettings

```text
Key
Value
```

Use typed serialization and validation in the settings service.

### Table: MetadataOverrides

Store user-defined metadata overrides including:

- Corrected title
- Corrected year
- Manual classification
- Custom artwork
- Custom episode title
- Custom franchise assignment

These values must not be overwritten by automatic rescans.

### Database Requirements

- Enable foreign key enforcement.
- Use transactions for grouped changes.
- Add database schema migrations.
- Create indexes for common lookups.
- Handle database errors gracefully.
- Support backup and recovery.
- Never erase progress during routine rescans.

---

## 12. Application Architecture

### Architecture Pattern

Use MVVM with application services and a separate data-access layer.

The UI must not contain directory scanning, SQL queries, or VLC initialization logic.

### Recommended Solution Structure

```text
MyCinema/
│
├── MyCinema.sln
│
├── src/
│   │
│   ├── MyCinema.App/
│   │   ├── App.xaml
│   │   ├── App.xaml.cs
│   │   ├── MainWindow.xaml
│   │   ├── MainWindow.xaml.cs
│   │   │
│   │   ├── Views/
│   │   │   ├── HomeView.xaml
│   │   │   ├── MoviesView.xaml
│   │   │   ├── ShowsView.xaml
│   │   │   ├── FranchisesView.xaml
│   │   │   ├── DetailsView.xaml
│   │   │   ├── PlayerView.xaml
│   │   │   └── SettingsView.xaml
│   │   │
│   │   ├── ViewModels/
│   │   │   ├── MainViewModel.cs
│   │   │   ├── HomeViewModel.cs
│   │   │   ├── LibraryViewModel.cs
│   │   │   ├── DetailsViewModel.cs
│   │   │   ├── PlayerViewModel.cs
│   │   │   └── SettingsViewModel.cs
│   │   │
│   │   ├── Controls/
│   │   │   ├── MediaCard.xaml
│   │   │   ├── PlaybackControls.xaml
│   │   │   ├── EpisodeSidebar.xaml
│   │   │   └── SeasonSelector.xaml
│   │   │
│   │   ├── Themes/
│   │   │   ├── Colors.xaml
│   │   │   ├── Typography.xaml
│   │   │   └── Controls.xaml
│   │   │
│   │   └── Assets/
│   │
│   ├── MyCinema.Core/
│   │   ├── Models/
│   │   ├── Enums/
│   │   ├── Interfaces/
│   │   └── Utilities/
│   │
│   └── MyCinema.Infrastructure/
│       ├── Database/
│       ├── Repositories/
│       ├── Services/
│       ├── Scanner/
│       ├── Metadata/
│       └── Logging/
│
├── tests/
│   └── MyCinema.Tests/
│
├── docs/
│
├── README.md
└── PROJECT_PLAN.md
```

### Dependency Direction

```text
MyCinema.App
    ↓
MyCinema.Core
    ↑
MyCinema.Infrastructure
```

The App project uses dependency injection to connect abstractions to infrastructure implementations.

Core must not depend on WPF, SQLite, or VLC.

### Key Application Services

**ILibraryScannerService**

```text
ScanLibraryAsync()
ScanDirectoryAsync()
CancelScan()
```

**IMediaClassificationService**

```text
DetectMediaType()
ParseMovie()
ParseEpisode()
DetectFranchiseCandidate()
```

**IPlaybackService**

```text
OpenMedia()
Play()
Pause()
Stop()
Seek()
SetVolume()
SetPlaybackSpeed()
SelectAudioTrack()
SelectSubtitleTrack()
```

**IProgressService**

```text
SaveProgressAsync()
GetProgressAsync()
MarkCompletedAsync()
ResetProgressAsync()
```

**ILibraryService**

```text
GetMoviesAsync()
GetShowsAsync()
GetFranchisesAsync()
GetContinueWatchingAsync()
SearchAsync()
```

**ISettingsService**

```text
LoadSettings()
SaveSettings()
AddLibraryRoot()
RemoveLibraryRoot()
```

**INavigationService**

```text
NavigateToHome()
NavigateToDetails()
NavigateToPlayer()
GoBack()
```

### Playback Lifecycle

The VLC engine should be created and managed centrally.

Ensure:

- One coordinated player session.
- Event subscriptions are released properly.
- Native resources are disposed correctly.
- Switching episodes does not leak player instances.
- VLC callbacks safely dispatch UI updates to the WPF thread.
- Unexpected decode or file errors do not crash the application.

---

## 13. Search and Filtering

### Search Capabilities

Search across:

- Movie titles
- Show titles
- Franchise titles
- Episode titles

Search should update results without blocking the interface.

### Sorting Options

```text
Title A–Z
Title Z–A
Release Year
Recently Added
Recently Watched
Unwatched First
```

### Filters

```text
All
Movies
TV Shows
Franchises
Watched
Unwatched
In Progress
```

Only show filters applicable to the current library page.

---

## 14. Subtitle and Audio Management

### Supported Subtitle Sources

- Embedded subtitle tracks
- External `.srt` files
- External `.ass` files
- External `.ssa` files
- Other formats supported by LibVLC

### Subtitle Features

- Select subtitle track
- Disable subtitles
- Load external subtitle file
- Remember subtitle preference where feasible
- Display subtitle language
- Handle absent or unsupported subtitles gracefully

### Audio Features

- Enumerate available audio tracks
- Select audio language
- Display track information when available
- Remember selection preferences when feasible
- Support volume and mute
- Handle stereo and multichannel media through LibVLC

### Future Enhancements

- Subtitle delay adjustment
- Subtitle size
- Subtitle font selection
- Subtitle position
- Preferred language rules
- Preferred audio language

Track selections should use stable identifiers or language preferences where possible rather than assuming track indexes never change.

---

## 15. Settings and Persistence

All major settings must survive application restart.

Suggested default settings:

```text
LibraryRoot:
    D:\Entertaiment

Theme:
    Dark

DefaultVolume:
    80

SeekIntervalSeconds:
    10

PlaybackSpeed:
    1.0

ResumePlayback:
    true

ShowResumePrompt:
    true

AutoPlayNextEpisode:
    false

RememberSidebarState:
    true

HardwareAcceleration:
    Auto
```

Store user preferences under:

```text
%LOCALAPPDATA%\MyCinema\
```

Validate configuration values during application startup.

If the configuration is missing or malformed, restore safe defaults without deleting the media database.

---

## 16. Error Handling and Edge Cases

### Missing Media File

If the video file no longer exists:

- Do not crash.
- Show a clear file-unavailable message.
- Keep saved progress temporarily.
- Allow library rescan.
- Allow relinking the file.

### Unavailable Library Drive

If `D:\Entertaiment` is temporarily unavailable:

- Keep the library records.
- Mark media as unavailable.
- Do not delete watch history.
- Retry when the drive becomes accessible.

### Renamed or Moved Media

Implement a best-effort identity matching strategy using available file identity information and lightweight fingerprints.

Do not blindly assign another file's watch progress merely because its filename is similar.

### Unsupported Video

If LibVLC cannot play a file:

- Display an error.
- Log the underlying playback failure.
- Allow the user to return to the library.
- Do not mark the item watched.

### Corrupted Database

Provide:

- Safe error reporting
- Backup database option
- Recovery or rebuild instructions
- Preservation of backup files

### Multi-Part Movies

Recognize multiple file parts where practical.

Do not silently combine independent videos without verifying that the grouping is correct.

### Duplicate Files

Detect repeated references to the same canonical file path.

Avoid showing duplicate library entries caused by overlapping root directories.

---

## 17. Performance Requirements

### Startup

- Show the main window promptly.
- Initialize the database during startup.
- Load cached library records first.
- Perform long scans asynchronously.
- Avoid rescanning everything on every application launch.

### Library Browsing

- Support large collections.
- Use UI virtualization.
- Load posters asynchronously.
- Cache image thumbnails.
- Avoid loading full-size images for small cards.
- Keep scrolling smooth.

### Playback

- Prioritize uninterrupted playback.
- Prevent database operations from blocking video callbacks.
- Avoid creating unnecessary media-player instances.
- Use hardware acceleration where supported and reliable.
- Handle seeking and track changes safely.

### Memory

- Dispose unused VLC resources.
- Unsubscribe from events.
- Limit cached artwork.
- Avoid retaining obsolete ViewModels unnecessarily.
- Monitor memory usage after repeated video switches.

---

## 18. Security and Privacy

MyCinema is an offline-first local application.

Requirements:

- No account required.
- No telemetry by default.
- No cloud upload of library information.
- No unnecessary network access.
- No execution of media filenames as commands.
- No automatic modification of source video files.
- No destructive filesystem operations during scanning.

If online metadata is introduced later, make it optional and clearly disclose what titles are sent to the metadata provider.

Store all user data locally.

---

## 19. Development Roadmap

Complete the following phases in order.

Every phase must build successfully and pass its acceptance checks before moving to the next phase.

### Phase 0 — Project Foundation

Tasks:

- [x] Create WPF solution.
- [x] Target .NET 10.
- [x] Configure x64 build.
- [x] Create App, Core, Infrastructure, and Tests projects.
- [x] Configure project references.
- [x] Install required NuGet packages.
- [x] Add dependency injection.
- [x] Configure logging.
- [x] Create basic dark theme.
- [x] Create main application shell.
- [x] Add navigation infrastructure.

**Completion Criteria:**

Application launches, displays the navigation shell, and closes without errors.

**Verified 2026-11-08:** Debug solution build succeeds with zero warnings and errors; all six foundation tests pass. Desktop checks verified launch, Home/Settings navigation, back navigation, library section routes, keyboard selection, and clean shutdown with exit code 0. Startup logging confirms an x64 process. The original root WPF project is retained as the App project, with assembly name `MyCinema.App`; Core and Infrastructure are under `src`, and Tests under `tests`. Library routes currently display introductory empty states; playback, scanning, persisted settings, and database behavior are not implemented or marked complete. Next: Phase 1.

### Phase 1 — Basic Video Player

Tasks:

- [x] Initialize LibVLC.
- [x] Integrate WPF VideoView.
- [x] Open a local video file.
- [x] Implement play.
- [x] Implement pause.
- [x] Implement stop.
- [x] Implement seek bar.
- [x] Implement rewind 10 seconds.
- [x] Implement forward 10 seconds.
- [x] Implement volume.
- [x] Implement mute.
- [x] Display video duration.
- [x] Display current playback position.
- [x] Implement full-screen mode.
- [x] Implement keyboard shortcuts.
- [x] Verify overlay behavior.
- [x] Handle playback errors.

**Completion Criteria:**

A local MKV or MP4 file plays reliably with working controls, seeking, and full-screen mode.

**Verified 2026-11-08:** Release build succeeds and all 22 automated tests pass, including three integration tests using the packaged LibVLC runtime. Native tests verify actual seeking, pause, stop, replay, natural completion, shared-player file switching, file release on disposal, missing/corrupt file errors, and recovery. Desktop checks with local 4K H.265 MKV media verify rendered playback, time labels, seek-bar dragging, ten-second skips, volume/mute controls, fullscreen transitions, Escape restoration, double-click toggling, and clean shutdown during active playback (exit code 0). The video HWND remains alive during navigation, which preserves the picture and current session position when returning to the player. Controls sit below the video; the supported VideoView content overlay handles mouse input. Phase 1 shortcuts are Ctrl+O, Space, arrows, M, F, and Escape; episode and subtitle shortcuts remain assigned to later phases. Database persistence, saved progress, advanced playback controls, and broader codec/hardware testing remain incomplete. See README.md for operation and test details. Next: Phase 2.

### Phase 2 — Database Foundation

Tasks:

- [x] Initialize SQLite.
- [x] Implement database schema.
- [x] Create database migrations.
- [x] Add repositories.
- [x] Add LibraryRoot storage.
- [x] Add LibraryItem storage.
- [x] Add MediaFile storage.
- [x] Add progress storage.
- [x] Add settings persistence.
- [x] Test database restart persistence.

**Completion Criteria:**

Library data and settings can be saved, retrieved, and updated without corruption.

**Verified 2026-11-08:** Release build succeeds with zero warnings/errors and all 41 tests pass (22 existing tests plus 19 database cases). SQLite schema version 1 includes all six planned data tables, migration history, foreign keys, hierarchy checks, indexes, and unique canonical Windows paths. Repositories save/retrieve/update library roots, library items, media files, independent per-file progress, settings, and metadata overrides. Tests dispose and reopen service containers to verify disk persistence, and cover migration idempotence/rollback, transaction rollback, invalid relationships, duplicate paths, unavailable-file progress preservation, stale progress saves, malformed settings, independent concurrent connections, cancellation, backup/restore, newer schemas, and damaged/unwritable databases. Desktop checks verify first-launch database creation, Home/Settings navigation, a second launch reopening the existing database, and both clean shutdowns (exit code 0). The real application database passes integrity checking with one migration record; no sample library rows were added. A backend backup API refuses to overwrite existing files, and README.md documents recovery. Scanning, editable/applied player preferences, automatic progress saving, resume UI, and backup UI remain in later phases. Next: Phase 3.

### Phase 3 — Folder Scanning

Tasks:

- [x] Configure `D:\Entertaiment`.
- [x] Implement recursive scanning.
- [x] Detect supported video extensions.
- [x] Detect movie folders.
- [x] Detect show folders.
- [x] Detect season folders.
- [x] Parse episode numbers.
- [x] Detect franchise candidates.
- [x] Extract movie years.
- [x] Detect edition labels.
- [x] Save scan results to SQLite.
- [x] Implement background scanning.
- [x] Implement scan cancellation.
- [x] Implement progress reporting.
- [x] Implement manual rescan.
- [x] Preserve user overrides.

**Completion Criteria:**

The Lord of the Rings folders appear as a franchise candidate containing individual movies.

Game of Thrones appears as a show with correctly ordered seasons and episodes.

**Verified 2026-11-08:** Release build succeeds without warnings/errors and all 68 tests pass. The scanner runs discovery/classification on a worker, reports progress, supports cancellation, and commits each root atomically. Schema migration 2 adds stable scanner identities, media-root ownership, and multi-episode endpoints without changing v1 file/progress records. Rescans retain identities and progress, reuse unchanged metadata/duration, respect manual overrides, and mark confirmed missing files unavailable. Inaccessible roots/subtrees retain saved records; linked paths are skipped; enabled overlapping roots are scanned once. Tests cover the Lord of the Rings/Game of Thrones examples, mixed collections, all extensions, episode patterns/order/ranges, years/editions, alternate cuts/parts, extras, ambiguity, cancellation/concurrency, rollback, missing/returning media, upgrade preservation, and the scan ViewModel. Desktop verification upgraded the actual database, scanned `D:\Entertaiment`, expanded the Harry Potter collection, and loaded saved results after restart. It found 73 files: 50 movies, 23 episodes, two shows (BAYM and Loki), three seasons, and two franchise candidates (Harry Potter and Marvel), with zero warnings. Clean shutdown exits with code 0, and the real database passes integrity checking with two migrations. Settings includes manual scan/cancel controls and a read-only hierarchy preview; full library browsing, playback from library items, metadata correction UI, additional-root editing, and automatic watch-progress persistence remain in later phases. Next: Phase 4.

### Phase 4 — Media Library Interface

Tasks:

- [x] Create Home page.
- [x] Create Movies page.
- [x] Create Shows page.
- [x] Create Franchises page.
- [x] Create media cards.
- [x] Create movie details page.
- [x] Create show details page.
- [x] Create franchise details page.
- [x] Create season selector.
- [x] Create episode listing.
- [x] Implement search.
- [x] Implement sorting.
- [x] Implement filtering.
- [x] Connect cards to playback navigation.

**Completion Criteria:**

The user can browse the detected library and open any available movie or episode.

**Verified 2026-11-08:** Release build succeeds without warnings/errors and all 83 tests pass. Home provides bounded rows for recent additions, movies, shows, franchises, and uncategorized media. Movies, TV Shows, Franchises, and Recently Added browse a consistent SQLite catalog, including unavailable records. Case-insensitive search is debounced on a worker and includes episode titles and parent context on Home/Shows. Applicable type, availability, year, and stored watch-state filters combine with all six sort options. Listings load cards in batches of 60. Details support movie editions/parts, numeric season/episode selection, explicit episode ranges, franchise ordering, file metadata, unavailable-file handling, and opening selected media through the existing shared player. Back navigation retains item identity and session selections. Scans refresh the browser automatically; saved records load on restart. Tests cover these behaviors, rapid query replacement, read-only progress aggregation, restart persistence, empty states, and deferred WPF template-resource loading. Desktop checks used the real catalog: searched Movies, played Ant-Man, selected Loki Season 02 and played Ouroboros, restored that season through Back, and browsed Harry Potter's eight films in ordinal order. Clean shutdown exits with code 0. No schema migration or media-file changes were needed. Artwork uses offline fallbacks; metadata/artwork editing remains in later phases. Automatic progress saving, resume actions, and Continue Watching remain Phase 5. Next: Phase 5.

### Phase 5 — Resume and Watch History

Tasks:

- [x] Track current playback position.
- [x] Save progress periodically.
- [x] Save on pause.
- [x] Save on episode switching.
- [x] Save on application closing.
- [x] Restore progress on reopening.
- [x] Implement Resume/Start Over dialog.
- [x] Implement completed/uncompleted state.
- [x] Implement Continue Watching section.
- [x] Implement mark watched/unwatched.
- [x] Test progress after application restart.

**Completion Criteria:**

A video paused midway can resume from its saved position after completely closing and reopening MyCinema.

**Implemented and tested 2026-11-08:** Progress for scanned files saves on a serialized worker queue every ten seconds and at pause, Stop, file changes, navigation away, natural end, and shutdown. Stop preserves the preceding saved position. Resume/Start Over/Cancel uses the stored resume preferences and a 5% threshold; seeking into the credits does not mark watched. Continue Watching on Home and in its sidebar page includes available unfinished movies and episodes, progress labels/bars, recent-play ordering, and edition-aware navigation. Details expose watched/unwatched, reset, and Start Over actions. Release builds without warnings/errors; all 98 tests pass. A native player/SQLite/container reopening test verifies saved seek position, resume on a new player, and Stop/replay from zero. WPF templates render at the minimum window width. Interactive desktop restart acceptance remains pending because Computer Use rejected the app launch with “Computer Use was not approved to use app.” No database migration or media changes were required. Next: Phase 6.

### Phase 6 — Episode Sidebar

Tasks:

- [x] Create episode sidebar component.
- [x] Connect sidebar to the active show.
- [x] Add season selector.
- [x] Display episode list.
- [x] Highlight active episode.
- [x] Show episode progress.
- [x] Implement click-to-play.
- [x] Implement previous episode.
- [x] Implement next episode.
- [x] Preserve progress when changing episodes.
- [x] Implement sidebar collapse/expand.
- [x] Test season transitions.

**Completion Criteria:**

The user can watch Game of Thrones, select a different episode from the sidebar, and return to the previous episode without losing progress.

**Implemented and tested 2026-11-08:** The player has a right-hand show/season/episode component with current-episode highlighting, watched indicators, per-file progress, and click-to-play through the existing resume workflow. Previous/next buttons and P/N shortcuts traverse numeric episode order across seasons, skip unavailable/deleted files and empty seasons, and stop at show boundaries. Episode commands exclude overlapping opens and keep the shared native player and fullscreen state. Collapse state survives SQLite/container reopening and respects `RememberSidebarState`. Catalog refreshes preserve the browsed season and stable episode rows. Movies and unscanned files clear the show context; directly opened scanned episode paths are recognized. Release builds without warnings/errors; all 110 tests pass, including eleven sidebar cases, WPF renders at 604×400 / 956×540 / 1180×540, and a native VLC season-switch/return test that resumes at eight seconds on the same player. Interactive desktop acceptance (including keyboard focus, auto-scroll, sidebar resizing around the native video, and fullscreen transitions) remains pending: Computer Use rejected launch with “Computer Use was not approved to use app.” No schema migration or source-media changes are needed. Next: remaining Phase 7 tasks.

### Phase 7 — Advanced Playback

Tasks:

- [x] Add playback speed selection.
- [x] Add audio track selection.
- [x] Add subtitle track selection.
- [x] Add external subtitle loading.
- [x] Add subtitle enable/disable.
- [x] Implement player control auto-hide.
- [x] Improve full-screen controls.
- [x] Implement aspect ratio handling.
- [x] Improve error reporting.
- [x] Test common container formats.

**Completion Criteria:**

Playback controls, subtitle selection, track selection, and full-screen mode behave reliably across representative files.

**Brought forward 2026-11-08:** The player exposes audio-language and subtitle selectors, subtitle Off, and local subtitle loading. Track descriptions/language tags and actual VLC IDs are refreshed as streams appear or change; selection works during play/pause without reopening media. Generated native fixtures verify Russian/English audio switching, embedded and external subtitles, stale-track clearing on file changes, and invalid/missing selection recovery. Track choices currently last for the playback session. Remaining Phase 7 tasks and interactive desktop acceptance are still pending.

**Implemented and tested 2026-11-08:** Remaining Phase 7 tasks are implemented. Playback options offer all seven speeds from 0.5× to 2× and Original/16:9/4:3/2.35:1 picture ratios, applied through the existing serialized native player without reopening media. Speed and picture choices survive Stop/replay and file switches within the session. A compact expandable options panel preserves usable space for episodes at minimum window size. Fullscreen header/controls hide after three idle seconds and reappear on mouse/keyboard interaction; pause, seeking, errors, open menus, hover, and control focus prevent hiding. Input uses the supported VideoView content overlay, including cursor hiding, Tab access to controls, and the S subtitle-menu shortcut. Errors expose file/native details, a dismiss action, and Retry for failed sessions; option errors preserve current playback. Release builds with zero warnings/errors; all 119 tests pass, including native speed/aspect/replay/file-switch/error cases, video-frame decoding and seek/speed tests for generated MKV/MP4/MOV/AVI/WMV fixtures, controlled-clock inactivity cases, and actual WPF fullscreen visibility and minimum-layout assertions. The running desktop renders local video with the updated layout. Live user input interrupted the desktop check, so automated input stopped; interactive fullscreen keyboard/menu/cursor checks and earlier Phases 5–6 desktop acceptance remain pending. No schema migration or source-media changes were required. Next implementation: Phase 8, with desktop acceptance still to finish before release.

### Phase 8 — Manual Organization

Tasks:

- [x] Implement manual classification.
- [x] Allow changing Movie/Show/Franchise.
- [x] Implement franchise creation.
- [x] Implement movie reassignment.
- [x] Implement title editing.
- [x] Implement year editing.
- [x] Implement episode metadata editing.
- [x] Implement custom poster assignment.
- [x] Preserve changes after rescanning.
- [x] Handle duplicate or incorrectly grouped media.

**Completion Criteria:**

The user can correct incorrectly detected library items and those corrections survive an application restart and rescan.

**Implemented and tested 2026-11-09:** Details pages provide poster assignment/removal, a return-to-folder-artwork action, and an expandable metadata/organization editor. Scans automatically discover poster.png, poster.jpg, or poster.jpeg at movie/show/franchise folder roots. Explicit image choices/removal take priority; metadata corrections allow folder artwork to keep updating. Movies, shows, and franchises render artwork on portrait cards and details; episodes may inherit a show's poster. Chosen images decode to a cached PNG copy and survive removal of their source; folder images remain in place and update on the next scan. Missing/invalid images fall back safely. Title/year/description edits, episode show/season/number/range correction, all six Movie/Show/Franchise conversions, franchise creation/reassignment/reordering/removal, duplicate merging, and separating grouped files use transactional catalog writes. Physical media and file/progress IDs are retained. Schema migration 3 supports scanner aliases for merged titles and removed groupings; migration 4 separates explicit poster choices from folder discovery. Edits and new-child classification survive repeated rescans, including naming changes without duplicate seasons/items. Debug and isolated Release builds have zero warnings/errors; all 144 tests pass in Release, including 19 organization cases, six folder-artwork cases, v1/v2/v3 migration preservation, and actual WPF image/editor renders at widths 604 and 956. Poster commands are exercised with injected file selection and actual cache/folder files, including invalid images and navigation during import. The existing Release app was left running; the verified updated executable is under `.artifacts/phase8/bin/Person Movie Player/release`. Live file-picker/organization acceptance and earlier Phases 5–7 desktop checks remain pending. Next implementation: Phase 9 — Interface Polish.

### Phase 9 — Interface Polish

Tasks:

- [x] Finalize dark theme.
- [x] Improve navigation.
- [x] Refine movie cards.
- [x] Add hover effects.
- [x] Add skeleton/loading states.
- [x] Improve empty states.
- [x] Improve error messages.
- [x] Add tooltips.
- [x] Improve keyboard navigation.
- [x] Test display scaling.
- [x] Optimize artwork caching.
- [x] Add optional animations.

**Completion Criteria:**

The application is comfortable to navigate with mouse and keyboard and looks consistent across all pages.

**Implemented and tested 2026-11-09:** Consistent dark dropdowns, scrollbars, tooltips, checkboxes, expanders, seek/volume sliders, and stable focus outlines complement refined cards, unavailable badges, accessible descriptions, and hover/press feedback. Home rows link to full sections. Ctrl+F selects library search, Escape clears it, Alt+Left navigates back, and F6 switches navigation/content focus without changing the selected destination. Focused controls retain Space/arrow behavior while letter playback shortcuts remain available from buttons. Loading skeletons suppress misleading empty states; contextual recovery actions distinguish new libraries, unmatched searches, and empty Continue Watching. Refresh errors expose retry and retain loaded cards. Frozen artwork is shared in a bounded 32 MiB/128-entry LRU with file-stamp invalidation, corrupt-image fallback caching, released file handles, and 480×960 decode limits. Optional 140 ms page fades respect Windows motion settings and an appearance preference saved without changing playback settings; no schema migration is required. The isolated Release build has zero warnings/errors and all 147 tests pass. WPF checks exercise actual slider-thumb/key events and render library layouts at widths 604/956 and 100%/125%/150%/200% DPI, with details, Settings, and player at all four scales, plus loading/error/empty renders. Screenshots are under `.artifacts/phase9-final/bin/MyCinema.Tests/release/Artifacts`; the final executable is under `.artifacts/phase9-final/bin/Person Movie Player/release`. Desktop verification launched an intermediate build, but window binding failed twice because the tool assigned the new and original processes the same application identity; automated input stopped. Live shortcuts, mouse/keyboard comfort, physical monitor DPI transitions/native video scaling, and earlier Phases 5–8 desktop acceptance remain pending for release. Next: Phase 10 — Testing and Release.

### Phase 10 — Testing and Release

Tasks:

- [ ] Test scanning accuracy.
- [ ] Test corrupted video handling.
- [ ] Test missing file handling.
- [ ] Test disconnected library drive.
- [ ] Test database persistence.
- [ ] Test resume accuracy.
- [ ] Test episode switching.
- [ ] Test repeated full-screen toggling.
- [ ] Test VLC resource disposal.
- [ ] Test repeated application restarts.
- [ ] Test large libraries.
- [ ] Verify playback on a clean Windows machine.
- [ ] Package VLC native dependencies.
- [ ] Verify third-party licenses.
- [ ] Create release build.
- [ ] Create README and installation instructions.

**Completion Criteria:**

A clean Windows installation can launch MyCinema and play local media without requiring Visual Studio.

---

## 20. Testing Plan

### Unit Tests

Test the following independently:

- Movie filename parsing
- Episode filename parsing
- Season extraction
- Franchise detection
- Filename normalization
- Completion calculations
- Next-episode selection
- Library sorting
- Playback progress calculations

### Integration Tests

Test:

- SQLite data insertion and retrieval
- Database migrations
- Recursive library scanning
- Media classification persistence
- User metadata override preservation
- Progress updates
- Missing-file recovery

### Manual Playback Tests

| Test | Expected Result |
|---|---|
| Open MKV | Video plays |
| Open MP4 | Video plays |
| Pause video | Playback pauses |
| Rewind 10 seconds | Position decreases |
| Forward 10 seconds | Position increases |
| Drag seek bar | Playback seeks |
| Switch episode | Selected episode loads |
| Restart application | Progress persists |
| Enable subtitles | Selected subtitle displays |
| Select audio track | Audio changes |
| Enter fullscreen | Video fills display correctly |
| Exit fullscreen | Window state restores |
| Remove a file | App displays unavailable state |
| Disconnect library drive | Watch history remains |

Test large files and long-duration videos in addition to small samples.

---

## 21. Version 1 Acceptance Criteria

Version 1 is considered successful when all the following are true.

### Library

- [ ] The application scans `D:\Entertaiment`.
- [ ] Movies are detected.
- [ ] TV shows are detected.
- [ ] Seasons and episodes are identified.
- [ ] Franchises can be created or detected.
- [ ] Users can correct classifications.
- [ ] Library records persist after restart.

### Playback

- [ ] Videos play smoothly.
- [ ] Play/Pause works.
- [ ] Seek bar works.
- [ ] Rewind/Forward works.
- [ ] Volume works.
- [ ] Fullscreen works.
- [ ] Subtitle selection works.
- [ ] Audio track selection works.
- [ ] Playback speed works.

### Resume

- [ ] Watch progress saves automatically.
- [ ] Resume works after restarting.
- [ ] Each episode has independent progress.
- [ ] Each movie file has independent progress.
- [ ] Continue Watching works.
- [ ] Completed items can be restarted.

### User Interface

- [ ] Dark interface.
- [ ] Movies page.
- [ ] Shows page.
- [ ] Franchises page.
- [ ] Details pages.
- [ ] Episode sidebar.
- [ ] Search and sorting.
- [ ] Settings page.

### Stability

- [ ] No crashes during normal playback.
- [ ] Missing files are handled.
- [ ] Scanning does not freeze the UI.
- [ ] Restart preserves user data.
- [ ] Repeated video switching does not leak native player resources.

---

## 22. Future Development

After Version 1 is fully functional, consider:

### Media Metadata

- Optional TMDB integration
- Automatic poster downloading
- Backdrop downloading
- Movie descriptions
- Cast information
- Genre classification
- Release dates

### Advanced Library

- Multiple library roots
- Network drive support
- Smart collections
- Favorites
- Watchlists
- Custom tags
- Library analytics

### Advanced Player

- Chapter navigation
- Subtitle delay
- Audio delay
- Custom subtitle appearance
- Frame stepping
- Playback bookmarks
- Intro skip markers
- Automatic next-episode countdown
- Picture-in-picture mode

### Personalization

- Custom themes
- Multiple watch profiles
- Poster view / list view
- Custom keyboard shortcuts
- Separate watch history by profile

These features should not delay the basic application.

---

## 23. Development Rules

Follow these rules throughout development.

1. Keep the application functional after every phase.
2. Do not write the entire project in a single file.
3. Use MVVM consistently.
4. Use dependency injection for services.
5. Keep database operations out of Views.
6. Keep video engine logic out of ViewModels where possible.
7. Use asynchronous operations for slow tasks.
8. Never block the WPF UI thread with scanning.
9. Do not replace existing working functionality unnecessarily.
10. Preserve watch history during rescans.
11. Never delete media automatically.
12. Handle missing files and invalid metadata safely.
13. Dispose native VLC resources correctly.
14. Use clear naming conventions.
15. Add logging for recoverable and unrecoverable errors.
16. Test each new feature before advancing.
17. Prefer simple, reliable solutions over premature abstractions.
18. Keep the original media directory structure intact.
19. Make the application usable fully offline.
20. Maintain the README and this project plan as functionality changes.

---

## 24. Recommended Implementation Order

Build the smallest functional application first.

```text
STEP 1
Create WPF application
        ↓
STEP 2
Integrate LibVLCSharp
        ↓
STEP 3
Play one local video
        ↓
STEP 4
Implement video controls
        ↓
STEP 5
Create SQLite database
        ↓
STEP 6
Scan D:\Entertaiment
        ↓
STEP 7
Detect movies and shows
        ↓
STEP 8
Build library browser
        ↓
STEP 9
Implement resume playback
        ↓
STEP 10
Implement episode sidebar
        ↓
STEP 11
Implement franchise management
        ↓
STEP 12
Implement advanced playback
        ↓
STEP 13
Polish interface
        ↓
STEP 14
Test and package application
```

**Important:** Do not start with a fully animated streaming-service interface.

First build a simple window that reliably plays a local MKV file with play/pause, seeking, and fullscreen. Then add the database, library scanner, and media organization features.

---

## 25. Final Product Vision

MyCinema should become a personal, fully offline entertainment application.

The user opens MyCinema and immediately sees:

- Movies ready to watch
- Organized television shows
- Movie franchises
- Recently added content
- Previously watched content
- Movies and episodes ready to resume

Selecting a film opens a polished video player.

Selecting a television show displays seasons and episodes.

While watching a show, the user can switch episodes through the player sidebar.

The application automatically remembers playback progress and restores it after restarting.

The entire experience operates using the user's local media collection without requiring a server, cloud account, or internet connection.

**Final goal:** A polished Windows media library and video player that provides the organization and convenience of a streaming platform while keeping all media and playback data on the user's own computer.

---

## 26. Implementation Instructions for an AI Coding Assistant

When implementing this project:

1. Read this entire project plan before making changes.
2. Inspect the current solution structure and existing code.
3. Determine which development phase has already been completed.
4. Implement only the next incomplete phase unless explicitly instructed otherwise.
5. Use the specified C#, .NET, WPF, LibVLCSharp, SQLite, and MVVM architecture.
6. Write complete, compilable production code instead of placeholders.
7. Avoid inventing unsupported third-party APIs.
8. Preserve existing working code and user data.
9. Add reasonable error handling and logging.
10. Build the solution and fix compilation errors.
11. Run relevant automated tests.
12. Verify functional behavior where possible.
13. Summarize files created or changed.
14. Explain any remaining issues or manual testing requirements.
15. Update the relevant checklist items only when they have actually been verified.

Do not claim a feature is completed merely because its UI exists.

A feature is completed only when its underlying behavior works and has been tested.

### First Development Task

Begin with **Phase 0 — Project Foundation**, followed by **Phase 1 — Basic Video Player**.

The first functional milestone is:

> A native WPF Windows application that opens a local MKV or MP4 file and supports play, pause, rewind 10 seconds, forward 10 seconds, seeking, volume control, and fullscreen playback.

After that milestone is verified, continue with library scanning and organization.

---

## 27. Technical References

- .NET support policy: https://dotnet.microsoft.com/en-us/platform/support/policy
- WPF documentation: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/
- LibVLCSharp: https://github.com/videolan/libvlcsharp
- LibVLCSharp WPF: https://www.nuget.org/packages/LibVLCSharp.WPF
- SQLite: https://www.sqlite.org/
- Microsoft.Data.Sqlite: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/
- CommunityToolkit.Mvvm: https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/

---

**Project:** MyCinema  
**Document:** PROJECT_PLAN.md  
**Version:** 1.0  
**Status:** Phase 9 implemented and tested; live desktop acceptance for Phases 5–9 pending / Next: Phase 10 — Testing and Release.
