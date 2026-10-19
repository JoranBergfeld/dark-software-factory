using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

/// <summary>Read-only original-work binding: a stop closes admission, not access to retained work.</summary>
internal sealed class FactoryInspectionAuthority
{
    private const string LifecycleKey = "dsf/factory/lifecycle";
    private readonly Dictionary<string, string> transition = new(StringComparer.Ordinal);
    internal bool AdmissionClosed { get; private set; }

    internal bool Include(string key, string? value = null)
    {
        if (key == LifecycleKey)
        {
            try
            {
                using var document = JsonDocument.Parse(value ?? throw new JsonException());
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("transition", out var lifecycle) || lifecycle.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String
                    || state.GetString() != "completed" || lifecycle.GetString() is not ("reconfiguration" or "reattachment"))
                    AdmissionClosed = true;
            }
            catch (JsonException) { AdmissionClosed = true; }
            return false;
        }
        if (FactoryReattachment.IsStopRecord(key)
            || key.StartsWith(FactoryReattachment.AccessPrefix, StringComparison.Ordinal)) return false;
        if (FactoryReconfigurationOperationRecords.IsRecord(key))
        {
            if (value is not null && !transition.TryAdd(key, value) && transition[key] != value) AdmissionClosed = true;
            return false;
        }
        if (!FactoryPause.IsStopRecord(key) && !FactoryOffboarding.IsStopRecord(key)) return true;
        AdmissionClosed = true;
        return false;
    }

    internal void CompleteTransition(DateTimeOffset now)
    {
        if (transition.Count == 0) return;
        try
        {
            if (!transition.TryGetValue(FactoryReconfigurationOperationRecords.RootKey, out var root)) throw new JsonException();
            var parsed = JsonSerializer.Deserialize<FactoryReconfigurationOperation>(root, FactorySourceValidation.RecordJson)
                ?? throw new JsonException();
            var operation = FactoryReconfigurationOperationRecords.Read(root, parsed.Identity, now);
            var prefix = FactoryReconfigurationOperationRecords.Prefix + operation.OperationId.ToString("N") + "/";
            if (operation.Phase != FactoryReconfigurationPhase.Completed
                || transition.Keys.Any(key => key != FactoryReconfigurationOperationRecords.RootKey
                    && !key.StartsWith(prefix, StringComparison.Ordinal)))
                AdmissionClosed = true;
        }
        catch (JsonException) { AdmissionClosed = true; }
    }
}
