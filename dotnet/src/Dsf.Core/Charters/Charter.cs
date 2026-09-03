using System.Text.Json.Serialization;

namespace Dsf.Core.Charters;

/// <summary>Sync state of a product's charter relative to its source file.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CharterStatus>))]
public enum CharterStatus
{
    [JsonStringEnumMemberName("OK")]
    Ok,

    [JsonStringEnumMemberName("STALE")]
    Stale,

    [JsonStringEnumMemberName("MISSING")]
    Missing,

    [JsonStringEnumMemberName("INVALID")]
    Invalid,
}

/// <summary>A product's charter: vision, users, goals, non-goals, metrics.</summary>
public sealed record Charter(
    string Product,
    string Vision,
    string TargetUsers,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> NonGoals,
    IReadOnlyList<string> SuccessMetrics,
    string Constraints,
    IReadOnlyDictionary<string, string> Glossary)
{
    public int SchemaVersion { get; init; } = 1;

    public string? SourceSha { get; init; }

    public string? SourceRef { get; init; }
}

/// <summary>A charter plus its persisted sync metadata (one record per product).</summary>
public sealed record StoredCharter(
    string Product,
    string Repository,
    Charter? Charter,
    CharterStatus Status,
    string? SourceSha,
    string? SourceRef,
    string? Content,
    DateTimeOffset? LastSyncedAt,
    string? LastError);
