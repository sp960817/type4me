using Type4Me.Win.ASR;
using Type4Me.Win.Core;
using Type4Me.Win.Services;

namespace Type4Me.Win.Session;

public sealed class RecognitionSession : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly AudioCaptureService _audio;
    private readonly TextInjectionService _injection;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _sessionCts;
    private ISpeechRecognizer? _recognizer;
    private Task? _eventTask;
    private ProcessingMode _currentMode = ProcessingMode.Direct;
    private PromptContext _promptContext = PromptContext.Empty;
    private DateTimeOffset _startedAt;
    private RecognitionTranscript _lastTranscript = RecognitionTranscript.Empty;
    private SessionState _state = SessionState.Idle;
    private int _runtimeErrorSignaled;
    private long _audioBytesSent;

    public RecognitionSession(
        SettingsStore settings,
        HistoryStore history,
        AudioCaptureService audio,
        TextInjectionService injection)
    {
        _settings = settings;
        _history = history;
        _audio = audio;
        _injection = injection;
        _audio.AudioChunkReady += OnAudioChunkReady;
    }

    public SessionState State
    {
        get { lock (_gate) return _state; }
        private set
        {
            lock (_gate) _state = value;
            StatusChanged?.Invoke(value, _lastTranscript.DisplayText);
        }
    }

    public event Action<SessionState, string>? StatusChanged;
    public event Action<string>? Error;

    public async Task ToggleAsync(ProcessingMode mode)
    {
        if (State == SessionState.Recording || State == SessionState.Starting)
        {
            await StopAsync();
        }
        else if (State == SessionState.Idle)
        {
            await StartAsync(mode, IntPtr.Zero);
        }
    }

    public async Task StartAsync(ProcessingMode mode, IntPtr targetWindow)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (State != SessionState.Idle)
            {
                return;
            }

            State = SessionState.Starting;
            _sessionCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            _currentMode = mode;
            _promptContext = _injection.CapturePromptContext(targetWindow);
            _lastTranscript = RecognitionTranscript.Empty;
            _startedAt = DateTimeOffset.Now;
            _runtimeErrorSignaled = 0;
            Interlocked.Exchange(ref _audioBytesSent, 0);

            try
            {
                var config = _settings.LoadSelectedASRConfig()
                    ?? throw new InvalidOperationException("请先在设置中配置可用的语音识别接口密钥。");
                if (!config.IsValid)
                {
                    throw new InvalidOperationException("当前语音识别配置不完整。");
                }

                var descriptor = ASRProviderRegistry.Get(config.Provider);
                _recognizer = descriptor.CreateClient();
                _eventTask = Task.Run(() => ConsumeEventsAsync(_recognizer, _sessionCts.Token), CancellationToken.None);
                await _recognizer.ConnectAsync(config, new ASRRequestOptions(_settings.PunctuationMode), _sessionCts.Token);
                _audio.Start();
                State = SessionState.Recording;
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex.Message);
                await ResetAsync();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (State is not (SessionState.Recording or SessionState.Starting))
            {
                return;
            }

            State = SessionState.Finishing;
            try
            {
                _audio.Stop();
                if (Interlocked.Read(ref _audioBytesSent) == 0)
                {
                    await ResetAsync();
                    StatusChanged?.Invoke(SessionState.Idle, "已取消：没有录到声音");
                    return;
                }

                if (_recognizer is not null)
                {
                    await _recognizer.EndAudioAsync(_sessionCts?.Token ?? CancellationToken.None);
                }

                if (_eventTask is not null)
                {
                    var completed = await Task.WhenAny(_eventTask, Task.Delay(TimeSpan.FromSeconds(30)));
                    if (completed != _eventTask && State != SessionState.Idle)
                    {
                        await FinalizeAsync(_lastTranscript.DisplayText, "timeout");
                    }
                }
                else if (State != SessionState.Idle)
                {
                    await FinalizeAsync(_lastTranscript.DisplayText, "stopped");
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex.Message);
                await FinalizeAsync("", "failed");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task AbortAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            _sessionCts?.Cancel();
            _audio.Stop();
            await ResetAsync();
            StatusChanged?.Invoke(SessionState.Idle, "已取消");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async void OnAudioChunkReady(byte[] chunk)
    {
        try
        {
            if (_recognizer is not null && State is SessionState.Recording or SessionState.Finishing)
            {
                Interlocked.Add(ref _audioBytesSent, chunk.Length);
                await _recognizer.SendAudioAsync(chunk, _sessionCts?.Token ?? CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            await FailActiveSessionAsync(UserFacingASRError(ex));
        }
    }

    private async Task ConsumeEventsAsync(ISpeechRecognizer recognizer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in recognizer.Events.WithCancellation(cancellationToken))
            {
                switch (evt)
                {
                    case RecognitionEvent.Ready:
                        StatusChanged?.Invoke(State, "准备就绪");
                        break;
                    case RecognitionEvent.Transcript transcript:
                        _lastTranscript = transcript.Value;
                        StatusChanged?.Invoke(State, transcript.Value.DisplayText);
                        break;
                    case RecognitionEvent.Error error:
                        await FailActiveSessionAsync(UserFacingASRError(error.Exception));
                        return;
                    case RecognitionEvent.Completed:
                        await FinalizeAsync(_lastTranscript.DisplayText, "completed");
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await FailActiveSessionAsync(UserFacingASRError(ex));
            await FinalizeAsync(_lastTranscript.DisplayText, "failed");
        }
    }

    private async Task FailActiveSessionAsync(string message)
    {
        if (Interlocked.Exchange(ref _runtimeErrorSignaled, 1) == 1)
        {
            return;
        }

        Error?.Invoke(message);
        _audio.Stop();
        _sessionCts?.Cancel();
        await ResetAsync();
    }

    private static string UserFacingASRError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("WebSocket", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("websocket", StringComparison.OrdinalIgnoreCase))
        {
            return "语音识别连接已断开。请检查当前语音服务商的接口密钥、网络代理、账号额度，或切换到 OpenAI 批量转写再试。原始错误：" + message;
        }
        return message;
    }

    private async Task FinalizeAsync(string rawText, string status)
    {
        if (State == SessionState.Idle) return;

        var normalizedRawText = TextPostProcessor.ApplyPunctuationMode(rawText.Trim(), _settings.PunctuationMode);
        var finalText = normalizedRawText;
        string? processed = null;
        if (!string.IsNullOrWhiteSpace(finalText) && !string.IsNullOrWhiteSpace(_currentMode.Prompt))
        {
            State = SessionState.PostProcessing;
            try
            {
                var llmConfig = _settings.LoadSelectedLLMConfig();
                if (llmConfig is not null)
                {
                    var llm = LLMProviderRegistry.Get(_settings.SelectedLLMProvider).CreateClient();
                    processed = await llm.ProcessAsync(finalText, _currentMode.Prompt, llmConfig, _promptContext, _sessionCts?.Token ?? CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(processed))
                    {
                        finalText = processed.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke("文本模型处理失败，使用原始识别文本：" + ex.Message);
            }
        }

        InjectionOutcome outcome = InjectionOutcome.CopiedToClipboard;
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            State = SessionState.Injecting;
            outcome = await _injection.InjectAsync(finalText, _sessionCts?.Token ?? CancellationToken.None);
        }

        _history.Insert(new HistoryRecord(
            Guid.NewGuid().ToString(),
            _startedAt,
            Math.Max(0, (DateTimeOffset.Now - _startedAt).TotalSeconds),
            normalizedRawText,
            _currentMode.Name,
            processed,
            finalText,
            status,
            finalText.Length,
            _settings.SelectedASRProvider.ToString(),
            null));

        StatusChanged?.Invoke(SessionState.Idle, outcome == InjectionOutcome.Inserted ? "已输入" : "已复制到剪贴板");
        await ResetAsync();
    }

    private async Task ResetAsync()
    {
        _audio.Stop();
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        if (_recognizer is not null)
        {
            await _recognizer.DisconnectAsync();
            await _recognizer.DisposeAsync();
            _recognizer = null;
        }
        _eventTask = null;
        State = SessionState.Idle;
    }

    public void Dispose()
    {
        _audio.AudioChunkReady -= OnAudioChunkReady;
        _audio.Dispose();
        _sessionCts?.Dispose();
        _lifecycleGate.Dispose();
    }
}
