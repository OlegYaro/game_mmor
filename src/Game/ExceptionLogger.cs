using Godot;

namespace Game;

/// <summary>
/// Godot logger that notices C# exceptions Godot caught in engine callbacks and reports them to
/// <see cref="CrashGuard"/>. Other engine errors are left to the smoke script, which also scans the log.
/// </summary>
public partial class ExceptionLogger : Logger
{
    // GodotSharp reports exceptions caught in callbacks via ExceptionUtils.LogException (verified on
    // Godot 4.7.2). Exceptions on other threads go through LogUnhandledException, which is skipped here:
    // CrashGuard gets those from AppDomain.UnhandledException with the full exception text.
    private const string GodotExceptionReporter = "Godot.NativeInterop.ExceptionUtils.LogException(";

    public override void _LogError(
        string function,
        string file,
        int line,
        string code,
        string rationale,
        bool editorNotify,
        int errorType,
        Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        if (function.Contains(GodotExceptionReporter, StringComparison.Ordinal))
        {
            CrashGuard.ReportFailure(code);
        }
    }
}
