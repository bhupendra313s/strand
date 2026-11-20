using System;
using System.Linq;
using System.Threading;
using System.Windows;
using ContinuityHub.Core;

namespace ContinuityHub;

internal static class Program
{
    public const string ShowPairingEventName = @"Local\ContinuityHub.ShowPairing";
    public const string SendFilesEventName = @"Local\ContinuityHub.SendFiles";

    /// <summary>`ContinuityHub.exe --send a.txt "My Folder"` queues paths here for the running instance (QuickDrop).</summary>
    public static string SendQueueDir => System.IO.Path.Combine(HubConfig.DefaultDataDir, "send-queue");

    [STAThread]
    private static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\ContinuityHub.SingleInstance", out var first);
        var sendIdx = Array.IndexOf(args, "--send");
        var sendPaths = sendIdx >= 0 ? args.Skip(sendIdx + 1).Select(System.IO.Path.GetFullPath).ToArray() : Array.Empty<string>();
        if (sendPaths.Length > 0)
        {
            System.IO.Directory.CreateDirectory(SendQueueDir);
            System.IO.File.WriteAllLines(System.IO.Path.Combine(SendQueueDir, Guid.NewGuid().ToString("N") + ".txt"), sendPaths);
        }
        if (!first)
        {
            // Already running (probably in the tray): hand over the paths, or ask it to show its window.
            var name = sendPaths.Length > 0 ? SendFilesEventName : ShowPairingEventName;
            if (EventWaitHandle.TryOpenExisting(name, out var signal)) { signal.Set(); signal.Dispose(); }
            return;
        }

        var dataDir = HubConfig.DefaultDataDir;
        System.IO.Directory.CreateDirectory(dataDir);
        Log.Init(dataDir);
        Log.Info($"Starting Strand {typeof(Program).Assembly.GetName().Version}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        var app = new App(minimized: args.Contains("--minimized"));
        app.Run();
    }
}

/// <summary>WPF application shell with the Windows 11 Fluent theme (Mica, light/dark, accent colour).</summary>
internal sealed class App : Application
{
    private readonly bool _minimized;
    private HubController? _hub;

    public App(bool minimized)
    {
        _minimized = minimized;
        ThemeMode = ThemeMode.System;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ContinuityHub;component/UI/Theme.xaml"),
        });
        DispatcherUnhandledException += (_, e) => { Log.Error("UI exception", e.Exception); e.Handled = true; };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _hub = new HubController(HubConfig.DefaultDataDir);
        _ = _hub.StartAsync(showWindow: !_minimized);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hub?.Dispose();
        base.OnExit(e);
    }
}
