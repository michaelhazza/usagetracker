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
}
