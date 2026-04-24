using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Platform
{
    public readonly record struct SingleInstanceCommandResult(
        int ExitCode,
        string? Output = null,
        string? ErrorCode = null,
        string? ErrorMessage = null,
        int? ProtocolVersion = null);

    internal readonly record struct SingleInstanceCommandResultParseResult(
        SingleInstanceCommandResult Response,
        string NormalizationMode);

    internal static class SingleInstanceCommandResultParser
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        internal static bool TryParse(string payload, out SingleInstanceCommandResultParseResult result)
        {
            result = default;

            SingleInstanceCommandResult? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<SingleInstanceCommandResult?>(payload, SerializerOptions);
            }
            catch (JsonException)
            {
                return false;
            }

            if (!parsed.HasValue)
            {
                return false;
            }

            string normalizationMode = parsed.Value.ProtocolVersion switch
            {
                1 => "structured-v1",
                int protocolVersion => $"structured-v{protocolVersion}",
                null => "structured-unspecified-version",
            };

            result = new SingleInstanceCommandResultParseResult(parsed.Value, normalizationMode);
            return true;
        }

        internal static string Serialize(SingleInstanceCommandResult response) => JsonSerializer.Serialize(response, SerializerOptions);
    }

    internal enum SingleInstanceSignalFailureKind
    {
        None = 0,
        ConnectionFailed = 1,
        InvalidResponse = 2,
    }

    internal enum SingleInstanceAcquireDisposition
    {
        Acquired = 0,
        ExistingHealthyInstance = 1,
        ExistingUnresponsiveInstance = 2,
    }

    internal readonly record struct SingleInstanceAcquireResult(
        SingleInstanceAcquireDisposition Disposition,
        SingleInstanceSignalFailureKind FailureKind = SingleInstanceSignalFailureKind.None)
    {
        internal bool Acquired => Disposition == SingleInstanceAcquireDisposition.Acquired;
        internal bool ExistingHealthy => Disposition == SingleInstanceAcquireDisposition.ExistingHealthyInstance;
        internal bool ExistingUnresponsive => Disposition == SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance;
    }

    public class SingleInstanceHelper : IDisposable
    {
        private const string ActivationPayload = "ACTIVATE";
        private const int ResponseProtocolVersion = 1;
        private Mutex? _mutex;
        private bool _owned;
        private bool _disposed;
        private readonly Logger _logger;
        private CancellationTokenSource? _activationListenerCts;
        private Task? _activationListenerTask;
        private TaskCompletionSource? _activationListenerReadySource;
        private readonly string _mutexName;
        private readonly string _activationPipeName;
        private readonly int _responseTimeoutMs;
        private readonly int _requestReadTimeoutMs;

        public event EventHandler? ActivationRequested;
        public event Func<string, Task<SingleInstanceCommandResult>>? CommandRequested;
        public bool ExistingInstanceDetected { get; private set; }
        public bool LastSignalExistingSucceeded { get; private set; }
        public int? LastSignalExitCode { get; private set; }
        public string? LastSignalOutput { get; private set; }
        public string? LastSignalErrorCode { get; private set; }
        public string? LastSignalErrorMessage { get; private set; }
        public int? LastSignalProtocolVersion { get; private set; }
        internal SingleInstanceSignalFailureKind LastSignalFailureKind { get; private set; }
        internal Task ActivationListenerReadyForTests => _activationListenerReadySource?.Task ?? Task.CompletedTask;
        internal bool HasActivationRequestedSubscribersForTests => ActivationRequested != null;
        internal bool HasCommandRequestedSubscribersForTests => CommandRequested != null;

        public SingleInstanceHelper()
            : this(AppConstants.Identity.AppName, $"{AppConstants.Identity.AppName}.Activation")
        {
        }

        internal SingleInstanceHelper(
            string mutexName,
            string activationPipeName,
            int responseTimeoutMs = AppConstants.Timing.SingleInstanceResponseTimeoutMs,
            int requestReadTimeoutMs = AppConstants.Timing.SingleInstanceRequestReadTimeoutMs)
        {
            _logger = Logger.Instance;
            _mutexName = mutexName;
            _activationPipeName = activationPipeName;
            _responseTimeoutMs = Math.Max(1, responseTimeoutMs);
            _requestReadTimeoutMs = Math.Max(1, requestReadTimeoutMs);
        }

        /// <summary>
        /// Attempts to acquire the single-instance mutex for the current process.
        /// </summary>
        /// <remarks>
        /// On success, the helper starts the background activation listener used for later UI activation or CLI
        /// forwarding. On failure, the helper marks that another instance exists and optionally signals that instance
        /// with either an activation payload or a forwarded command payload.
        /// </remarks>
        public bool TryAcquire(string? payloadToExistingInstance = null)
        {
            return TryAcquireDetailed(payloadToExistingInstance).Acquired;
        }

        internal SingleInstanceAcquireResult TryAcquireDetailed(
            string? payloadToExistingInstance = null,
            bool showUserErrors = true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            bool isCliForward = !string.IsNullOrWhiteSpace(payloadToExistingInstance);
            ExistingInstanceDetected = false;
            LastSignalExistingSucceeded = false;
            LastSignalExitCode = null;
            LastSignalOutput = null;
            LastSignalErrorCode = null;
            LastSignalErrorMessage = null;
            LastSignalProtocolVersion = null;
            LastSignalFailureKind = SingleInstanceSignalFailureKind.None;

            if (_owned && _mutex != null)
            {
                return new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.Acquired);
            }

            try
            {
                _mutex = new Mutex(true, _mutexName, out bool createdNew);
                _owned = createdNew;

                if (!_owned)
                {
                    ExistingInstanceDetected = true;
                    _mutex.Dispose();
                    _mutex = null;

                    if (isCliForward && _logger.IsEnabled(LogLevel.Info))
                    {
                        _logger.Info("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardStart} | targetPipe={_activationPipeName} mode=existing-instance requestProtocolVersion={GetRequestProtocolVersion(payloadToExistingInstance)} requestAction={GetRequestAction(payloadToExistingInstance)}");
                    }

                    LastSignalExistingSucceeded = SignalExistingInstance(payloadToExistingInstance ?? ActivationPayload);
                    if (!LastSignalExistingSucceeded)
                    {
                        if (!isCliForward && showUserErrors)
                        {
                            string message = LastSignalFailureKind == SingleInstanceSignalFailureKind.None
                                ? "An instance of the application is already running."
                                : "An existing AudioPilot instance is not responding.";
                            MessageBoxService.ShowError(message);
                        }

                        return new SingleInstanceAcquireResult(
                            SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance,
                            LastSignalFailureKind);
                    }

                    return new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingHealthyInstance);
                }

                StartActivationListener();

                return new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.Acquired);
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceHelper", "Failed to check for running instances", nameof(TryAcquire), ex);
                if (!isCliForward && showUserErrors)
                {
                    MessageBoxService.ShowError("Failed to check for running instances.");
                }
                LastSignalFailureKind = SingleInstanceSignalFailureKind.ConnectionFailed;
                return new SingleInstanceAcquireResult(
                    SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance,
                    LastSignalFailureKind);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopActivationListener();
            ActivationRequested = null;
            CommandRequested = null;

            if (_mutex != null)
            {
                try
                {
                    _mutex.Dispose();
                }
                catch
                {
                }
                _mutex = null;
            }

            _owned = false;
            GC.SuppressFinalize(this);
        }

        private void StartActivationListener()
        {
            if (_activationListenerCts != null)
            {
                return;
            }

            var activationListenerCts = new CancellationTokenSource();
            _activationListenerCts = activationListenerCts;
            _activationListenerReadySource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken activationToken = activationListenerCts.Token;
            _activationListenerTask = Task.Run(() => ActivationListenerLoopAsync(activationToken), activationToken);
        }

        private void StopActivationListener()
        {
            CancellationTokenSource? listenerCts = Interlocked.Exchange(ref _activationListenerCts, null);
            Task? listenerTask = Interlocked.Exchange(ref _activationListenerTask, null);

            if (listenerCts == null)
            {
                return;
            }

            try
            {
                listenerCts.Cancel();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceHelper", "Activation listener stop failed", nameof(StopActivationListener), ex);
            }
            finally
            {
                _activationListenerReadySource = null;
                listenerCts.Dispose();
            }

            if (listenerTask != null)
            {
                _ = ObserveActivationListenerStopAsync(listenerTask);
            }
        }

        private async Task ObserveActivationListenerStopAsync(Task listenerTask)
        {
            try
            {
                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(AppConstants.Timing.SingleInstanceListenerStopTimeoutSeconds));
                Task completedTask = await Task.WhenAny(listenerTask, timeoutTask).ConfigureAwait(false);
                if (completedTask != listenerTask)
                {
                    _logger.Warning("SingleInstanceHelper", "Activation listener stop timed out", nameof(StopActivationListener));
                    return;
                }

                await listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceHelper", "Activation listener stop failed", nameof(StopActivationListener), ex);
            }
        }

        private async Task ActivationListenerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _activationPipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    _activationListenerReadySource?.TrySetResult();

                    await server.WaitForConnectionAsync(cancellationToken);

                    string? payload;
                    using (CancellationTokenSource requestReadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        requestReadCts.CancelAfter(_requestReadTimeoutMs);
                        try
                        {
                            payload = await ReadFramedMessageAsync(server, requestReadCts.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            if (_logger.IsEnabled(LogLevel.Warning))
                            {
                                _logger.Warning("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ActivationSignalIgnored} | reason=request-read-timeout timeoutMs={_requestReadTimeoutMs}");
                            }

                            continue;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ActivationSignalIgnored} | reason=empty-payload");
                        }
                        continue;
                    }

                    if (string.Equals(payload, ActivationPayload, StringComparison.Ordinal))
                    {
                        ActivationRequested?.Invoke(this, EventArgs.Empty);
                        await WriteResponseAsync(server, new SingleInstanceCommandResult(0, ProtocolVersion: ResponseProtocolVersion), cancellationToken);
                        continue;
                    }

                    SingleInstanceCommandResult response;
                    var commandHandler = CommandRequested;
                    if (commandHandler == null)
                    {
                        response = new SingleInstanceCommandResult(3, ErrorCode: "ui-host-unavailable", ErrorMessage: "No command handler is available.", ProtocolVersion: ResponseProtocolVersion);
                    }
                    else
                    {
                        try
                        {
                            response = await commandHandler(payload);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning("SingleInstanceHelper", "Command forwarding handler failed", nameof(ActivationListenerLoopAsync), ex);
                            response = new SingleInstanceCommandResult(7, ErrorCode: "forwarded-runtime-failed", ErrorMessage: "Command execution failed in the running UI host.", ProtocolVersion: ResponseProtocolVersion);
                        }
                    }

                    await WriteResponseAsync(server, response, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warning("SingleInstanceHelper", "Activation listener error", nameof(ActivationListenerLoopAsync), ex);
                    await Task.Delay(AppConstants.Timing.SingleInstanceListenerRetryDelayMs, cancellationToken);
                }
            }
        }

        private static async Task WriteResponseAsync(NamedPipeServerStream server, SingleInstanceCommandResult response, CancellationToken cancellationToken)
        {
            var wirePayload = SingleInstanceCommandResultParser.Serialize(response);
            await WriteFramedMessageAsync(server, wirePayload, cancellationToken);
        }

        private bool SignalExistingInstance(string payloadText)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _activationPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(AppConstants.Timing.SingleInstanceConnectTimeoutMs);
                WriteFramedMessage(client, payloadText);

                if (!TryReadResponseWithTimeout(client, _responseTimeoutMs, out SingleInstanceCommandResult response, out string normalizationMode))
                {
                    LastSignalFailureKind = SingleInstanceSignalFailureKind.InvalidResponse;
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.Warning("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardResponseInvalid} | targetPipe={_activationPipeName}");
                    }
                    return false;
                }

                LastSignalExitCode = response.ExitCode;
                LastSignalOutput = !string.IsNullOrWhiteSpace(response.Output) ? response.Output : response.ErrorMessage;
                LastSignalErrorCode = response.ErrorCode;
                LastSignalErrorMessage = response.ErrorMessage;
                LastSignalProtocolVersion = response.ProtocolVersion;
                LastSignalFailureKind = SingleInstanceSignalFailureKind.None;

                if (_logger.IsEnabled(LogLevel.Info))
                {
                    _logger.Info("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardResponseReceived} | targetPipe={_activationPipeName} exitCode={response.ExitCode} errorCode={(response.ErrorCode ?? "none")} protocolVersion={(response.ProtocolVersion?.ToString() ?? "unknown")} normalizationMode={normalizationMode}");
                }
                return true;
            }
            catch (TimeoutException ex)
            {
                LastSignalFailureKind = SingleInstanceSignalFailureKind.ConnectionFailed;
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardConnectFailed} | targetPipe={_activationPipeName} exceptionType={ex.GetType().Name}");
                }
                _logger.Trace("SingleInstanceHelper", () => $"Failed to signal existing instance: {ex.GetType().Name}");
                return false;
            }
            catch (OperationCanceledException ex)
            {
                LastSignalFailureKind = SingleInstanceSignalFailureKind.ConnectionFailed;
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardConnectFailed} | targetPipe={_activationPipeName} exceptionType={ex.GetType().Name}");
                }
                _logger.Trace("SingleInstanceHelper", () => $"Failed to signal existing instance: {ex.GetType().Name}");
                return false;
            }
            catch (Exception ex)
            {
                LastSignalFailureKind = SingleInstanceSignalFailureKind.ConnectionFailed;
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardConnectFailed} | targetPipe={_activationPipeName} exceptionType={ex.GetType().Name}");
                }
                _logger.Trace("SingleInstanceHelper", () => $"Failed to signal existing instance: {ex.GetType().Name}");
                return false;
            }
        }

        private static bool TryReadResponseWithTimeout(
            NamedPipeClientStream client,
            int timeoutMs,
            out SingleInstanceCommandResult response,
            out string normalizationMode)
        {
            using var timeout = new CancellationTokenSource(Math.Max(1, timeoutMs));
            string? responseText = ReadFramedMessageAsync(client, timeout.Token).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(responseText))
            {
                response = default;
                normalizationMode = string.Empty;
                return false;
            }

            if (!SingleInstanceCommandResultParser.TryParse(responseText, out SingleInstanceCommandResultParseResult parsed))
            {
                response = default;
                normalizationMode = string.Empty;
                return false;
            }

            response = parsed.Response;
            normalizationMode = parsed.NormalizationMode;
            return true;
        }

        private static string GetRequestProtocolVersion(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return "activation";
            }

            return TryReadJsonProperty(payload, "protocolVersion") ?? "unknown";
        }

        private static string GetRequestAction(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return "activation";
            }

            return TryReadJsonProperty(payload, "action") ?? "unknown";
        }

        private static string? TryReadJsonProperty(string payload, string propertyName)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty(propertyName, out JsonElement property))
                {
                    return null;
                }

                return property.ValueKind switch
                {
                    JsonValueKind.String => property.GetString(),
                    JsonValueKind.Number => property.GetRawText(),
                    _ => null,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void WriteFramedMessage(NamedPipeClientStream client, string payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            var lengthBytes = BitConverter.GetBytes(bytes.Length);
            client.Write(lengthBytes, 0, lengthBytes.Length);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }

        private static async Task WriteFramedMessageAsync(Stream stream, string payload, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            var lengthBytes = BitConverter.GetBytes(bytes.Length);
            await stream.WriteAsync(lengthBytes.AsMemory(0, lengthBytes.Length), cancellationToken);
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static async Task<string?> ReadFramedMessageAsync(Stream stream, CancellationToken cancellationToken)
        {
            var lengthBytes = new byte[sizeof(int)];
            if (!await ReadExactlyAsync(stream, lengthBytes, cancellationToken))
            {
                return null;
            }

            int length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > 64 * 1024)
            {
                return null;
            }

            var payloadBytes = new byte[length];
            if (!await ReadExactlyAsync(stream, payloadBytes, cancellationToken))
            {
                return null;
            }

            return Encoding.UTF8.GetString(payloadBytes);
        }

        private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }
    }
}
