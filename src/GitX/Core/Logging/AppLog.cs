using System.Diagnostics;

namespace GitX.Core.Logging;

/// <summary>
/// 全局日志入口。现阶段实现用 <see cref="Debug.Print(string)"/>，
/// 输出到调试器输出窗口（VS 附加调试时可见，或用 Sysinternals DebugView 查看）。
/// <para>
/// 后续替换为正式日志组件（Serilog/NLog 等）时，只需改本文件内部实现，
/// 调用方 <c>AppLog.Info(...)</c> 无需变动。
/// </para>
/// <para>
/// API 与 <c>Microsoft.Extensions.Logging.ILogger</c> 用法对齐，方便迁移：
/// <c>_logger.LogWarning(ex, "msg {Arg}", arg)</c> → <c>AppLog.Warn(ex, "msg {Arg}", arg)</c>
/// </para>
/// </summary>
public static class AppLog
{
    private const string Prefix = "[GitX] ";

    /// <summary>调试级别（详细诊断信息）</summary>
    [Conditional("DEBUG")]
    public static void Debug(string message, params object[] args)
        => Write("DBG", message, args, null);

    /// <summary>信息级别（正常流程记录）</summary>
    public static void Info(string message, params object[] args)
        => Write("INF", message, args, null);

    /// <summary>信息级别（带异常）</summary>
    public static void Info(Exception? ex, string message, params object[] args)
        => Write("INF", message, args, ex);

    /// <summary>警告级别</summary>
    public static void Warn(string message, params object[] args)
        => Write("WRN", message, args, null);

    /// <summary>警告级别（带异常）</summary>
    public static void Warn(Exception? ex, string message, params object[] args)
        => Write("WRN", message, args, ex);

    /// <summary>错误级别</summary>
    public static void Error(string message, params object[] args)
        => Write("ERR", message, args, null);

    /// <summary>错误级别（带异常）</summary>
    public static void Error(Exception? ex, string message, params object[] args)
        => Write("ERR", message, args, ex);

    private static void Write(string level, string message, object[] args, Exception? ex)
    {
        var text = args is { Length: > 0 } ? SafeFormat(message, args) : message;
        var line = $"{Prefix}{level} {text}";
        if (ex != null)
        {
            line += Environment.NewLine + "  → " + ex;
        }

        // Debug.Print 在 DEBUG 配置下输出到调试器；Release 下用 Trace 兜底（DebugView 可见）
#if DEBUG
        System.Diagnostics.Debug.Print(line);
#else
        Trace.WriteLine(line);
#endif
    }

    private static string SafeFormat(string format, object[] args)
    {
        try { return string.Format(format, args); }
        catch { return format; }
    }
}
