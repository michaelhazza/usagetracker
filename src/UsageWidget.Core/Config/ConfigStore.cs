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
        JObject root;
        try
        {
            root = JObject.Parse(raw);
        }
        catch (JsonException)
        {
            // A torn/corrupt file (interrupted write, disk hiccup) must not brick startup forever.
            // Preserve the evidence and start from defaults — secrets live elsewhere and survive.
            File.Copy(_path, _path + ".corrupt.bak", overwrite: true);
            return new AdapterConfig();
        }

        var version = ConfigMigrator.ReadVersion(root);

        if (version < AdapterConfig.CurrentSchemaVersion)
        {
            // Back up the prior file before rewriting it.
            var backupPath = $"{_path}.{version}.bak";
            File.WriteAllText(backupPath, raw);

            root = ConfigMigrator.Migrate(root);
            WriteAtomically(root.ToString(Formatting.Indented));
        }

        return root.ToObject<AdapterConfig>(JsonSerializer.Create(SerializerSettings))
               ?? new AdapterConfig();
    }

    public void Save(AdapterConfig config)
    {
        config.SchemaVersion = AdapterConfig.CurrentSchemaVersion;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        WriteAtomically(JsonConvert.SerializeObject(config, SerializerSettings));
    }

    /// <summary>
    /// Write-to-temp-then-rename so a crash or power loss mid-save can never leave a half-written
    /// config (the accounts list lives here — losing it looks like every account vanished).
    /// </summary>
    private void WriteAtomically(string json)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
