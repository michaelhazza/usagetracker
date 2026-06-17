using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace UsageWidget.Core.Config;

/// <summary>
/// Loads/saves <see cref="AdapterConfig"/> as plaintext JSON. On load, if the file's
/// <c>schemaVersion</c> is older than current, it backs up the prior file as
/// <c>adapter-config.&lt;version&gt;.bak</c> and writes the migrated document — never silently
/// breaking a hand-edited config (§4).
/// </summary>
public sealed class ConfigStore
{
    private readonly string _path;

    public static JsonSerializerSettings SerializerSettings { get; } = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new StringEnumConverter() },
    };

    public ConfigStore(string path) => _path = path;

    /// <summary>The default location: <c>%APPDATA%\UsageWidget\adapter-config.json</c>.</summary>
    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "UsageWidget", "adapter-config.json");
    }

    public AdapterConfig Load()
    {
        if (!File.Exists(_path))
        {
            return new AdapterConfig();
        }

        var raw = File.ReadAllText(_path);
        var root = JObject.Parse(raw);
        var version = ConfigMigrator.ReadVersion(root);

        if (version < AdapterConfig.CurrentSchemaVersion)
        {
            // Back up the prior file before rewriting it.
            var backupPath = $"{_path}.{version}.bak";
            File.WriteAllText(backupPath, raw);

            root = ConfigMigrator.Migrate(root);
            File.WriteAllText(_path, root.ToString(Formatting.Indented));
        }

        return root.ToObject<AdapterConfig>(JsonSerializer.Create(SerializerSettings))
               ?? new AdapterConfig();
    }

    public void Save(AdapterConfig config)
    {
        config.SchemaVersion = AdapterConfig.CurrentSchemaVersion;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(config, SerializerSettings);
        File.WriteAllText(_path, json);
    }
}
