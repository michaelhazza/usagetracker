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
        try
        {
            var root = JObject.Parse(raw);
            var version = ConfigMigrator.ReadVersion(root);

            if (version < AdapterConfig.CurrentSchemaVersion)
            {
                // Back up the prior file before rewriting it.
                var backupPath = $"{_path}.{version}.bak";
                File.WriteAllText(backupPath, raw);

                root = ConfigMigrator.Migrate(root);
                WriteAtomically(root.ToString(Formatting.Indented));
            }

            // Deserialization is inside the guard too: a hand-edited TYPE error ("defaultCadence": 60,
            // a misspelled enum, accounts-as-object) throws here, not just at parse — and must
            // self-recover, not brick startup on every launch.
            return root.ToObject<AdapterConfig>(JsonSerializer.Create(SerializerSettings))
                   ?? new AdapterConfig();
        }
        catch (JsonException)
        {
            // A torn/corrupt/type-invalid file must not brick startup forever. Preserve the
            // evidence and start from defaults — secrets live elsewhere and survive.
            TryBackupCorrupt();
            return new AdapterConfig();
        }
    }

    /// <summary>Best-effort: the recovery path must never itself throw and re-brick startup.</summary>
    private void TryBackupCorrupt()
    {
        try { File.Copy(_path, _path + ".corrupt.bak", overwrite: true); }
        catch { /* the file may be locked; recovering to defaults still beats crashing */ }
    }

    public void Save(AdapterConfig config)
    {
        config.SchemaVersion = AdapterConfig.CurrentSchemaVersion;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        WriteAtomically(JsonConvert.SerializeObject(config, SerializerSettings));
    }

    /// <summary>
    /// Write-to-temp-then-rename so a crash mid-save can never leave the live config half-written
    /// and unreadable (the accounts list lives here — a truncated file looks like every account
    /// vanished). The rename is atomic on the same volume; readers see either the old or the new
    /// file, never a partial one.
    /// </summary>
    private void WriteAtomically(string json)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
