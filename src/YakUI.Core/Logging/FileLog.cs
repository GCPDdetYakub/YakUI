using System.Globalization;
using System.Text;

namespace YakUI.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public interface IAppLog
{
    LogLevel MinimumLevel { get; }

    void Write(LogLevel level, string message, Exception? exception = null);
}

public static class AppLogExtensions
{
    public static void Debug(this IAppLog log, string message) => log.Write(LogLevel.Debug, message);

    public static void Info(this IAppLog log, string message) => log.Write(LogLevel.Info, message);

    public static void Warning(this IAppLog log, string message, Exception? e = null) => log.Write(LogLevel.Warning, message, e);

    public static void Error(this IAppLog log, string message, Exception? e = null) => log.Write(LogLevel.Error, message, e);
}

/// <summary>
/// Текстовый лог с ротацией (FR-90, NFR-08): <c>&lt;данные&gt;\logs\yakui.log</c>; при превышении размера файл
/// переименовывается в <c>yakui.1.log</c> … <c>yakui.N.log</c>, старейший удаляется. Уровень по умолчанию — Info.
/// Сбои записи лога не прерывают работу приложения.
/// </summary>
public sealed class FileLog : IAppLog
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _keepFiles;
    private readonly TimeProvider _time;

    public FileLog(string logsDirectory, LogLevel minimumLevel = LogLevel.Info, long maxBytes = 5 * 1024 * 1024, int keepFiles = 5, TimeProvider? time = null)
    {
        System.IO.Directory.CreateDirectory(logsDirectory);
        LogsDirectory = logsDirectory;
        _path = Path.Combine(logsDirectory, "yakui.log");
        MinimumLevel = minimumLevel;
        _maxBytes = maxBytes;
        _keepFiles = Math.Max(1, keepFiles);
        _time = time ?? TimeProvider.System;
    }

    public string LogsDirectory { get; }

    public string CurrentFile => _path;

    public LogLevel MinimumLevel { get; }

    public static LogLevel ParseLevel(string? value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : LogLevel.Info;

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (level < MinimumLevel)
            return;

        var sb = new StringBuilder()
            .Append(_time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(level.ToString().ToUpperInvariant().PadRight(7))
            .Append(' ')
            .Append(message);
        if (exception is not null)
            sb.AppendLine().Append(exception);
        sb.AppendLine();

        lock (_lock)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_path, sb.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
                // лог не должен ломать приложение
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < _maxBytes)
            return;

        var oldest = Numbered(_keepFiles);
        if (File.Exists(oldest))
            File.Delete(oldest);

        for (var i = _keepFiles - 1; i >= 1; i--)
        {
            var from = Numbered(i);
            if (File.Exists(from))
                File.Move(from, Numbered(i + 1));
        }

        File.Move(_path, Numbered(1));
    }

    private string Numbered(int i) => Path.Combine(LogsDirectory, string.Create(CultureInfo.InvariantCulture, $"yakui.{i}.log"));
}
