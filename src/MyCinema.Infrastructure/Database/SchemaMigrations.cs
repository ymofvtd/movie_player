namespace MyCinema.Infrastructure.Database;

internal static class SchemaMigrations
{
    // Append migrations; never edit a migration after it has shipped.
    internal static readonly (int Version, string Name, string Sql)[] All =
    [
        (1, "Initial library storage", """
        CREATE TABLE SchemaMigrations (
            Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedUtc TEXT NOT NULL
        );
        CREATE TABLE LibraryRoots (
            Id INTEGER PRIMARY KEY,
            Path TEXT NOT NULL CHECK(length(trim(Path)) > 0),
            CanonicalPath TEXT NOT NULL UNIQUE,
            IsEnabled INTEGER NOT NULL CHECK(IsEnabled IN (0, 1)),
            LastScanUtc TEXT, CreatedUtc TEXT NOT NULL
        );
        CREATE TABLE LibraryItems (
            Id INTEGER PRIMARY KEY,
            ParentId INTEGER REFERENCES LibraryItems(Id) ON DELETE RESTRICT,
            ItemType INTEGER NOT NULL CHECK(ItemType BETWEEN 0 AND 5),
            Title TEXT NOT NULL CHECK(length(trim(Title)) > 0),
            SortTitle TEXT NOT NULL,
            ReleaseYear INTEGER CHECK(ReleaseYear BETWEEN 1 AND 9999),
            SeasonNumber INTEGER CHECK(SeasonNumber >= 0),
            EpisodeNumber INTEGER CHECK(EpisodeNumber >= 0),
            Description TEXT, PosterPath TEXT,
            IsManualOverride INTEGER NOT NULL CHECK(IsManualOverride IN (0, 1)),
            CreatedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL,
            CHECK(ParentId IS NULL OR ParentId <> Id)
        );
        CREATE INDEX IX_LibraryItems_Parent ON LibraryItems(ParentId, SeasonNumber, EpisodeNumber);
        CREATE INDEX IX_LibraryItems_TypeTitle ON LibraryItems(ItemType, SortTitle);
        CREATE INDEX IX_LibraryItems_Created ON LibraryItems(CreatedUtc DESC);
        CREATE TABLE MediaFiles (
            Id INTEGER PRIMARY KEY,
            LibraryItemId INTEGER NOT NULL REFERENCES LibraryItems(Id) ON DELETE RESTRICT,
            FilePath TEXT NOT NULL CHECK(length(trim(FilePath)) > 0),
            CanonicalPath TEXT NOT NULL UNIQUE,
            FileSize INTEGER NOT NULL CHECK(FileSize >= 0),
            LastModifiedUtc TEXT NOT NULL,
            DurationMs INTEGER NOT NULL CHECK(DurationMs >= 0),
            Edition TEXT, VideoFormat TEXT,
            IsAvailable INTEGER NOT NULL CHECK(IsAvailable IN (0, 1)),
            LastSeenUtc TEXT NOT NULL
        );
        CREATE INDEX IX_MediaFiles_Item ON MediaFiles(LibraryItemId);
        CREATE TABLE PlaybackProgress (
            Id INTEGER PRIMARY KEY,
            MediaFileId INTEGER NOT NULL UNIQUE REFERENCES MediaFiles(Id) ON DELETE RESTRICT,
            PositionMs INTEGER NOT NULL CHECK(PositionMs >= 0),
            DurationMs INTEGER NOT NULL CHECK(DurationMs >= 0),
            IsCompleted INTEGER NOT NULL CHECK(IsCompleted IN (0, 1)),
            LastPlayedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL,
            CHECK(DurationMs = 0 OR PositionMs <= DurationMs)
        );
        CREATE INDEX IX_PlaybackProgress_Recent ON PlaybackProgress(IsCompleted, LastPlayedUtc DESC);
        CREATE TABLE UserSettings (
            Key TEXT PRIMARY KEY NOT NULL CHECK(length(trim(Key)) > 0), Value TEXT NOT NULL
        );
        CREATE TABLE MetadataOverrides (
            LibraryItemId INTEGER NOT NULL REFERENCES LibraryItems(Id) ON DELETE RESTRICT,
            Field TEXT NOT NULL CHECK(length(trim(Field)) > 0), Value TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL,
            PRIMARY KEY(LibraryItemId, Field)
        );
        CREATE TRIGGER LibraryItems_ParentOnInsert BEFORE INSERT ON LibraryItems
        WHEN NEW.ParentId IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'Invalid library hierarchy') WHERE NOT EXISTS (
                SELECT 1 FROM LibraryItems p WHERE p.Id = NEW.ParentId AND (
                    (p.ItemType = 1 AND NEW.ItemType = 2) OR
                    (p.ItemType = 3 AND NEW.ItemType = 4) OR
                    (p.ItemType = 4 AND NEW.ItemType = 5)));
        END;
        CREATE TRIGGER LibraryItems_ParentOnUpdate BEFORE UPDATE OF ParentId, ItemType ON LibraryItems
        BEGIN
            SELECT RAISE(ABORT, 'Invalid library hierarchy') WHERE NEW.ParentId IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM LibraryItems p WHERE p.Id = NEW.ParentId AND (
                    (p.ItemType = 1 AND NEW.ItemType = 2) OR
                    (p.ItemType = 3 AND NEW.ItemType = 4) OR
                    (p.ItemType = 4 AND NEW.ItemType = 5)));
            SELECT RAISE(ABORT, 'Item type conflicts with existing children') WHERE EXISTS (
                SELECT 1 FROM LibraryItems c WHERE c.ParentId = NEW.Id AND NOT (
                    (NEW.ItemType = 1 AND c.ItemType = 2) OR
                    (NEW.ItemType = 3 AND c.ItemType = 4) OR
                    (NEW.ItemType = 4 AND c.ItemType = 5)));
        END;
        """),
        (2, "Stable scanner identities and episode ranges", """
        ALTER TABLE LibraryItems ADD COLUMN EpisodeEndNumber INTEGER
            CHECK(EpisodeEndNumber IS NULL OR (EpisodeNumber IS NOT NULL AND EpisodeEndNumber >= EpisodeNumber));
        ALTER TABLE MediaFiles ADD COLUMN LibraryRootId INTEGER REFERENCES LibraryRoots(Id) ON DELETE RESTRICT;
        CREATE INDEX IX_MediaFiles_Root ON MediaFiles(LibraryRootId);
        CREATE TABLE ScanSources (
            SourceKey TEXT PRIMARY KEY NOT NULL,
            LibraryRootId INTEGER NOT NULL REFERENCES LibraryRoots(Id) ON DELETE RESTRICT,
            LibraryItemId INTEGER NOT NULL UNIQUE REFERENCES LibraryItems(Id) ON DELETE RESTRICT
        );
        """),
        (3, "Manual organization scanner aliases", """
        ALTER TABLE ScanSources RENAME TO OldScanSources;
        CREATE TABLE ScanSources (
            SourceKey TEXT PRIMARY KEY NOT NULL,
            LibraryRootId INTEGER NOT NULL REFERENCES LibraryRoots(Id) ON DELETE RESTRICT,
            LibraryItemId INTEGER REFERENCES LibraryItems(Id) ON DELETE RESTRICT
        );
        INSERT INTO ScanSources SELECT SourceKey, LibraryRootId, LibraryItemId FROM OldScanSources;
        DROP TABLE OldScanSources;
        CREATE INDEX IX_ScanSources_Item ON ScanSources(LibraryItemId);
        """),
        (4, "Folder artwork and explicit poster choices", """
        ALTER TABLE LibraryItems ADD COLUMN IsPosterOverride INTEGER NOT NULL DEFAULT 0
            CHECK(IsPosterOverride IN (0, 1));
        UPDATE LibraryItems SET IsPosterOverride=1 WHERE IsManualOverride=1 AND PosterPath IS NOT NULL;
        """)
    ];
}
