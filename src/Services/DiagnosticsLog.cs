using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;

namespace MoogleMap.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Message, string? Exception)
{
    public string Format()
    {
        var line = $"{Time:HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant()[..3]}] {Message}";
        return Exception is null ? line : $"{line}\n    {Exception}";
    }
}

/// <summary>
/// Forwards every message to Dalamud's log and keeps the most recent ones in memory, so the
/// Diagnostics page can show them - including Debug lines Dalamud hides by default - and
/// users can copy them straight into a bug report.
/// </summary>
public sealed class DiagnosticsLog
{
    public const int Capacity = 1000;

    private static readonly Regex Placeholder = new(@"\{@?([A-Za-z0-9_]+)(:[^}]*)?\}", RegexOptions.Compiled);

    private readonly Func<IPluginLog> dalamudLog;
    private readonly Queue<LogEntry> entries = new(Capacity);
    private readonly object gate = new();

    /// <summary>Bumped on every write or clear, so the UI can tell when its cached view is stale.</summary>
    public int Revision { get; private set; }

    public DiagnosticsLog(Func<IPluginLog> dalamudLog)
    {
        this.dalamudLog = dalamudLog;
    }

    public void Debug(string template, params object?[] args) => Write(LogLevel.Debug, null, template, args);
    public void Debug(Exception ex, string template, params object?[] args) => Write(LogLevel.Debug, ex, template, args);
    public void Info(string template, params object?[] args) => Write(LogLevel.Info, null, template, args);
    public void Info(Exception ex, string template, params object?[] args) => Write(LogLevel.Info, ex, template, args);
    public void Warning(string template, params object?[] args) => Write(LogLevel.Warning, null, template, args);
    public void Warning(Exception ex, string template, params object?[] args) => Write(LogLevel.Warning, ex, template, args);
    public void Error(string template, params object?[] args) => Write(LogLevel.Error, null, template, args);
    public void Error(Exception ex, string template, params object?[] args) => Write(LogLevel.Error, ex, template, args);

    public List<LogEntry> Snapshot()
    {
        lock (gate)
        {
            return [.. entries];
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            Revision++;
        }
    }

    private void Write(LogLevel level, Exception? ex, string template, object?[] args)
    {
        var log = dalamudLog();
        switch (level)
        {
            case LogLevel.Debug: log.Debug(ex, template, args!); break;
            case LogLevel.Info: log.Info(ex, template, args!); break;
            case LogLevel.Warning: log.Warning(ex, template, args!); break;
            case LogLevel.Error: log.Error(ex, template, args!); break;
        }

        var entry = new LogEntry(DateTime.Now, level, Render(template, args), ex is null ? null : Describe(ex));
        lock (gate)
        {
            if (entries.Count >= Capacity)
                entries.Dequeue();
            entries.Enqueue(entry);
            Revision++;
        }
    }

    /// <summary>Fills Serilog-style {Named} placeholders positionally, the way Serilog does.</summary>
    private static string Render(string template, object?[] args)
    {
        if (args.Length == 0)
            return template;

        var index = 0;
        return Placeholder.Replace(template, m =>
        {
            if (index >= args.Length)
                return m.Value;

            var arg = args[index++];
            var format = m.Groups[2].Success ? m.Groups[2].Value[1..] : null;
            return arg is IFormattable f && format is not null
                ? f.ToString(format, CultureInfo.InvariantCulture)
                : Convert.ToString(arg, CultureInfo.InvariantCulture) ?? "null";
        });
    }

    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append("\n    --> ");
            sb.Append(e.GetType().Name).Append(": ").Append(e.Message);

            // A few frames are enough to locate the fault without flooding the report.
            var frames = e.StackTrace?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
            for (var i = 0; i < Math.Min(frames.Length, 3); i++)
                sb.Append("\n      ").Append(frames[i].Trim());
        }
        return sb.ToString();
    }
}
