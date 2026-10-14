using Godot;

namespace Game;

/// <summary>
/// Autoload that turns any unhandled C# exception into a log marker and exit code 1 (ADR 0011).
/// </summary>
/// <remarks>
/// Godot catches exceptions thrown inside engine callbacks (<c>_Ready</c>, <c>_Process</c>, signals),
/// logs them and keeps running with exit code 0. <see cref="ExceptionLogger"/> sees those reports.
/// Exceptions on other threads reach <see cref="AppDomain.UnhandledException"/>; without this guard
/// the runtime aborts the process (SIGABRT, exit code 134 on macOS).
/// </remarks>
public partial class CrashGuard : Node
{
    /// <summary>Marker the smoke script looks for in the log.</summary>
    public const string Marker = "UNHANDLED_EXCEPTION";

    private const int FailureExitCode = 1;

    private static int s_failed;
    private static int s_mainThreadId;

    private ExceptionLogger? _logger;

    /// <summary>True once any unhandled exception was reported in this process.</summary>
    public static bool HasFailed => Volatile.Read(ref s_failed) != 0;

    public override void _EnterTree()
    {
        s_mainThreadId = System.Environment.CurrentManagedThreadId;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _logger = new ExceptionLogger();
        OS.AddLogger(_logger);
    }

    public override void _ExitTree()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        if (_logger is not null)
        {
            OS.RemoveLogger(_logger);
            _logger = null;
        }
    }

    /// <summary>
    /// Records a failure and asks the main thread to quit with exit code 1. Safe to call from any thread.
    /// </summary>
    internal static void ReportFailure(string details)
    {
        if (Interlocked.Exchange(ref s_failed, 1) != 0)
        {
            return;
        }

        // Logger callbacks may run on any thread and inside Godot's error printing, so do the work deferred.
        Callable.From(() =>
        {
            GD.PrintErr($"{Marker}: {details}");
            if (Engine.GetMainLoop() is SceneTree tree)
            {
                tree.Quit(FailureExitCode);
            }
        }).CallDeferred();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        ReportFailure(e.ExceptionObject.ToString() ?? "unknown exception");

        // The runtime aborts the process (SIGABRT) once this handler returns. On a worker thread, park the
        // thread instead so the main loop can run the deferred quit and exit cleanly with code 1.
        // Environment.Exit is not an option here: on Godot 4.7.2 it deadlocks in engine shutdown when
        // called off the main thread. On the main thread there is nothing to wait for; the abort still
        // gives a non-zero exit code and the marker is already in the log.
        if (System.Environment.CurrentManagedThreadId != s_mainThreadId)
        {
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        ReportFailure(e.Exception.ToString());
    }
}
