using System;
using System.IO;

namespace DemosceneDownloader.Services;

public class Logger(string filePath)
{
    private static readonly object _lock = new();

    public enum LogLevel { Info, Warning, Error, Debug }

    public void Log(LogLevel level, string message)
    {
        string msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_lock) File.AppendAllText(filePath, msg + Environment.NewLine);
    }

    public void Info(string m)    => Log(LogLevel.Info,    m);
    public void Warning(string m) => Log(LogLevel.Warning, m);
    public void Error(string m)   => Log(LogLevel.Error,   m);
    public void Debug(string m)   => Log(LogLevel.Debug,   m);
}
