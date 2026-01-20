using System;
using System.IO;

namespace DNFSyncBox.Agent
{
    /// <summary>
    /// 代理专用日志器：输出到 AppData 目录，便于确认实际输入路径。
    /// </summary>
    public sealed class AgentLogger
    {
        private readonly object _lock = new object();
        private readonly string _logPath;

        public AgentLogger(int pid)
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DNFSyncBox",
                "logs");
            Directory.CreateDirectory(logDirectory);
            _logPath = Path.Combine(logDirectory, $"agent-{pid}.log");
        }

        public void Info(string message)
        {
            WriteLine("INFO", message);
        }

        public void Error(string message, Exception? exception = null)
        {
            var detail = exception == null ? message : $"{message} | {exception}";
            WriteLine("ERROR", detail);
        }

        private void WriteLine(string level, string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            lock (_lock)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
    }
}
