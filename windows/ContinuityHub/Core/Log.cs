using System;
using System.IO;

namespace ContinuityHub.Core;

/// <summary>Tiny file logger. Never logs clipboard contents, only sizes and ids.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string dataDir)
    {
        _path = Path.Combine(dataDir, "hub.log");
        try { if (File.Exists(_path) && new FileInfo(_path).Length > 2_000_000) File.Delete(_path); } catch { }
    }

    public static void Info(string m) => Write("INFO", m);
    public static void Warn(string m) => Write("WARN", m);
    public static void Error(string m, Exception? e = null) => Write("ERROR", e == null ? m : $"{m}: {e}");

    private static void Write(string level, string m)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {m}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_path == null) return;
        lock (Gate) { try { File.AppendAllText(_path, line + Environment.NewLine); } catch { } }
    }
}
