using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContinuityHub.Core;

public sealed class PairedDevice
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("token_sha256")] public string TokenSha256 { get; set; } = "";
    [JsonPropertyName("paired_at")] public DateTimeOffset PairedAt { get; set; }
    [JsonPropertyName("last_seen")] public DateTimeOffset? LastSeen { get; set; }
}

/// <summary>Paired devices persisted to devices.json. Only token hashes are stored.</summary>
public sealed class DeviceRegistry
{
    private readonly string? _path;
    private readonly object _gate = new();
    private List<PairedDevice> _devices = new();

    public DeviceRegistry(string? path)
    {
        _path = path;
        if (path != null && File.Exists(path))
        {
            try { _devices = JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(path)) ?? new(); }
            catch (Exception e) { Log.Warn($"devices.json unreadable: {e.Message}"); }
        }
    }

    public IReadOnlyList<PairedDevice> All { get { lock (_gate) return _devices.ToList(); } }

    /// <summary>Registers (or re-registers) a device and returns its new bearer token.</summary>
    public string Pair(string deviceId, string name)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            _devices.RemoveAll(d => d.DeviceId == deviceId);
            _devices.Add(new PairedDevice
            {
                DeviceId = deviceId, Name = name, TokenSha256 = HashToken(token), PairedAt = DateTimeOffset.UtcNow,
            });
            Save();
        }
        return token;
    }

    public bool Verify(string deviceId, string token)
    {
        lock (_gate)
        {
            var d = _devices.FirstOrDefault(x => x.DeviceId == deviceId);
            if (d == null) return false;
            var ok = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(d.TokenSha256), Encoding.ASCII.GetBytes(HashToken(token)));
            if (ok) { d.LastSeen = DateTimeOffset.UtcNow; Save(); }
            return ok;
        }
    }

    public void Forget(string deviceId)
    {
        lock (_gate) { _devices.RemoveAll(d => d.DeviceId == deviceId); Save(); }
    }

    private void Save()
    {
        if (_path == null) return;
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_devices, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    private static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
