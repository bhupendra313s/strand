using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ContinuityHub.Core;

/// <summary>
/// Prevents echo loops and stale/duplicate deliveries. Thread-safe.
/// Rules are documented in PROTOCOL.md §5.
/// </summary>
public sealed class LoopGuard
{
    private const int MaxRememberedIds = 512;

    private readonly string _selfId;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _newestTsByOrigin = new();
    private readonly HashSet<string> _seenIds = new();
    private readonly Queue<string> _seenOrder = new();
    private string? _lastAppliedRemoteHash;
    private string? _lastBroadcastHash;

    public LoopGuard(string selfId) => _selfId = selfId;

    public string SelfId => _selfId;

    /// <summary>True if an incoming payload should be applied.</summary>
    public bool AcceptIncoming(string id, string originDeviceId, long timestamp)
    {
        lock (_gate)
        {
            if (originDeviceId == _selfId) return false;
            if (_seenIds.Contains(id)) return false;
            if (_newestTsByOrigin.TryGetValue(originDeviceId, out var newest) && timestamp < newest) return false;

            _newestTsByOrigin[originDeviceId] = Math.Max(newest, timestamp);
            _seenIds.Add(id);
            _seenOrder.Enqueue(id);
            while (_seenOrder.Count > MaxRememberedIds) _seenIds.Remove(_seenOrder.Dequeue());
            return true;
        }
    }

    /// <summary>Call just before writing remote content into the local clipboard.</summary>
    public void MarkAppliedRemote(string contentHash)
    {
        lock (_gate)
        {
            _lastAppliedRemoteHash = contentHash;
            _lastBroadcastHash = null;
        }
    }

    /// <summary>
    /// Call when the local OS reports a clipboard change. Returns false for echoes of
    /// remote content we just applied, and for repeats of what we already broadcast.
    /// </summary>
    public bool ShouldBroadcastLocal(string contentHash)
    {
        lock (_gate)
        {
            if (contentHash == _lastAppliedRemoteHash || contentHash == _lastBroadcastHash) return false;
            _lastAppliedRemoteHash = null; // a genuine local copy happened
            _lastBroadcastHash = contentHash;
            return true;
        }
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
