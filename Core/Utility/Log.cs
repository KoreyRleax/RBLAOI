using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace RBLAOI.Core.Utility
{
    /// <summary>
    /// 日志服务类
    /// </summary>
    public class Log
    {
        private static readonly object _lockObj = new object();
        private static string _logDirectory;
        private static bool _isInitialized = false;
        private static string _sessionId;

        /// <summary>
        /// 日志级别
        /// </summary>
        public enum LogLevel
        {
            Info,       // 信息
            Warning,    // 警告
            Error,      // 错误
            Debug       // 调试
        }

        /// <summary>
        /// 初始化日志服务
        /// </summary>
        /// <param name="logDirectory">日志目录，默认程序目录下的 Logs 文件夹</param>
        public static void Initialize(string logDirectory = null)
        {
            if (string.IsNullOrEmpty(logDirectory))
            {
                logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            }

            _logDirectory = logDirectory;

            // 每次启动生成唯一会话ID，确保每次重启创建新日志文件
            _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }

            _isInitialized = true;
        }

        /// <summary>
        /// 写入日志
        /// </summary>
        public static void Write(LogLevel level, string message, string source = null)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            string logFile = GetLogFileName();
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string levelText = GetLevelText(level);
            string sourceText = string.IsNullOrEmpty(source) ? "" : $"[{source}] ";
            string logEntry = $"{time} [{levelText}] {sourceText}{message}";

            // 异步写入，避免阻塞
            Task.Run(() =>
            {
                lock (_lockObj)
                {
                    try
                    {
                        File.AppendAllText(logFile, logEntry + Environment.NewLine, Encoding.UTF8);

                        // 同时输出到调试窗口
                        System.Diagnostics.Debug.WriteLine(logEntry);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"日志写入失败: {ex.Message}");
                    }
                }
            });
        }

        /// <summary>
        /// 写入信息日志
        /// </summary>
        public static void Info(string message, string source = null)
        {
            Write(LogLevel.Info, message, source);
        }

        /// <summary>
        /// 写入警告日志
        /// </summary>
        public static void Warning(string message, string source = null)
        {
            Write(LogLevel.Warning, message, source);
        }

        /// <summary>
        /// 写入错误日志
        /// </summary>
        public static void Error(string message, string source = null)
        {
            Write(LogLevel.Error, message, source);
        }

        /// <summary>
        /// 写入调试日志
        /// </summary>
        public static void Debug(string message, string source = null)
        {
#if DEBUG
            Write(LogLevel.Debug, message, source);
#endif
        }

        /// <summary>
        /// 写入异常日志
        /// </summary>
        public static void Exception(Exception ex, string source = null)
        {
            string message = $"异常: {ex.Message}\r\n堆栈: {ex.StackTrace}";
            if (ex.InnerException != null)
            {
                message += $"\r\n内部异常: {ex.InnerException.Message}";
            }
            Write(LogLevel.Error, message, source);
        }

        /// <summary>
        /// 获取日志文件名（每次程序启动创建一个新文件）
        /// </summary>
        private static string GetLogFileName()
        {
            return Path.Combine(_logDirectory, $"log_{_sessionId}.txt");
        }

        /// <summary>
        /// 获取日志级别文本
        /// </summary>
        private static string GetLevelText(LogLevel level)
        {
            return level switch
            {
                LogLevel.Info => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Debug => "DEBUG",
                _ => "INFO"
            };
        }

        /// <summary>
        /// 获取所有日志文件
        /// </summary>
        public static string[] GetLogFiles()
        {
            if (!Directory.Exists(_logDirectory))
                return new string[0];

            return Directory.GetFiles(_logDirectory, "log_*.txt");
        }

        /// <summary>
        /// 读取日志文件内容
        /// </summary>
        public static string ReadLogFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    return File.ReadAllText(filePath, Encoding.UTF8);
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>
        /// 清理旧日志（保留最近N天）
        /// </summary>
        public static void CleanOldLogs(int keepDays = 30)
        {
            try
            {
                var files = GetLogFiles();
                DateTime cutoff = DateTime.Now.AddDays(-keepDays);

                foreach (var file in files)
                {
                    if (File.GetCreationTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch { }
        }
    }
}