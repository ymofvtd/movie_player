namespace MyCinema.Core.Models;

public sealed record MetadataOverride(long LibraryItemId, string Field, string Value,
    DateTimeOffset UpdatedUtc);
