using System.IO.Compression;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Type4Me.Win.Core;

namespace Type4Me.Win.ASR;

public interface ISpeechRecognizer : IAsyncDisposable
{
    IAsyncEnumerable<RecognitionEvent> Events { get; }
    Task ConnectAsync(IASRProviderConfig config, ASRRequestOptions options, CancellationToken cancellationToken);
    Task SendAudioAsync(byte[] pcm16Mono16k, CancellationToken cancellationToken);
    Task EndAudioAsync(CancellationToken cancellationToken);
    Task DisconnectAsync();
}

public abstract class SpeechRecognizerBase : ISpeechRecognizer
{
    protected readonly Channel<RecognitionEvent> Channel = System.Threading.Channels.Channel.CreateUnbounded<RecognitionEvent>();

    public IAsyncEnumerable<RecognitionEvent> Events => Channel.Reader.ReadAllAsync();

    public abstract Task ConnectAsync(IASRProviderConfig config, ASRRequestOptions options, CancellationToken cancellationToken);
    public abstract Task SendAudioAsync(byte[] pcm16Mono16k, CancellationToken cancellationToken);
    public abstract Task EndAudioAsync(CancellationToken cancellationToken);

    public virtual Task DisconnectAsync()
    {
        Channel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    protected void Emit(RecognitionEvent evt) => Channel.Writer.TryWrite(evt);

    protected static Task SendBinaryFrameAsync(ClientWebSocket? socket, byte[] payload, string providerName, CancellationToken cancellationToken)
    {
        EnsureOpen(socket, providerName);
        return socket!.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
    }

    protected static Task SendTextFrameAsync(ClientWebSocket? socket, string payload, string providerName, CancellationToken cancellationToken)
    {
        EnsureOpen(socket, providerName);
        return socket!.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken);
    }

    protected static void EnsureOpen(ClientWebSocket? socket, string providerName)
    {
        if (socket is not { State: WebSocketState.Open })
        {
            var state = socket?.State.ToString() ?? "not created";
            throw new InvalidOperationException(
                $"{providerName} 语音识别 WebSocket 已断开（状态：{state}）。请检查接口密钥、网络代理、服务商额度或当前选择的识别服务。");
        }
    }
}

public sealed class OpenAIASRClient : SpeechRecognizerBase
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly MemoryStream _audio = new();
    private OpenAIASRConfig? _config;

    public override Task ConnectAsync(IASRProviderConfig config, ASRRequestOptions options, CancellationToken cancellationToken)
    {
        _config = config as OpenAIASRConfig ?? throw new InvalidOperationException("需要 OpenAI 语音识别配置。");
        _audio.SetLength(0);
        Emit(new RecognitionEvent.Ready());
        Emit(new RecognitionEvent.Transcript(new RecognitionTranscript([], "录音中...", "", false)));
        return Task.CompletedTask;
    }

    public override Task SendAudioAsync(byte[] pcm16Mono16k, CancellationToken cancellationToken)
    {
        _audio.Write(pcm16Mono16k);
        return Task.CompletedTask;
    }

    public override async Task EndAudioAsync(CancellationToken cancellationToken)
    {
        if (_config is null) return;
        if (_audio.Length < 16_000)
        {
            Emit(new RecognitionEvent.Transcript(new RecognitionTranscript([], "", "", true)));
            Emit(new RecognitionEvent.Completed());
            Channel.Writer.TryComplete();
            return;
        }

        var wav = WavEncoder.FromPcm16Mono16k(_audio.ToArray());
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(wav)
        {
            Headers = { ContentType = MediaTypeHeaderValue.Parse("audio/wav") }
        }, "file", "audio.wav");
        form.Add(new StringContent(_config.Model), "model");
        form.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl.TrimEnd('/')}/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(body);
        var text = json.RootElement.TryGetProperty("text", out var textElement)
            ? textElement.GetString()?.Trim() ?? ""
            : "";
        Emit(new RecognitionEvent.Transcript(new RecognitionTranscript(
            string.IsNullOrWhiteSpace(text) ? [] : [text],
            "",
            text,
            true)));
        Emit(new RecognitionEvent.Completed());
        Channel.Writer.TryComplete();
    }
}

public sealed class DeepgramASRClient : SpeechRecognizerBase
{
    private ClientWebSocket? _socket;
    private readonly List<string> _confirmed = [];
    private string _lastText = "";

    public override async Task ConnectAsync(IASRProviderConfig config, ASRRequestOptions options, CancellationToken cancellationToken)
    {
        var cfg = config as DeepgramASRConfig ?? throw new InvalidOperationException("需要 Deepgram 语音识别配置。");
        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("Authorization", $"Token {cfg.ApiKey}");
        await _socket.ConnectAsync(BuildUrl(cfg, options), cancellationToken);
        Emit(new RecognitionEvent.Ready());
        _ = Task.Run(() => ReceiveLoop(cancellationToken), CancellationToken.None);
    }

    public override Task SendAudioAsync(byte[] pcm16Mono16k, CancellationToken cancellationToken) =>
        SendBinaryFrameAsync(_socket, pcm16Mono16k, "Deepgram", cancellationToken);

    public override Task EndAudioAsync(CancellationToken cancellationToken) =>
        SendTextFrameAsync(_socket, """{"type":"CloseStream"}""", "Deepgram", cancellationToken);

    public override async Task DisconnectAsync()
    {
        if (_socket is { State: WebSocketState.Open })
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        _socket?.Dispose();
        await base.DisconnectAsync();
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_socket is { State: WebSocketState.Open } socket && !cancellationToken.IsCancellationRequested)
            {
                var text = await ReceiveTextAsync(socket, buffer, cancellationToken);
                if (text is null) break;
                ApplyDeepgramMessage(text);
            }
        }
        catch (Exception ex)
        {
            Emit(new RecognitionEvent.Error(ex));
        }
        finally
        {
            Emit(new RecognitionEvent.Completed());
            Channel.Writer.TryComplete();
        }
    }

    private void ApplyDeepgramMessage(string message)
    {
        using var json = JsonDocument.Parse(message);
        if (!json.RootElement.TryGetProperty("type", out var type) || type.GetString() != "Results") return;
        var transcript = json.RootElement
            .GetProperty("channel")
            .GetProperty("alternatives")[0]
            .GetProperty("transcript")
            .GetString()?.Trim() ?? "";
        var isFinal = ReadBool(json.RootElement, "is_final") ||
                      ReadBool(json.RootElement, "speech_final") ||
                      ReadBool(json.RootElement, "from_finalize");
        if (string.IsNullOrWhiteSpace(transcript) && !isFinal) return;

        var partial = "";
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            var normalized = TextJoiner.AppendSegment(string.Concat(_confirmed), transcript);
            if (isFinal) _confirmed.Add(normalized);
            else partial = normalized;
        }

        var authoritative = string.Concat(_confirmed) + partial;
        if (authoritative == _lastText) return;
        _lastText = authoritative;
        Emit(new RecognitionEvent.Transcript(new RecognitionTranscript(_confirmed.ToArray(), partial, authoritative, isFinal)));
    }

    private static Uri BuildUrl(DeepgramASRConfig cfg, ASRRequestOptions options)
    {
        var query = new Dictionary<string, string>
        {
            ["model"] = cfg.Model,
            ["language"] = cfg.Language,
            ["encoding"] = "linear16",
            ["sample_rate"] = "16000",
            ["channels"] = "1",
            ["interim_results"] = "true",
            ["punctuate"] = options.EnablePunctuation ? "true" : "false",
            ["smart_format"] = "true"
        };
        if (cfg.Numerals) query["numerals"] = "true";
        var qs = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return new Uri("wss://api.deepgram.com/v1/listen?" + qs);
    }

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    internal static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

public sealed class SonioxASRClient : SpeechRecognizerBase
{
    private ClientWebSocket? _socket;
    private string _confirmed = "";
    private string _lastText = "";

    public override async Task ConnectAsync(IASRProviderConfig config, ASRRequestOptions options, CancellationToken cancellationToken)
    {
        var cfg = config as SonioxASRConfig ?? throw new InvalidOperationException("需要 Soniox 语音识别配置。");
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri("wss://stt-rt.soniox.com/transcribe-websocket"), cancellationToken);
        await SendTextFrameAsync(_socket, JsonSerializer.Serialize(new
        {
            api_key = cfg.ApiKey,
            model = cfg.Model,
            audio_format = "pcm_s16le",
            sample_rate = 16000,
            num_channels = 1,
            enable_endpoint_detection = true,
            max_endpoint_delay_ms = 10000,
            language_hints = new[] { "zh", "en" },
            language_hints_strict = true
        }), "Soniox", cancellationToken);
        Emit(new RecognitionEvent.Ready());
        _ = Task.Run(() => ReceiveLoop(cancellationToken), CancellationToken.None);
    }

    public override Task SendAudioAsync(byte[] pcm16Mono16k, CancellationToken cancellationToken) =>
        SendBinaryFrameAsync(_socket, pcm16Mono16k, "Soniox", cancellationToken);

    public override Task EndAudioAsync(CancellationToken cancellationToken) =>
        SendTextFrameAsync(_socket, "", "Soniox", cancellationToken);

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_socket is { State: WebSocketState.Open } socket && !cancellationToken.IsCancellationRequested)
            {
                var text = await DeepgramASRClient.ReceiveTextAsync(socket, buffer, cancellationToken);
                if (text is null) break;
                if (ApplySonioxMessage(text)) break;
            }
        }
        catch (Exception ex)
        {
            Emit(new RecognitionEvent.Error(ex));
        }
        finally
        {
            Emit(new RecognitionEvent.Completed());
            Channel.Writer.TryComplete();
        }
    }

    private bool ApplySonioxMessage(string message)
    {
        using var json = JsonDocument.Parse(message);
        if (json.RootElement.TryGetProperty("error_code", out var errorCode))
        {
            var detail = json.RootElement.TryGetProperty("error_message", out var errorMessage) ? errorMessage.GetString() : "Soniox error";
            Emit(new RecognitionEvent.Error(new InvalidOperationException($"{errorCode.GetInt32()}: {detail}")));
            return true;
        }

        var finalized = new StringBuilder();
        var partial = new StringBuilder();
        if (json.RootElement.TryGetProperty("tokens", out var tokens))
        {
            foreach (var token in tokens.EnumerateArray())
            {
                var text = token.TryGetProperty("text", out var t) ? t.GetString() : "";
                if (string.IsNullOrEmpty(text) || text is "<end>" or "<fin>") continue;
                var isFinal = token.TryGetProperty("is_final", out var final) && final.GetBoolean();
                if (isFinal) finalized.Append(text);
                else partial.Append(text);
            }
        }

        if (finalized.Length > 0) _confirmed += finalized.ToString();
        var authoritative = _confirmed + partial;
        if (authoritative != _lastText)
        {
            _lastText = authoritative;
            Emit(new RecognitionEvent.Transcript(new RecognitionTranscript(
                string.IsNullOrEmpty(_confirmed) ? [] : [_confirmed],
                partial.ToString(),
                authoritative,
                partial.Length == 0)));
        }

        return json.RootElement.TryGetProperty("finished", out var finished) && finished.GetBoolean();
    }

}

public sealed class VolcanoASRClient : SpeechRecognizerBase
{
    private ClientWebSocket? _socket;
    private readonly List<string> _confirmed = [];
    private string _lastPartial = "";

    public override async Task ConnectAsync(IASRProviderConfig config, ASRRequestOptions options, CancellationToken cancellationToken)
    {
        var cfg = config as VolcanoASRConfig ?? throw new InvalidOperationException("需要火山引擎语音识别配置。");
        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("X-Api-App-Key", cfg.AppKey);
        _socket.Options.SetRequestHeader("X-Api-Access-Key", cfg.AccessKey);
        _socket.Options.SetRequestHeader("X-Api-Resource-Id", cfg.ResourceId);
        _socket.Options.SetRequestHeader("X-Api-Connect-Id", Guid.NewGuid().ToString());
        await _socket.ConnectAsync(new Uri("wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async"), cancellationToken);
        await SendBinaryFrameAsync(_socket, VolcProtocol.BuildClientRequest(cfg.Uid, options), "Volcano", cancellationToken);
        Emit(new RecognitionEvent.Ready());
        _ = Task.Run(() => ReceiveLoop(cancellationToken), CancellationToken.None);
    }

    public override Task SendAudioAsync(byte[] pcm16Mono16k, CancellationToken cancellationToken) =>
        SendBinaryFrameAsync(_socket, VolcProtocol.EncodeAudioPacket(pcm16Mono16k, false), "Volcano", cancellationToken);

    public override Task EndAudioAsync(CancellationToken cancellationToken) =>
        SendBinaryFrameAsync(_socket, VolcProtocol.EncodeAudioPacket([], true), "Volcano", cancellationToken);

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        try
        {
            while (_socket is { State: WebSocketState.Open } socket && !cancellationToken.IsCancellationRequested)
            {
                var payload = await ReceiveBytesAsync(socket, buffer, cancellationToken);
                if (payload is null) break;
                var response = VolcProtocol.Decode(payload);
                if (response.Utterances.Count > 0)
                {
                    foreach (var utterance in response.Utterances.Where(u => u.Definite && !_confirmed.Contains(u.Text)))
                    {
                        _confirmed.Add(TextJoiner.AppendSegment(string.Concat(_confirmed), utterance.Text));
                    }
                    var partial = response.Utterances.LastOrDefault(u => !u.Definite)?.Text ?? "";
                    _lastPartial = partial;
                }
                else if (!string.IsNullOrWhiteSpace(response.Text))
                {
                    _lastPartial = response.Text;
                }

                var authoritative = string.Concat(_confirmed) + TextJoiner.AppendSegment(string.Concat(_confirmed), _lastPartial);
                Emit(new RecognitionEvent.Transcript(new RecognitionTranscript(_confirmed.ToArray(), _lastPartial, authoritative, false)));
            }
        }
        catch (Exception ex)
        {
            Emit(new RecognitionEvent.Error(ex));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(_lastPartial) && !_confirmed.Contains(_lastPartial))
            {
                _confirmed.Add(TextJoiner.AppendSegment(string.Concat(_confirmed), _lastPartial));
                var text = string.Concat(_confirmed);
                Emit(new RecognitionEvent.Transcript(new RecognitionTranscript(_confirmed.ToArray(), "", text, true)));
            }
            Emit(new RecognitionEvent.Completed());
            Channel.Writer.TryComplete();
        }
    }

    private static async Task<byte[]?> ReceiveBytesAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return ms.ToArray();
    }
}

public sealed record VolcUtterance(string Text, bool Definite);
public sealed record VolcResponse(string Text, IReadOnlyList<VolcUtterance> Utterances);

public static class VolcProtocol
{
    public static byte[] BuildClientRequest(string uid, ASRRequestOptions options)
    {
        var request = new
        {
            user = new { uid },
            audio = new { format = "pcm", codec = "raw", rate = 16000, bits = 16, channel = 1 },
            request = new
            {
                model_name = "bigmodel",
                enable_punc = options.EnablePunctuation,
                enable_ddc = true,
                enable_nonstream = true,
                show_utterances = true,
                result_type = "full",
                end_window_size = 3000,
                force_to_speech_time = 0,
                context_history_length = options.ContextHistoryLength
            }
        };
        return EncodeMessage(0x10, 0x00, 0x10, JsonSerializer.SerializeToUtf8Bytes(request));
    }

    public static byte[] EncodeAudioPacket(byte[] audio, bool isLast) =>
        EncodeMessage(0x20, isLast ? (byte)0x02 : (byte)0x00, 0x00, audio);

    private static byte[] EncodeMessage(byte messageTypeHighNibble, byte flagsLowNibble, byte serializationAndCompression, byte[] payload)
    {
        var header = new byte[]
        {
            0x11,
            (byte)(messageTypeHighNibble | (flagsLowNibble & 0x0F)),
            serializationAndCompression,
            0x00
        };
        using var ms = new MemoryStream();
        ms.Write(header);
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)payload.Length);
        ms.Write(len);
        ms.Write(payload);
        return ms.ToArray();
    }

    public static VolcResponse Decode(byte[] data)
    {
        if (data.Length < 8) return new VolcResponse("", []);
        var serialization = (data[2] >> 4) & 0x0F;
        var compression = data[2] & 0x0F;
        var offset = (data[0] & 0x0F) * 4;
        var flags = data[1] & 0x0F;
        if (flags is 0x01 or 0x03) offset += 4;
        if (data.Length < offset + 4) return new VolcResponse("", []);
        var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;
        if (size <= 0 || data.Length < offset + size) return new VolcResponse("", []);
        var payload = data.AsSpan(offset, size).ToArray();
        if (compression == 1)
        {
            using var input = new MemoryStream(payload);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            payload = output.ToArray();
        }
        if (serialization != 1) return new VolcResponse("", []);
        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        var result = root.TryGetProperty("result", out var r) ? r : root;
        var text = result.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? "" : "";
        var utterances = new List<VolcUtterance>();
        if (result.TryGetProperty("utterances", out var utts) && utts.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in utts.EnumerateArray())
            {
                utterances.Add(new VolcUtterance(
                    item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    item.TryGetProperty("definite", out var d) && d.GetBoolean()));
            }
        }
        return new VolcResponse(text, utterances);
    }
}
