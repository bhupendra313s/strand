using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContinuityHub.Core;

public sealed class HubConfig
{
    public const int DefaultPort = 47800;

    [JsonPropertyName("hub_id")] public string HubId { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("hub_name")] public string HubName { get; set; } = Environment.MachineName;
    [JsonPropertyName("port")] public int Port { get; set; } = DefaultPort;
    [JsonPropertyName("cert_thumbprint")] public string? CertThumbprint { get; set; }
    [JsonPropertyName("paused")] public bool Paused { get; set; }
    [JsonPropertyName("notifications")] public bool Notifications { get; set; } = true;
    [JsonPropertyName("tray_hint_shown")] public bool TrayHintShown { get; set; }
    /// <summary>Tint the hub with the phone's Material You colours instead of the Windows accent.</summary>
    [JsonPropertyName("match_phone_colors")] public bool MatchPhoneColors { get; set; } = true;
    /// <summary>Last palette a phone sent (JSON), so the colours stay when the phone is offline.</summary>
    [JsonPropertyName("phone_palette")] public string? PhonePalette { get; set; }
    /// <summary>Per-feature on/off. Missing ids fall back to the feature's default.</summary>
    [JsonPropertyName("features")] public Dictionary<string, bool> Features { get; set; } = new();
    /// <summary>Feature settings as "featureId.key" → value.</summary>
    [JsonPropertyName("settings")] public Dictionary<string, string> Settings { get; set; } = new();

    [JsonIgnore] public string Path { get; private set; } = "";

    public static string DefaultDataDir =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ContinuityHub");

    public static HubConfig LoadOrCreate(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = System.IO.Path.Combine(dataDir, "config.json");
        HubConfig cfg;
        try { cfg = File.Exists(path) ? JsonSerializer.Deserialize<HubConfig>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception e) { Log.Warn($"config.json unreadable, recreating: {e.Message}"); cfg = new(); }
        cfg.Path = path;
        cfg.Save();
        return cfg;
    }

    public void Save()
    {
        if (Path.Length == 0) return;
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Path, overwrite: true);
    }
}
