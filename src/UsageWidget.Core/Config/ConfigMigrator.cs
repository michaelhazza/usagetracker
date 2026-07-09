using Newtonsoft.Json.Linq;

namespace UsageWidget.Core.Config;

/// <summary>
/// Forward-migrates a raw config document to <see cref="AdapterConfig.CurrentSchemaVersion"/> (§4).
/// Operates on the raw JSON so even hand-edited legacy files migrate cleanly. Pure/string-in,
/// string-out — the file backup is handled by <see cref="ConfigStore"/>.
/// </summary>
public static class ConfigMigrator
{
    /// <summary>Reads the declared version; a missing/blank version is treated as legacy v0.</summary>
    public static int ReadVersion(JObject root) =>
        root.TryGetValue("schemaVersion", StringComparison.OrdinalIgnoreCase, out var v) && v.Type == JTokenType.Integer
            ? v.Value<int>()
            : 0;

    /// <summary>Apply each step in order until the document is current.</summary>
    public static JObject Migrate(JObject root)
    {
        var version = ReadVersion(root);

        if (version < 1)
        {
            root = MigrateV0ToV1(root);
            version = 1;
        }

        if (version < 2)
        {
            root = MigrateV1ToV2(root);
            version = 2;
        }

        root["schemaVersion"] = version;
        return root;
    }

    /// <summary>
    /// v0 → v1: the legacy shape stored templates under a flat <c>"templates"</c> object; v1 renames
    /// it to <c>"templatesBySource"</c> and stamps the schema version.
    /// </summary>
    private static JObject MigrateV0ToV1(JObject root)
    {
        if (root["templatesBySource"] is null &&
            root.TryGetValue("templates", StringComparison.OrdinalIgnoreCase, out var legacy))
        {
            root["templatesBySource"] = legacy;
            root.Remove("templates");
        }

        return root;
    }

    /// <summary>
    /// v1 → v2: v1 shipped with an aggressive 1-minute cadence (and 5-minute idle cadence) that
    /// polls edge-protected providers hard enough to draw rate limits and challenges (§11 says
    /// 3 min / 15 min). Only the exact OLD DEFAULTS are raised — a value the user hand-edited to
    /// anything else is respected.
    /// </summary>
    private static JObject MigrateV1ToV2(JObject root)
    {
        // Saved files use PascalCase keys; hand-edited ones may use camelCase — match either.
        if (root.TryGetValue("polling", StringComparison.OrdinalIgnoreCase, out var p) &&
            p is JObject polling)
        {
            RaiseOldDefault(polling, "defaultCadence", "00:01:00", "00:03:00");
            RaiseOldDefault(polling, "idleCadence", "00:05:00", "00:15:00");
        }

        return root;
    }

    private static void RaiseOldDefault(JObject polling, string key, string oldDefault, string newValue)
    {
        if (polling.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var value) &&
            value.Type == JTokenType.String &&
            (string?)value == oldDefault)
        {
            // JToken.Replace keeps the original property (and its casing) in place.
            value.Replace(newValue);
        }
    }
}
