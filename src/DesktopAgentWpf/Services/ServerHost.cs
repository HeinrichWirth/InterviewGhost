using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Timer = System.Threading.Timer;

namespace DesktopAgentWpf.Services;

public sealed class ServerHost : IDisposable
{
    public event Action<string>? Log;
    public event Action<string>? StatusChanged;
    public event Action<int, string>? RagProgress;
    public event Action<string>? RagStatus;

    private WebApplication? _app;
    private ServerState? _state;
    private string? _outputDeviceId;
    private string? _inputDeviceId;
    private bool _ragEnabled;

    public string Url { get; private set; } = string.Empty;
    public int Port { get; private set; }
    public bool IsRagEnabled => _ragEnabled;

    public async Task StartAsync()
    {
        var contentRoot = AppContext.BaseDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot
        });

        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        var portPreferred = builder.Configuration.GetValue<int?>("Server:Port") ?? 43127;
        var maxMinutes = builder.Configuration.GetValue<int?>("Recording:MaxMinutes") ?? 10;
        var model = builder.Configuration.GetValue<string>("Gemini:Model") ?? "gemini-3-flash-preview";
        var modelAudio = builder.Configuration.GetValue<string>("Gemini:ModelAudio");
        var modelVision = builder.Configuration.GetValue<string>("Gemini:ModelVision");
        var answerMaxTokens = builder.Configuration.GetValue<int?>("Gemini:AnswerMaxOutputTokens") ?? 2048;
        var thinkingLevel = builder.Configuration.GetValue<string>("Gemini:ThinkingLevel");
        var responseLanguage = builder.Configuration.GetValue<string>("Gemini:ResponseLanguage");
        var systemPrompt = builder.Configuration.GetValue<string>("Gemini:SystemPrompt");
        var minAudioBytes = builder.Configuration.GetValue<int?>("Recording:MinAudioBytes") ?? 8192;
        var maxHistory = builder.Configuration.GetValue<int?>("Memory:MaxMessages") ?? 0;
        var verboseLogs = builder.Configuration.GetValue<bool?>("Logging:Verbose") ?? true;
        var maxLogChars = builder.Configuration.GetValue<int?>("Logging:MaxTextChars") ?? 1200;
        var streamEnabled = builder.Configuration.GetValue<bool?>("Stream:Enabled") ?? true;
        var streamFps = builder.Configuration.GetValue<int?>("Stream:Fps") ?? 10;
        var streamJpegQuality = builder.Configuration.GetValue<int?>("Stream:JpegQuality") ?? 70;
        var streamMaxWidth = builder.Configuration.GetValue<int?>("Stream:MaxWidth") ?? 1280;
        var streamMaxHeight = builder.Configuration.GetValue<int?>("Stream:MaxHeight") ?? 720;
        var ragEnabled = builder.Configuration.GetValue<bool?>("Rag:Enabled") ?? false;
        var ragProvider = builder.Configuration.GetValue<string>("Rag:Provider") ?? "Local";
        var ragFolder = builder.Configuration.GetValue<string>("Rag:Folder") ?? "rag";
        var ragMaxFiles = builder.Configuration.GetValue<int?>("Rag:MaxFiles") ?? 500;
        var ragMaxFileSizeMb = builder.Configuration.GetValue<int?>("Rag:MaxFileSizeMb") ?? 10;
        var ragChunkChars = builder.Configuration.GetValue<int?>("Rag:ChunkChars") ?? 1000;
        var ragChunkOverlap = builder.Configuration.GetValue<int?>("Rag:ChunkOverlap") ?? 200;
        var ragTopK = builder.Configuration.GetValue<int?>("Rag:TopK") ?? 4;
        var ragMaxContextChars = builder.Configuration.GetValue<int?>("Rag:MaxContextChars") ?? 4000;
        var ragMinScore = builder.Configuration.GetValue<double?>("Rag:MinScore") ?? 0.08;
        var ragQueryMaxChars = builder.Configuration.GetValue<int?>("Rag:QueryMaxChars") ?? 400;
        var ragDefaultQuery = builder.Configuration.GetValue<string>("Rag:DefaultQuery");
        var ragUseAudio = builder.Configuration.GetValue<bool?>("Rag:UseForAudio") ?? false;
        var ragUseScreenshot = builder.Configuration.GetValue<bool?>("Rag:UseForScreenshot") ?? true;
        var ragUseFollowUp = builder.Configuration.GetValue<bool?>("Rag:UseForFollowUp") ?? true;
        var ragAllowedExtensions = builder.Configuration.GetValue<string>("Rag:AllowedExtensions");
        var ragEmbeddingModel = builder.Configuration.GetValue<string>("Rag:GeminiEmbeddingModel") ?? "gemini-embedding-001";
        var ragEmbeddingTaskDoc = builder.Configuration.GetValue<string>("Rag:GeminiEmbeddingTaskDoc") ?? "RETRIEVAL_DOCUMENT";
        var ragEmbeddingTaskQuery = builder.Configuration.GetValue<string>("Rag:GeminiEmbeddingTaskQuery") ?? "RETRIEVAL_QUERY";
        var ragEmbeddingDim = builder.Configuration.GetValue<int?>("Rag:GeminiEmbeddingOutputDim") ?? 0;
        var ragEmbeddingBatch = builder.Configuration.GetValue<int?>("Rag:GeminiEmbeddingBatchSize") ?? 16;
        var ragFileSearchStoreName = builder.Configuration.GetValue<string>("Rag:GeminiFileSearchStoreName");
        var ragFileSearchDisplayName = builder.Configuration.GetValue<string>("Rag:GeminiFileSearchStoreDisplayName") ?? "interviewghost-rag";
        var ragFileSearchUploadOnStart = builder.Configuration.GetValue<bool?>("Rag:GeminiFileSearchUploadOnStart") ?? true;
        var ragFileSearchMetadataFilter = builder.Configuration.GetValue<string>("Rag:GeminiFileSearchMetadataFilter");
        var ragFileSearchChunkMaxTokensPerChunk = builder.Configuration.GetValue<int?>("Rag:GeminiFileSearchChunkMaxTokensPerChunk") ?? 0;
        var ragFileSearchChunkMaxOverlapTokens = builder.Configuration.GetValue<int?>("Rag:GeminiFileSearchChunkMaxOverlapTokens") ?? 0;
        var ragFileSearchOperationTimeoutSeconds = builder.Configuration.GetValue<int?>("Rag:GeminiFileSearchOperationTimeoutSeconds") ?? 600;
        var ragFileSearchOperationPollSeconds = builder.Configuration.GetValue<int?>("Rag:GeminiFileSearchOperationPollSeconds") ?? 5;

        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = builder.Configuration.GetValue<string>("Gemini:ApiKey");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                LogMessage("Using Gemini:ApiKey from appsettings.json.");
            }
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogMessage("GEMINI_API_KEY is not set. The phone UI will show an error until you set it and restart the app.");
        }

        Port = GetAvailablePort(portPreferred);
        if (Port != portPreferred)
        {
            LogMessage($"Port {portPreferred} is in use. Using {Port} instead.");
        }

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(Port);
        });

        builder.Services.AddSignalR();

        var token = Guid.NewGuid().ToString("N");
        var tokenAuth = new TokenAuth(token);
        var audioRecorder = new AudioRecorder();
        var screenshotService = new ScreenshotService(LogMessage, verboseLogs);
        var audioModelResolved = string.IsNullOrWhiteSpace(modelAudio) ? model : modelAudio;
        var visionModelResolved = string.IsNullOrWhiteSpace(modelVision) ? model : modelVision;
        var useGeminiFileSearch = ragEnabled && IsGeminiFileSearchProvider(ragProvider);
        var useGeminiEmbeddings = ragEnabled && !useGeminiFileSearch && IsGeminiEmbeddingsProvider(ragProvider);
        IRagProvider ragProviderService;
        if (useGeminiFileSearch)
        {
            var timeoutSeconds = Math.Max(ragFileSearchOperationTimeoutSeconds, 30);
            var pollSeconds = Math.Clamp(ragFileSearchOperationPollSeconds, 1, 60);
            var fileSearchOptions = new GeminiFileSearchOptions(
                ragEnabled,
                ragFileSearchStoreName,
                ragFileSearchDisplayName,
                ragFileSearchUploadOnStart,
                ragFolder,
                ragMaxFiles,
                ragMaxFileSizeMb,
                ragTopK,
                ragFileSearchMetadataFilter,
                ragQueryMaxChars,
                ragDefaultQuery,
                ragUseAudio,
                ragUseScreenshot,
                ragUseFollowUp,
                ParseExtensions(ragAllowedExtensions),
                TimeSpan.FromSeconds(timeoutSeconds),
                TimeSpan.FromSeconds(pollSeconds),
                ragFileSearchChunkMaxTokensPerChunk,
                ragFileSearchChunkMaxOverlapTokens);
            ragProviderService = new GeminiFileSearchService(apiKey, fileSearchOptions, LogMessage);
        }
        else if (useGeminiEmbeddings)
        {
            var embeddingsOptions = new GeminiEmbeddingsOptions(
                ragEnabled,
                ragFolder,
                ragMaxFiles,
                ragMaxFileSizeMb,
                ragChunkChars,
                ragChunkOverlap,
                ragTopK,
                ragMaxContextChars,
                (float)ragMinScore,
                ragQueryMaxChars,
                ragDefaultQuery,
                ragUseAudio,
                ragUseScreenshot,
                ragUseFollowUp,
                ParseExtensions(ragAllowedExtensions),
                ragEmbeddingModel,
                ragEmbeddingTaskDoc,
                ragEmbeddingTaskQuery,
                ragEmbeddingDim,
                Math.Clamp(ragEmbeddingBatch, 1, 64));
            ragProviderService = new GeminiEmbeddingsService(apiKey, embeddingsOptions, LogMessage);
        }
        else
        {
            var ragOptions = new RagOptions(
                ragEnabled,
                ragFolder,
                ragMaxFiles,
                ragMaxFileSizeMb,
                ragChunkChars,
                ragChunkOverlap,
                ragTopK,
                ragMaxContextChars,
                ragMinScore,
                ragQueryMaxChars,
                ragDefaultQuery,
                ragUseAudio,
                ragUseScreenshot,
                ragUseFollowUp,
                ParseExtensions(ragAllowedExtensions));
            ragProviderService = new RagService(ragOptions, LogMessage);
        }

        var geminiClientAudio = new GeminiClient(apiKey, audioModelResolved, LogMessage, verboseLogs, maxLogChars, answerMaxTokens, thinkingLevel, responseLanguage, systemPrompt);
        var geminiClientVision = new GeminiClient(apiKey, visionModelResolved, LogMessage, verboseLogs, maxLogChars, answerMaxTokens, thinkingLevel, responseLanguage, systemPrompt);
        var state = new ServerState(
            audioRecorder,
            screenshotService,
            geminiClientAudio,
            geminiClientVision,
            ragProviderService,
            tokenAuth,
            LogMessage,
            TimeSpan.FromMinutes(maxMinutes),
            minAudioBytes,
            maxHistory,
            streamEnabled,
            streamFps,
            streamJpegQuality,
            streamMaxWidth,
            streamMaxHeight);

        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(tokenAuth);
        builder.Services.AddSingleton(audioRecorder);
        builder.Services.AddSingleton(screenshotService);
        builder.Services.AddSingleton(geminiClientAudio);
        builder.Services.AddSingleton(geminiClientVision);
        builder.Services.AddSingleton(ragProviderService);

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/" || context.Request.Path == "/index.html")
            {
                var tokenValue = context.Request.Query["t"].ToString();
                if (!tokenAuth.IsValid(tokenValue))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Invalid token");
                    return;
                }
            }

            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                var headers = context.Context.Response.Headers;
                headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                headers["Pragma"] = "no-cache";
                headers["Expires"] = "0";
            }
        });
        app.MapHub<TeleprompterHub>("/hub");
        app.MapGet("/stream.jpg", async context =>
        {
            var tokenValue = context.Request.Query["t"].ToString();
            if (!tokenAuth.IsValid(tokenValue))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!state.TryGetLatestFrame(out var bytes) || bytes == null)
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
            context.Response.ContentType = "image/jpeg";
            await context.Response.Body.WriteAsync(bytes);
        });

        _app = app;
        _state = state;
        _ragEnabled = ragProviderService.Enabled;
        var ragProviderName = useGeminiFileSearch ? "GeminiFileSearch" : useGeminiEmbeddings ? "GeminiEmbeddings" : "Local";
        LogMessage($"RAG provider: {ragProviderName} (enabled={_ragEnabled})");
        if (!string.IsNullOrWhiteSpace(_outputDeviceId))
        {
            _state.SetOutputDevice(_outputDeviceId);
        }

        await app.StartAsync();
        state.HubContext = app.Services.GetRequiredService<IHubContext<TeleprompterHub>>();
        state.StartScreenStream();
        ragProviderService.ProgressChanged += progress => RagProgress?.Invoke(progress.Percent, progress.Message);
        ragProviderService.StatusChanged += message => RagStatus?.Invoke(message);
        _ = Task.Run(async () =>
        {
            try
            {
                await ragProviderService.InitializeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogMessage($"RAG init failed: {ex.Message}");
                RagStatus?.Invoke($"RAG: error - {ex.Message}");
            }
        });

        var ip = GetLocalIPv4() ?? "127.0.0.1";
        Url = $"http://{ip}:{Port}/?t={token}";
        StatusChanged?.Invoke("Running");
        LogMessage($"Listening on 0.0.0.0:{Port}");
    }

    private static HashSet<string> ParseExtensions(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return set;
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var ext = part.StartsWith('.') ? part : "." + part;
            set.Add(ext.ToLowerInvariant());
        }

        return set;
    }

    private static bool IsGeminiEmbeddingsProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        var value = provider.Trim();
        return value.Equals("GeminiEmbeddings", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("GeminiEmbedding", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Embeddings", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Gemini", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeminiFileSearchProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        var value = provider.Trim();
        return value.Equals("GeminiFileSearch", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("FileSearch", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("GeminiFileSearchTool", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("GeminiFS", StringComparison.OrdinalIgnoreCase);
    }

    public void SetOutputDevice(string? deviceId)
    {
        _outputDeviceId = deviceId;
        _state?.SetOutputDevice(deviceId);
    }

    public void SetInputDevice(string? deviceId)
    {
        _inputDeviceId = deviceId;
        _state?.SetInputDevice(deviceId);
    }

    public Task<AudioProbeResult> ProbeAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (_state == null)
        {
            throw new InvalidOperationException("Server not started.");
        }

        return _state.ProbeAsync(duration, cancellationToken);
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
        }
    }

    public void Dispose()
    {
        _state?.Dispose();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void LogMessage(string message) => Log?.Invoke(message);

    private static int GetAvailablePort(int preferred)
    {
        if (IsPortAvailable(preferred))
        {
            return preferred;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetLocalIPv4()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Ppp)
            .ToList();

        var preferred = all.Where(IsPreferredInterface).ToList();

        var ip = TryGetPrivateIPv4(preferred);
        if (!string.IsNullOrWhiteSpace(ip))
        {
            return ip;
        }

        return TryGetPrivateIPv4(all);
    }

    private static string? TryGetPrivateIPv4(IEnumerable<NetworkInterface> interfaces)
    {
        foreach (var ni in interfaces)
        {
            var ipProps = ni.GetIPProperties();

            var hasGateway = ipProps.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(g.Address) &&
                g.Address.ToString() != "0.0.0.0");

            if (!hasGateway)
            {
                continue;
            }

            foreach (var ua in ipProps.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    continue;
                }

                var address = ua.Address.ToString();
                if (IsPrivateIPv4(address))
                {
                    return address;
                }
            }
        }

        return null;
    }

    private static bool IsPreferredInterface(NetworkInterface ni)
    {
        var type = ni.NetworkInterfaceType;
        var isPhysical = type == NetworkInterfaceType.Wireless80211 ||
                         type == NetworkInterfaceType.Ethernet ||
                         type == NetworkInterfaceType.GigabitEthernet ||
                         type == NetworkInterfaceType.FastEthernetFx ||
                         type == NetworkInterfaceType.FastEthernetT ||
                         type == NetworkInterfaceType.Ethernet3Megabit;

        if (!isPhysical)
        {
            return false;
        }

        var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
        var blocked = new[]
        {
            "virtual", "hyper-v", "vmware", "virtualbox", "tap", "tun", "vpn",
            "wireguard", "wintun", "tailscale", "zerotier", "hamachi",
            "cisco", "anyconnect", "openvpn"
        };

        return !blocked.Any(name.Contains);
    }

    private static bool IsPrivateIPv4(string address)
    {
        if (address.StartsWith("10."))
        {
            return true;
        }

        if (address.StartsWith("192.168."))
        {
            return true;
        }

        if (address.StartsWith("172."))
        {
            var parts = address.Split('.');
            if (parts.Length > 1 && int.TryParse(parts[1], out var second))
            {
                return second >= 16 && second <= 31;
            }
        }

        return false;
    }
}

internal sealed class ServerState : IDisposable
{
    private readonly AudioRecorder _audioRecorder;
    private readonly ScreenshotService _screenshotService;
    private readonly GeminiClient _geminiClientAudio;
    private readonly GeminiClient _geminiClientVision;
    private readonly IRagProvider _ragProvider;
    private readonly TokenAuth _tokenAuth;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly TimeSpan _maxDuration;
    private readonly long _minAudioBytes;
    private readonly int _maxHistory;
    private readonly bool _streamEnabled;
    private readonly int _streamFps;
    private readonly int _streamJpegQuality;
    private readonly int _streamMaxWidth;
    private readonly int _streamMaxHeight;
    private string? _outputDeviceId;
    private string? _inputDeviceId;
    private readonly List<ChatMessage> _history = new();
    private Timer? _maxTimer;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private int _connectedCount;

    public ServerState(
        AudioRecorder audioRecorder,
        ScreenshotService screenshotService,
        GeminiClient geminiClientAudio,
        GeminiClient geminiClientVision,
        IRagProvider ragProvider,
        TokenAuth tokenAuth,
        Action<string> log,
        TimeSpan maxDuration,
        long minAudioBytes,
        int maxHistory,
        bool streamEnabled,
        int streamFps,
        int streamJpegQuality,
        int streamMaxWidth,
        int streamMaxHeight)
    {
        _audioRecorder = audioRecorder;
        _screenshotService = screenshotService;
        _geminiClientAudio = geminiClientAudio;
        _geminiClientVision = geminiClientVision;
        _ragProvider = ragProvider;
        _tokenAuth = tokenAuth;
        _log = log;
        _maxDuration = maxDuration;
        _minAudioBytes = minAudioBytes;
        _maxHistory = maxHistory;
        _streamEnabled = streamEnabled;
        _streamFps = Math.Clamp(streamFps, 1, 30);
        _streamJpegQuality = Math.Clamp(streamJpegQuality, 40, 95);
        _streamMaxWidth = Math.Clamp(streamMaxWidth, 320, 3840);
        _streamMaxHeight = Math.Clamp(streamMaxHeight, 240, 2160);
    }

    public bool IsRecording { get; private set; }
    public string? ActiveConnectionId { get; private set; }
    public LastRequest? LastRequest { get; private set; }
    public IHubContext<TeleprompterHub>? HubContext { get; set; }
    public TokenAuth TokenAuth => _tokenAuth;
    public bool IsApiConfigured => _geminiClientAudio.IsConfigured;
    public int MemoryCount => _history.Count;
    public int StreamFps => _streamFps;
    public bool StreamEnabled => _streamEnabled;

    private readonly object _frameLock = new();
    private byte[]? _latestFrame;
    private long _latestFrameId;

    public void RegisterConnection()
    {
        Interlocked.Increment(ref _connectedCount);
    }

    public void UnregisterConnection()
    {
        Interlocked.Decrement(ref _connectedCount);
    }

    public void StartScreenStream()
    {
        if (!_streamEnabled)
        {
            _log("Screen streaming disabled.");
            return;
        }

        if (HubContext == null)
        {
            _log("Screen streaming not started: hub context is missing.");
            return;
        }

        if (_streamTask != null)
        {
            return;
        }

        _streamCts = new CancellationTokenSource();
        _streamTask = Task.Run(() => ScreenStreamLoopAsync(_streamCts.Token));
    }

    public async Task StartRecordingAsync(string connectionId, IClientProxy caller)
    {
        await _operationLock.WaitAsync();
        try
        {
            if (!_geminiClientAudio.IsConfigured)
            {
                await caller.SendAsync("error", new { message = "GEMINI_API_KEY is not set on the PC.", retryable = false });
                return;
            }

            if (IsRecording)
            {
                await caller.SendAsync("error", new { message = "Recording already in progress.", retryable = false });
                return;
            }

            var sessionFolder = CreateSessionFolder();
            _audioRecorder.Start(sessionFolder, _outputDeviceId, _inputDeviceId);
            IsRecording = true;
            ActiveConnectionId = connectionId;
            _maxTimer = new Timer(_ => _ = AutoStopAsync(), null, _maxDuration, Timeout.InfiniteTimeSpan);
            await caller.SendAsync("recording", new { isRecording = true });
            _log("Recording started");
        }
        catch (Exception ex)
        {
            _log($"Recording start failed: {ex.Message}");
            await caller.SendAsync("error", new { message = ex.Message, retryable = false });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopRecordingAndProcessAsync(string connectionId, IClientProxy caller, string? reason = null)
    {
        await _operationLock.WaitAsync();
        try
        {
            if (!IsRecording)
            {
                await caller.SendAsync("error", new { message = "No active recording.", retryable = false });
                return;
            }

            _maxTimer?.Dispose();
            _maxTimer = null;
            IsRecording = false;

            var result = await _audioRecorder.StopAsync().ConfigureAwait(false);
            LastRequest = LastRequest.ForAudio(result.MicPath, result.SystemPath);

            await caller.SendAsync("recording", new { isRecording = false });
            if (!string.IsNullOrWhiteSpace(reason))
            {
                await caller.SendAsync("status", new { message = reason });
            }

            var micSilent = IsAudioTooSmall(result.MicPath, "mic");
            var sysSilent = IsAudioTooSmall(result.SystemPath, "system");

            _log("Generating answer from audio...");
            var ragPayload = GetRagPayload(BuildHistoryQuery(), _ragProvider.UseForAudio);
            var answer = await _geminiClientAudio
                .GetAnswerFromAudioAsync(result.MicPath, result.SystemPath, _history, micSilent, sysSilent, ragPayload?.Context, ragPayload?.Tools)
                .ConfigureAwait(false);

            var userEntry = "Audio call: user requested a live reply based on mic + system audio.";
            AddToHistory("user", userEntry);
            AddToHistory("model", answer);
            await caller.SendAsync("answer", new { text = answer });
            await SendMemoryAsync(caller);
            _log("Answer ready");
        }
        catch (Exception ex)
        {
            _log($"Stop/process failed: {ex.Message}");
            await caller.SendAsync("error", new { message = ex.Message, retryable = LastRequest != null });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task ScreenshotAndProcessAsync(string connectionId, IClientProxy caller)
    {
        await _operationLock.WaitAsync();
        try
        {
            if (!_geminiClientAudio.IsConfigured)
            {
                await caller.SendAsync("error", new { message = "GEMINI_API_KEY is not set on the PC.", retryable = false });
                return;
            }

            if (IsRecording)
            {
                await caller.SendAsync("error", new { message = "Stop recording before taking a screenshot.", retryable = false });
                return;
            }

            var sessionFolder = CreateSessionFolder();
            var path = Path.Combine(sessionFolder, "screenshot.png");
            _screenshotService.CapturePng(path);

            LastRequest = LastRequest.ForScreenshot(path);

            _log("Screenshot captured, analyzing...");
            var ragPayload = GetRagPayload(BuildHistoryQuery(), _ragProvider.UseForScreenshot);
            var answer = await _geminiClientVision.GetAnswerFromScreenshotAsync(path, _history, ragPayload?.Context, ragPayload?.Tools).ConfigureAwait(false);
            AddToHistory("user", "Screenshot: user requested a response based on the current screen.");
            AddToHistory("model", answer);
            await caller.SendAsync("answer", new { text = answer });
            await SendMemoryAsync(caller);
            _log("Answer ready");
        }
        catch (Exception ex)
        {
            _log($"Screenshot failed: {ex.Message}");
            await caller.SendAsync("error", new { message = ex.Message, retryable = LastRequest != null });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task FollowUpAsync(string connectionId, IClientProxy caller, string text)
    {
        await _operationLock.WaitAsync();
        try
        {
            _log("Follow-up request received.");
            if (!_geminiClientAudio.IsConfigured)
            {
                await caller.SendAsync("error", new { message = "GEMINI_API_KEY is not set on the PC.", retryable = false });
                return;
            }

            if (IsRecording)
            {
                await caller.SendAsync("error", new { message = "Stop recording before sending a follow-up.", retryable = false });
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                await caller.SendAsync("error", new { message = "Please enter follow-up text.", retryable = false });
                return;
            }

            var trimmed = text.Trim();

            _log("Generating follow-up answer...");
            await caller.SendAsync("status", new { message = "Processing follow-up..." });
            var ragPayload = GetRagPayload(trimmed, _ragProvider.UseForFollowUp);
            var answer = await _geminiClientAudio.GetAnswerFromFollowUpAsync(trimmed, _history, ragPayload?.Context, ragPayload?.Tools).ConfigureAwait(false);
            AddToHistory("user", trimmed);
            AddToHistory("model", answer);
            await caller.SendAsync("answer", new { text = answer });
            await SendMemoryAsync(caller);
            _log("Answer ready");
        }
        catch (Exception ex)
        {
            _log($"Follow-up failed: {ex.Message}");
            await caller.SendAsync("error", new { message = ex.Message, retryable = false });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task ClearMemoryAsync(IClientProxy caller)
    {
        await _operationLock.WaitAsync();
        try
        {
            _history.Clear();
            await SendMemoryAsync(caller);
            await caller.SendAsync("status", new { message = "Memory cleared." });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RetryAsync(string connectionId, IClientProxy caller)
    {
        await _operationLock.WaitAsync();
        try
        {
            if (!_geminiClientAudio.IsConfigured)
            {
                await caller.SendAsync("error", new { message = "GEMINI_API_KEY is not set on the PC.", retryable = false });
                return;
            }

            if (LastRequest == null)
            {
                await caller.SendAsync("error", new { message = "Nothing to retry.", retryable = false });
                return;
            }

            if (LastRequest.Type == LastRequestType.Audio && LastRequest.MicPath != null && LastRequest.SystemPath != null)
            {
                _log("Retrying audio answer...");
                var micSilent = IsAudioTooSmall(LastRequest.MicPath, "mic");
                var sysSilent = IsAudioTooSmall(LastRequest.SystemPath, "system");
                var ragPayload = GetRagPayload(BuildHistoryQuery(), _ragProvider.UseForAudio);
                var answer = await _geminiClientAudio
                    .GetAnswerFromAudioAsync(LastRequest.MicPath, LastRequest.SystemPath, _history, micSilent, sysSilent, ragPayload?.Context, ragPayload?.Tools)
                    .ConfigureAwait(false);
                var userEntry = "Audio call: user requested a live reply based on mic + system audio.";
                AddToHistory("user", userEntry);
                AddToHistory("model", answer);
                await caller.SendAsync("answer", new { text = answer });
                await SendMemoryAsync(caller);
                _log("Answer ready");
                return;
            }

            if (LastRequest.Type == LastRequestType.Screenshot && LastRequest.ScreenshotPath != null)
            {
                _log("Retrying screenshot analysis...");
                var ragPayload = GetRagPayload(BuildHistoryQuery(), _ragProvider.UseForScreenshot);
                var answer = await _geminiClientVision.GetAnswerFromScreenshotAsync(LastRequest.ScreenshotPath, _history, ragPayload?.Context, ragPayload?.Tools).ConfigureAwait(false);
                AddToHistory("model", answer);
                await caller.SendAsync("answer", new { text = answer });
                await SendMemoryAsync(caller);
                _log("Answer ready");
                return;
            }

            await caller.SendAsync("error", new { message = "Retry failed: missing data.", retryable = false });
        }
        catch (Exception ex)
        {
            _log($"Retry failed: {ex.Message}");
            await caller.SendAsync("error", new { message = ex.Message, retryable = LastRequest != null });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void ClearConnection(string connectionId)
    {
        if (ActiveConnectionId == connectionId)
        {
            ActiveConnectionId = null;
        }
    }

    private async Task AutoStopAsync()
    {
        if (HubContext == null || ActiveConnectionId == null)
        {
            return;
        }

        await _operationLock.WaitAsync();
        try
        {
            if (!IsRecording)
            {
                return;
            }

            IsRecording = false;
            _maxTimer?.Dispose();
            _maxTimer = null;

            var result = await _audioRecorder.StopAsync().ConfigureAwait(false);
            LastRequest = LastRequest.ForAudio(result.MicPath, result.SystemPath);

            var caller = HubContext.Clients.Client(ActiveConnectionId);
            await caller.SendAsync("recording", new { isRecording = false });
            await caller.SendAsync("status", new { message = "Max recording duration reached (auto-stopped)." });

            _log("Auto-stop triggered, generating answer from audio...");

            var micSilent = IsAudioTooSmall(result.MicPath, "mic");
            var sysSilent = IsAudioTooSmall(result.SystemPath, "system");
            var ragPayload = GetRagPayload(BuildHistoryQuery(), _ragProvider.UseForAudio);
            var answer = await _geminiClientAudio
                .GetAnswerFromAudioAsync(result.MicPath, result.SystemPath, _history, micSilent, sysSilent, ragPayload?.Context, ragPayload?.Tools)
                .ConfigureAwait(false);

            var userEntry = "Audio call: user requested a live reply based on mic + system audio.";
            AddToHistory("user", userEntry);
            AddToHistory("model", answer);
            await caller.SendAsync("answer", new { text = answer });
            await SendMemoryAsync(caller);
            _log("Answer ready");
        }
        catch (Exception ex)
        {
            _log($"Auto-stop failed: {ex.Message}");
            if (HubContext != null && ActiveConnectionId != null)
            {
                await HubContext.Clients.Client(ActiveConnectionId)
                    .SendAsync("error", new { message = ex.Message, retryable = LastRequest != null });
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void AddToHistory(string role, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var normalizedRole = role == "model" ? "model" : "user";
        _history.Add(new ChatMessage(normalizedRole, text));

        if (_maxHistory > 0 && _history.Count > _maxHistory)
        {
            var overflow = _history.Count - _maxHistory;
            _history.RemoveRange(0, overflow);
        }
    }

    private string BuildHistoryQuery()
    {
        if (!_ragProvider.Enabled || _history.Count == 0)
        {
            return string.Empty;
        }

        var maxChars = Math.Max(50, _ragProvider.QueryMaxChars);
        var sb = new StringBuilder();
        for (var i = _history.Count - 1; i >= 0 && sb.Length < maxChars; i--)
        {
            var text = _history[i].Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            var remaining = maxChars - sb.Length;
            if (remaining <= 0)
            {
                break;
            }

            if (text.Length > remaining)
            {
                sb.Append(text.Substring(0, remaining));
                break;
            }

            sb.Append(text);
        }

        return sb.ToString();
    }

    private RagPayload? GetRagPayload(string query, bool useForRequest)
    {
        if (!useForRequest || !_ragProvider.Enabled || !_ragProvider.IsReady)
        {
            return null;
        }

        var effectiveQuery = string.IsNullOrWhiteSpace(query) ? _ragProvider.DefaultQuery : query;
        if (string.IsNullOrWhiteSpace(effectiveQuery))
        {
            return null;
        }

        var payload = _ragProvider.BuildPayload(effectiveQuery);
        if ((payload.Context == null || string.IsNullOrWhiteSpace(payload.Context)) &&
            (payload.Tools == null || payload.Tools.Length == 0))
        {
            return null;
        }

        return payload;
    }

    private Task SendMemoryAsync(IClientProxy caller)
    {
        return caller.SendAsync("memory", new { count = _history.Count });
    }

    private bool IsAudioTooSmall(string path, string label)
    {
        try
        {
            var length = new FileInfo(path).Length;
            if (length < _minAudioBytes)
            {
                _log($"{label} audio too small ({length} bytes). Treating as silence.");
                return true;
            }
        }
        catch (Exception ex)
        {
            _log($"{label} audio size check failed: {ex.Message}. Assuming audio is present.");
        }

        return false;
    }

    private static string CreateSessionFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "InterviewGhost");
        var folder = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public void Dispose()
    {
        _maxTimer?.Dispose();
        StopScreenStream();
        _audioRecorder.Dispose();
    }

    public void SetOutputDevice(string? deviceId)
    {
        _outputDeviceId = deviceId;
        _log($"Output device set to: {deviceId ?? "(default)"}");
    }

    public void SetInputDevice(string? deviceId)
    {
        _inputDeviceId = deviceId;
        _log($"Input device set to: {deviceId ?? "(default)"}");
    }

    public Task<AudioProbeResult> ProbeAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        return _audioRecorder.ProbeAsync(_inputDeviceId, _outputDeviceId, duration, cancellationToken);
    }

    private void StopScreenStream()
    {
        try
        {
            _streamCts?.Cancel();
        }
        catch
        {
        }
    }

    private async Task ScreenStreamLoopAsync(CancellationToken token)
    {
        var delay = TimeSpan.FromMilliseconds(1000.0 / _streamFps);
        _log($"Screen streaming started ({_streamFps} fps, jpeg quality {_streamJpegQuality}).");

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (Volatile.Read(ref _connectedCount) <= 0)
                {
                    await Task.Delay(500, token);
                    continue;
                }

                var sw = Stopwatch.StartNew();
                var bytes = _screenshotService.CaptureJpegBytes(_streamJpegQuality, _streamMaxWidth, _streamMaxHeight, out var width, out var height);
                lock (_frameLock)
                {
                    _latestFrame = bytes;
                    _latestFrameId++;
                }

                var remaining = delay - sw.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"Screen stream failed: {ex.Message}");
                await Task.Delay(500, token);
            }
        }

        _log("Screen streaming stopped.");
    }

    public bool TryGetLatestFrame(out byte[]? bytes)
    {
        lock (_frameLock)
        {
            bytes = _latestFrame;
            return bytes != null && bytes.Length > 0;
        }
    }
}

internal sealed class TeleprompterHub : Hub
{
    private readonly ServerState _state;

    public TeleprompterHub(ServerState state)
    {
        _state = state;
    }

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query["t"].ToString();
        if (!_state.TokenAuth.IsValid(token))
        {
            Context.Abort();
            return;
        }

        _state.RegisterConnection();
        await Clients.Caller.SendAsync("state", new
        {
            isRecording = _state.IsRecording,
            hasKey = _state.IsApiConfigured,
            streamEnabled = _state.StreamEnabled,
            streamFps = _state.StreamFps
        });
        await Clients.Caller.SendAsync("memory", new { count = _state.MemoryCount });
        if (!_state.IsApiConfigured)
        {
            await Clients.Caller.SendAsync("error", new { message = "GEMINI_API_KEY is not set on the PC.", retryable = false });
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _state.ClearConnection(Context.ConnectionId);
        _state.UnregisterConnection();
        return base.OnDisconnectedAsync(exception);
    }

    public Task StartRecording() => _state.StartRecordingAsync(Context.ConnectionId, Clients.Caller);

    public Task StopRecording() => _state.StopRecordingAndProcessAsync(Context.ConnectionId, Clients.Caller);

    public Task Screenshot() => _state.ScreenshotAndProcessAsync(Context.ConnectionId, Clients.Caller);

    public Task Retry() => _state.RetryAsync(Context.ConnectionId, Clients.Caller);

    public Task FollowUp(string text) => _state.FollowUpAsync(Context.ConnectionId, Clients.Caller, text);

    public Task ClearMemory() => _state.ClearMemoryAsync(Clients.Caller);
}

internal sealed class LastRequest
{
    private LastRequest(LastRequestType type, string? micPath, string? systemPath, string? screenshotPath)
    {
        Type = type;
        MicPath = micPath;
        SystemPath = systemPath;
        ScreenshotPath = screenshotPath;
    }

    public LastRequestType Type { get; }
    public string? MicPath { get; }
    public string? SystemPath { get; }
    public string? ScreenshotPath { get; }

    public static LastRequest ForAudio(string micPath, string systemPath) =>
        new(LastRequestType.Audio, micPath, systemPath, null);

    public static LastRequest ForScreenshot(string screenshotPath) =>
        new(LastRequestType.Screenshot, null, null, screenshotPath);
}

internal enum LastRequestType
{
    Audio,
    Screenshot
}
