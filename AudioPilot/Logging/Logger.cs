using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Logging
{
    public interface ILogger : IDisposable
    {
        LogLevel MinimumLevel { get; set; }
        bool IsEnabled(LogLevel level);
        void Log(LogLevel level, string category, string message, string? methodName = null, Exception? exception = null);
        void Log(LogLevel level, string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null);
        void Trace(string category, string message, string? methodName = null);
        void Trace(string category, Func<string> messageFactory, string? methodName = null);
        void Debug(string category, string message, string? methodName = null);
        void Debug(string category, Func<string> messageFactory, string? methodName = null);
        void Info(string category, string message, string? methodName = null);
        void Info(string category, Func<string> messageFactory, string? methodName = null);
        void Warning(string category, string message, string? methodName = null, Exception? exception = null);
        void Warning(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null);
        void Error(string category, string message, string? methodName = null, Exception? exception = null);
        void Error(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null);
        void Fatal(string category, string message, string? methodName = null, Exception? exception = null);
        void Fatal(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null);
    }

    public partial class Logger : ILogger
    {
        internal enum LoggerWriteMode
        {
            FileBacked,
            InMemory,
        }

        private const int MaxQueueSize = AppConstants.Logging.MaxQueueSize;
        private const int MaxBatchSize = AppConstants.Logging.MaxBatchSize;
        private const long MaxLogFileBytes = AppConstants.Logging.MaxLogFileBytes;
        private const int MaxExceptionDetailsChars = AppConstants.Logging.MaxExceptionDetailsChars;
        private const int LogBackupRetentionCount = AppConstants.Files.LogBackupRetentionCount;
        private const int LogResetIntervalDays = AppConstants.Logging.LogResetIntervalDays;
        private const int LogBackupMaxAgeDays = AppConstants.Logging.LogBackupMaxAgeDays;

        private static readonly Lazy<Logger> _instance = new(
            () => new Logger(AppDomain.CurrentDomain.BaseDirectory),
            LazyThreadSafetyMode.ExecutionAndPublication);

        [GeneratedRegex(@" in (?:(?:[A-Za-z]:\\)|(?:\\\\))[^\r\n]+", RegexOptions.CultureInvariant)]
        private static partial Regex ExceptionPathRegex();

        [GeneratedRegex(@"(?:[A-Za-z]:\\[^\r\n]+|\\\\[^\r\n]+)", RegexOptions.CultureInvariant)]
        private static partial Regex ExceptionMessagePathRegex();

        private readonly string _logFilePath;
        private readonly string _logBackupDirectory;
        private readonly string _logFileName;
        private readonly LoggerWriteMode _writeMode;
        private readonly StringBuilder? _inMemoryLogBuffer;
        private readonly ConcurrentQueue<string> _logQueue = new();
        private readonly Lock _fileLock = new();
        private readonly Lock _producerStateLock = new();
        private readonly Task _logWriterTask;
        private readonly CancellationTokenSource _cts;
        private readonly AutoResetEvent _queueEvent = new(false);
        private int _shutdownDrainClaimed;
        private int _disposeStarted;
        private bool _disposed;
        private DateTime _currentDate;
        private int _queuedCount;
        private long _droppedMessageCount;
        private long _totalDroppedMessageCount;
        private int _rotationCount;
        private readonly int _maxQueueCapacity = MaxQueueSize;
        private TaskCompletionSource<bool> _activeProducerCompletionSource = CreateCompletedProducerCompletionSource();
        private int _activeProducerCount;

        public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        internal int QueueDepth => Volatile.Read(ref _queuedCount);
        internal long PendingDroppedCount => Interlocked.Read(ref _droppedMessageCount);
        internal long TotalDroppedCount => Interlocked.Read(ref _totalDroppedMessageCount);
        internal int RotationCount => Volatile.Read(ref _rotationCount);
        internal int MaxQueueCapacity => _maxQueueCapacity;
        internal static Func<int, Task> RetryDelayAsyncForTests { get; set; } = Task.Delay;
        internal void SetQueueDepthForTests(int value) => Interlocked.Exchange(ref _queuedCount, value);
        internal bool TryEnqueueForTests(string line)
        {
            if (Volatile.Read(ref _queuedCount) >= MaxQueueSize)
            {
                Interlocked.Increment(ref _droppedMessageCount);
                Interlocked.Increment(ref _totalDroppedMessageCount);
                return false;
            }

            _logQueue.Enqueue(line);
            Interlocked.Increment(ref _queuedCount);
            _queueEvent.Set();
            return true;
        }

        public static Logger Instance => _instance.Value;

        public Logger(string? logDirectory = null, string logFileName = AppConstants.Files.LogFileName)
            : this(logDirectory, logFileName, LoggerWriteMode.FileBacked)
        {
        }

        internal static Logger CreateInMemoryForTests(string logFileName = AppConstants.Files.LogFileName)
            => new(logDirectory: null, logFileName, LoggerWriteMode.InMemory);

        internal Logger(string? logDirectory, string logFileName, LoggerWriteMode writeMode)
        {
            _writeMode = writeMode;
            _logFileName = logFileName;
            _currentDate = DateTime.Now.Date;
            _cts = new CancellationTokenSource();

            if (_writeMode == LoggerWriteMode.InMemory)
            {
                _logFilePath = string.Empty;
                _logBackupDirectory = string.Empty;
                _inMemoryLogBuffer = new StringBuilder();
                _logWriterTask = Task.CompletedTask;
                return;
            }

            var logDir = logDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, _logFileName);
            _logBackupDirectory = Path.Combine(logDir, AppConstants.Files.BackupFolderName);
            _logWriterTask = Task.Run(() => ProcessLogQueue(_cts.Token));

            CheckAndResetForNewDay();
        }

        public bool IsEnabled(LogLevel level)
        {
            return level >= MinimumLevel && !_disposed;
        }

        private static TaskCompletionSource<bool> CreateCompletedProducerCompletionSource()
        {
            var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            completionSource.TrySetResult(true);
            return completionSource;
        }

        private void ProcessLogQueue(CancellationToken token)
        {
            var handles = new WaitHandle[] { _queueEvent, token.WaitHandle };

            while (!token.IsCancellationRequested)
            {
                int signaled = WaitHandle.WaitAny(handles);
                if (signaled == 0)
                {
                    var messages = DequeueBatch(MaxBatchSize);
                    WriteBatch(messages);
                }
                else if (signaled == 1)
                {
                    break;
                }
            }

            if (Interlocked.CompareExchange(ref _shutdownDrainClaimed, 1, 0) != 0)
            {
                return;
            }

            while (true)
            {
                var remaining = DequeueBatch(MaxBatchSize);
                if (remaining.Count == 0)
                {
                    break;
                }

                WriteBatch(remaining);
            }
        }

        private List<string> DequeueBatch(int maxItems)
        {
            var messages = new List<string>(Math.Min(maxItems, MaxBatchSize));
            while (_logQueue.TryDequeue(out string? message))
            {
                messages.Add(message);
                Interlocked.Decrement(ref _queuedCount);
                if (messages.Count >= maxItems)
                {
                    break;
                }
            }

            return messages;
        }

        private void WriteBatch(List<string> messages)
        {
            if (messages.Count == 0)
                return;

            if (_writeMode == LoggerWriteMode.InMemory)
            {
                lock (_fileLock)
                {
                    AppendDroppedMessageWarningIfNeeded(messages);
                    foreach (string message in messages)
                    {
                        _inMemoryLogBuffer!.AppendLine(message);
                    }
                }

                return;
            }

            lock (_fileLock)
            {
                try
                {
                    CheckAndResetForNewDay();
                    RotateIfNeeded(messages);

                    AppendDroppedMessageWarningIfNeeded(messages);

                    using var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, Encoding.UTF8);
                    foreach (var message in messages)
                    {
                        writer.WriteLine(message);
                    }
                    writer.FlushAsync().Wait();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to write to log file: {ex.GetType().Name}");
                    foreach (var msg in messages)
                    {
                        Console.WriteLine(msg);
                    }
                }
            }
        }

        private void AppendDroppedMessageWarningIfNeeded(List<string> messages)
        {
            long dropped = Interlocked.Exchange(ref _droppedMessageCount, 0);
            if (dropped <= 0)
            {
                return;
            }

            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            messages.Insert(0, $"[{ts}] [Warning] [Logger] Dropped {dropped} log messages because queue exceeded {MaxQueueSize} entries");
        }

        private void RotateIfNeeded(List<string> incomingMessages)
        {
            try
            {
                long incomingBytesEstimate = 0;
                for (int index = 0; index < incomingMessages.Count; index++)
                {
                    incomingBytesEstimate += (incomingMessages[index].Length + Environment.NewLine.Length) * sizeof(char);
                }

                long currentLength = File.Exists(_logFilePath) ? new FileInfo(_logFilePath).Length : 0;

                if ((currentLength + incomingBytesEstimate) < MaxLogFileBytes)
                {
                    return;
                }

                if (!File.Exists(_logFilePath))
                {
                    return;
                }

                bool rotated = ArchiveActiveLogFile();

                if (rotated)
                {
                    Interlocked.Increment(ref _rotationCount);
                    PruneOldLogBackups();
                }
            }
            catch (Exception ex)
            {
                if (System.Diagnostics.Debugger.IsAttached)
                {
                    System.Diagnostics.Debug.WriteLine($"Logger rotation failed: {ex.GetType().Name}");
                }
            }
        }

        private void CheckAndResetForNewDay()
        {
            var today = DateTime.Now.Date;
            if ((today - _currentDate).TotalDays >= LogResetIntervalDays)
            {
                _currentDate = today;
                try
                {
                    if (File.Exists(_logFilePath) && new FileInfo(_logFilePath).Length > 0)
                    {
                        _ = ArchiveActiveLogFile();
                    }

                    PruneOldLogBackups();
                }
                catch (Exception ex)
                {
                    if (System.Diagnostics.Debugger.IsAttached)
                    {
                        System.Diagnostics.Debug.WriteLine($"Logger daily reset failed: {ex.GetType().Name}");
                    }
                }
            }
        }

        public void Log(LogLevel level, string category, string message, string? methodName = null, Exception? exception = null)
        {
            if (level < MinimumLevel)
                return;

            if (!TryBeginProducer())
            {
                return;
            }

            try
            {
                LogCore(level, category, message, methodName, exception);
            }
            finally
            {
                EndProducer();
            }
        }

        public void Log(LogLevel level, string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null)
        {
            if (level < MinimumLevel)
            {
                return;
            }

            if (!TryBeginProducer())
            {
                return;
            }

            try
            {
                string message;
                try
                {
                    message = messageFactory();
                }
                catch (Exception ex)
                {
                    message = $"<log message factory failed: {ex.GetType().Name}>";
                }

                LogCore(level, category, message, methodName, exception);
            }
            finally
            {
                EndProducer();
            }
        }

        private void LogCore(LogLevel level, string category, string message, string? methodName, Exception? exception)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var method = string.IsNullOrEmpty(methodName) ? "" : $" [{methodName}]";
            var exceptionMessage = exception != null
                ? SanitizeExceptionMessage(exception.Message)
                : string.Empty;
            var exceptionDetails = exception != null
                ? $"\n  Exception: {exception.GetType().Name}: {exceptionMessage}\n{exception.StackTrace}"
                : "";

            exceptionDetails = SanitizeExceptionDetails(exceptionDetails);

            if (exceptionDetails.Length > MaxExceptionDetailsChars)
            {
                exceptionDetails = exceptionDetails[..MaxExceptionDetailsChars] + "\n  [truncated]";
            }

            var logLine = $"[{timestamp}] [{level}] [{category}] {message}{method}{exceptionDetails}";

            if (_writeMode == LoggerWriteMode.InMemory)
            {
                lock (_fileLock)
                {
                    _inMemoryLogBuffer!.AppendLine(logLine);
                }

                return;
            }

            if (Volatile.Read(ref _queuedCount) >= MaxQueueSize)
            {
                Interlocked.Increment(ref _droppedMessageCount);
                Interlocked.Increment(ref _totalDroppedMessageCount);
                return;
            }

            _logQueue.Enqueue(logLine);
            Interlocked.Increment(ref _queuedCount);
            _queueEvent.Set();
        }

        private bool TryBeginProducer()
        {
            lock (_producerStateLock)
            {
                if (_disposed)
                {
                    return false;
                }

                if (_activeProducerCount == 0)
                {
                    _activeProducerCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _activeProducerCount++;
                return true;
            }
        }

        private void EndProducer()
        {
            TaskCompletionSource<bool>? completionSource = null;

            lock (_producerStateLock)
            {
                if (_activeProducerCount == 0)
                {
                    return;
                }

                _activeProducerCount--;
                if (_activeProducerCount == 0)
                {
                    completionSource = _activeProducerCompletionSource;
                }
            }

            completionSource?.TrySetResult(true);
        }

        private bool WaitForActiveProducersBeforeShutdown()
        {
            Task activeProducersTask;
            lock (_producerStateLock)
            {
                activeProducersTask = _activeProducerCount == 0
                    ? Task.CompletedTask
                    : _activeProducerCompletionSource.Task;
            }

            try
            {
                return activeProducersTask.Wait(AppConstants.Logging.DisposeWaitMs);
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is OperationCanceledException or TaskCanceledException))
            {
                return true;
            }
        }

        internal static string SanitizeExceptionMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return message;
            }

            return ExceptionMessagePathRegex().Replace(message, "<path>");
        }

        public void ApplyLogLevel(Settings? settings)
        {
            LogPrivacy.ApplySettings(settings);

            if (settings == null || string.IsNullOrEmpty(settings.Miscellaneous.LogLevel))
                return;

            if (Enum.TryParse<LogLevel>(settings.Miscellaneous.LogLevel, true, out var level))
            {
                MinimumLevel = level;

                if (level == LogLevel.None)
                {
                    ClearQueuedMessages();
                    DeleteLogFilesIfPresent();
                    return;
                }

                Info("Logger", $"Log level set to: {level}");
            }
            else
            {
                Warning("Logger", $"Invalid log level '{settings.Miscellaneous.LogLevel}' in settings, defaulting to Info");
                MinimumLevel = LogLevel.Info;
            }
        }

        private void ClearQueuedMessages()
        {
            while (_logQueue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queuedCount);
            }
        }

        private void DeleteLogFilesIfPresent()
        {
            if (_writeMode == LoggerWriteMode.InMemory)
            {
                lock (_fileLock)
                {
                    _inMemoryLogBuffer!.Clear();
                }

                return;
            }

            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(_logFilePath))
                    {
                        File.Delete(_logFilePath);
                    }

                    foreach (string backupPath in EnumerateLogBackupCandidates())
                    {
                        if (File.Exists(backupPath))
                        {
                            File.Delete(backupPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    EmitInternalDiagnostic("Failed to delete log files after disabling logging", ex);
                }
            }
        }

        public void Trace(string category, string message, string? methodName = null)
            => Log(LogLevel.Trace, category, message, methodName);

        public void Trace(string category, Func<string> messageFactory, string? methodName = null)
            => Log(LogLevel.Trace, category, messageFactory, methodName);

        public void Debug(string category, string message, string? methodName = null)
            => Log(LogLevel.Debug, category, message, methodName);

        public void Debug(string category, Func<string> messageFactory, string? methodName = null)
            => Log(LogLevel.Debug, category, messageFactory, methodName);

        public void Info(string category, string message, string? methodName = null)
            => Log(LogLevel.Info, category, message, methodName);

        public void Info(string category, Func<string> messageFactory, string? methodName = null)
            => Log(LogLevel.Info, category, messageFactory, methodName);

        public void Warning(string category, string message, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Warning, category, message, methodName, exception);

        public void Warning(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Warning, category, messageFactory, methodName, exception);

        public void Error(string category, string message, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Error, category, message, methodName, exception);

        public void Error(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Error, category, messageFactory, methodName, exception);

        public void Fatal(string category, string message, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Fatal, category, message, methodName, exception);

        public void Fatal(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Fatal, category, messageFactory, methodName, exception);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _disposed = true;

            bool producersCompleted = WaitForActiveProducersBeforeShutdown();
            if (!producersCompleted)
            {
                EmitInternalDiagnostic($"Logger producer shutdown timed out after {AppConstants.Logging.DisposeWaitMs}ms; activeProducersPossible=true pendingQueueDepth={QueueDepth}");
            }

            _cts?.Cancel();
            _queueEvent?.Set();

            DrainQueuedMessagesForShutdown();

            bool shutdownCompleted = false;
            try
            {
                shutdownCompleted = _logWriterTask.Wait(AppConstants.Logging.DisposeWaitMs);
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is OperationCanceledException or TaskCanceledException))
            {
                shutdownCompleted = true;
            }
            catch (Exception ex)
            {
                EmitInternalDiagnostic("Logger shutdown wait failed", ex);
            }

            if (!shutdownCompleted)
            {
                EmitInternalDiagnostic($"Logger shutdown timed out after {AppConstants.Logging.DisposeWaitMs}ms; pendingQueueDepth={QueueDepth}");
            }
            else if (_logWriterTask.IsFaulted && _logWriterTask.Exception != null)
            {
                EmitInternalDiagnostic("Logger writer task faulted during shutdown", _logWriterTask.Exception);
            }

            _cts?.Dispose();
            _queueEvent?.Dispose();
            GC.SuppressFinalize(this);
        }

        internal string DisposeAndReadLogTextForTests()
        {
            Dispose();

            if (_writeMode == LoggerWriteMode.InMemory)
            {
                lock (_fileLock)
                {
                    return _inMemoryLogBuffer!.ToString();
                }
            }

            lock (_fileLock)
            {
                if (!File.Exists(_logFilePath))
                {
                    return string.Empty;
                }

                using var stream = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }

        private void DrainQueuedMessagesForShutdown()
        {
            if (Interlocked.CompareExchange(ref _shutdownDrainClaimed, 1, 0) != 0)
            {
                return;
            }

            while (true)
            {
                var messages = DequeueBatch(MaxBatchSize);
                if (messages.Count == 0)
                {
                    break;
                }

                WriteBatch(messages);
            }
        }

        private static void EmitInternalDiagnostic(string message, Exception? exception = null)
        {
            string payload = exception == null
                ? message
                : $"{message}: {exception.GetBaseException().Message}";

            try
            {
                Console.WriteLine(payload);
            }
            catch
            {
            }

            if (System.Diagnostics.Debugger.IsAttached)
            {
                System.Diagnostics.Debug.WriteLine(payload);
            }
        }

        internal static string SanitizeExceptionDetails(string exceptionDetails)
        {
            if (string.IsNullOrEmpty(exceptionDetails) || !exceptionDetails.Contains(" in ", StringComparison.Ordinal))
            {
                return exceptionDetails;
            }

            return ExceptionPathRegex().Replace(exceptionDetails, "");
        }

        private bool ArchiveActiveLogFile()
        {
            if (!File.Exists(_logFilePath))
            {
                return false;
            }

            Directory.CreateDirectory(_logBackupDirectory);
            RotateLogBackups();

            string backupPath = GetLogBackupPath();
            bool archived = false;
            for (int attempt = 0; attempt < 3 && !archived; attempt++)
            {
                try
                {
                    File.Move(_logFilePath, backupPath, overwrite: true);
                    archived = true;
                }
                catch (IOException)
                {
                    WaitForRetryDelay(attempt);
                }
                catch (UnauthorizedAccessException)
                {
                    WaitForRetryDelay(attempt);
                }
            }

            if (!archived && File.Exists(_logFilePath))
            {
                File.Copy(_logFilePath, backupPath, overwrite: true);
                File.WriteAllText(_logFilePath, string.Empty);
                archived = true;
            }

            return archived;
        }

        private static void WaitForRetryDelay(int attempt)
        {
            RetryDelayAsyncForTests(10 * (attempt + 1)).GetAwaiter().GetResult();
        }

        private void RotateLogBackups()
        {
            for (int index = LogBackupRetentionCount - 2; index >= 1; index--)
            {
                MoveLogBackupIfExists(GetLogBackupPath(index), GetLogBackupPath(index + 1));
            }

            MoveLogBackupIfExists(GetLogBackupPath(), GetLogBackupPath(1));
        }

        private void PruneOldLogBackups()
        {
            if (!Directory.Exists(_logBackupDirectory))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddDays(-LogBackupMaxAgeDays);
            foreach (string backupPath in EnumerateLogBackupCandidates())
            {
                if (!File.Exists(backupPath))
                {
                    continue;
                }

                DateTime lastWrite = File.GetLastWriteTimeUtc(backupPath);
                if (lastWrite < cutoff)
                {
                    File.Delete(backupPath);
                }
            }
        }

        private IEnumerable<string> EnumerateLogBackupCandidates()
        {
            yield return GetLogBackupPath();

            for (int index = 1; index < LogBackupRetentionCount; index++)
            {
                yield return GetLogBackupPath(index);
            }
        }

        private string GetLogBackupPath(int? index = null)
        {
            return index is null
                ? Path.Combine(_logBackupDirectory, _logFileName + ".bak")
                : Path.Combine(_logBackupDirectory, _logFileName + $".bak.{index.Value}");
        }

        private static void MoveLogBackupIfExists(string source, string destination)
        {
            if (!File.Exists(source))
            {
                return;
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
        }
    }
}
