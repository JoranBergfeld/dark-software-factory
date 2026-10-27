using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dsf.Core.Onboarding;

public static class FactorySourceEvidenceIntegrity
{
    public static string InventoryDigest(IEnumerable<(string Key, string Revision, string Value)> records)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (key, revision, value) in records.OrderBy(r => r.Key, StringComparer.Ordinal))
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new[] { key, revision, value }));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool Sensitive(string text) => Regex.IsMatch(text,
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----|(?:gh[pousr]_|github_pat_)[A-Za-z0-9_]{20,}|(?:AKIA|ASIA)[A-Z0-9]{16}|(?i:password|passwd|client[_-]?secret|api[_-]?key|access[_-]?token|accountkey|sharedaccesskey)\s*[""']?\s*[:=]\s*[""']?[^\s""']{8,}",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
}
