using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DesktopAgentWpf.Services;

public sealed class GeminiFileSearchService : IRagProvider
{
    private readonly GeminiFileSearchOptions _options;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private string? _storeName;
    private bool _ready;

    public GeminiFileSearchService(string? apiKey, GeminiFileSearchOptions options, Action<string>? log)
    {
        _apiKey = apiKey;
        _options = options;
        _log = log;

        _http = new HttpClient
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/")
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            _http.DefaultRequestHeaders.Add("x-goog-api-key", _apiKey);
        }
    }

    public bool Enabled => _options.Enabled;
    public bool IsReady => _ready;
    public bool UseForAudio => _options.UseForAudio;
    public bool UseForScreenshot => _options.UseForScreenshot;
    public bool UseForFollowUp => _options.UseForFollowUp;
    public int QueryMaxChars => _options.QueryMaxChars;
    public string? DefaultQuery => _options.DefaultQuery;

    public event Action<RagProgress>? ProgressChanged;
    public event Action<string>? StatusChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Enabled)
            {
                StatusChanged?.Invoke("RAG: disabled");
                return;
            }

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                StatusChanged?.Invoke("RAG: Gemini File Search needs GEMINI_API_KEY.");
                return;
            }

            var rawStoreName = _options.StoreName?.Trim();
            var displayName = _options.StoreDisplayName;
            _storeName = NormalizeStoreName(rawStoreName);
        if (string.IsNullOrWhiteSpace(_storeName))
        {
            if (!string.IsNullOrWhiteSpace(rawStoreName))
            {
                displayName = rawStoreName;
                    _log?.Invoke($"RAG Gemini: store name is not a valid resource id. Will create a new store with displayName='{displayName}'.");
                }

                _log?.Invoke("RAG Gemini: creating File Search store...");
            _storeName = await CreateStoreAsync(displayName, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke($"RAG: Gemini store created: {_storeName}");
            _log?.Invoke($"RAG Gemini store created: {_storeName}");
        }
        else
        {
            StatusChanged?.Invoke($"RAG: Gemini store: {_storeName}");
            _log?.Invoke($"RAG Gemini store: {_storeName}");
        }

        if (!_options.UploadOnStart)
        {
            _ready = true;
            StatusChanged?.Invoke("RAG: Gemini File Search ready (upload skipped).");
            await TryLogStoreStatsAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

            var folder = ResolveFolder(_options.Folder);
            _log?.Invoke($"RAG folder resolved to: {folder}");
            if (!Directory.Exists(folder))
            {
                StatusChanged?.Invoke($"RAG: folder not found: {folder}");
                _ready = true;
                return;
            }

            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Where(IsAllowedFile)
                .Take(_options.MaxFiles)
                .ToList();

            if (files.Count == 0)
            {
                StatusChanged?.Invoke("RAG: no files to upload");
                _ready = true;
                return;
            }

            StatusChanged?.Invoke($"RAG: uploading {files.Count} files to Gemini store...");
            ProgressChanged?.Invoke(new RagProgress(0, "Starting upload..."));

            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[i];
                var fileName = Path.GetFileName(file);
                ProgressChanged?.Invoke(new RagProgress(ToPercent(i, files.Count), $"Uploading {fileName} ({i + 1}/{files.Count})"));
                _log?.Invoke($"RAG Gemini uploading: {fileName}");

                var bytes = await ReadBytesAsync(file, cancellationToken).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0)
                {
                    continue;
                }

                var mime = GetMimeType(file);
                await UploadToStoreAsync(_storeName!, bytes, mime, fileName, cancellationToken).ConfigureAwait(false);
            }

            await WaitForDocumentsActiveAsync(_storeName!, cancellationToken).ConfigureAwait(false);
            ProgressChanged?.Invoke(new RagProgress(100, "Ready"));
            StatusChanged?.Invoke("RAG: Gemini File Search ready.");
            _ready = true;
            await TryLogStoreStatsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ready = false;
            _log?.Invoke($"RAG Gemini init error: {ex.Message}");
            StatusChanged?.Invoke($"RAG: error - {ex.Message}");
        }
    }

    public RagPayload BuildPayload(string? query)
    {
        if (!Enabled || !_ready || string.IsNullOrWhiteSpace(_storeName))
        {
            return new RagPayload(null, null);
        }

        var fileSearch = new Dictionary<string, object>
        {
            ["file_search_store_names"] = new[] { _storeName }
        };

        if (_options.TopK > 0)
        {
            _log?.Invoke("RAG Gemini note: top_k is not set for File Search (not supported by REST tools).");
        }

        if (!string.IsNullOrWhiteSpace(_options.MetadataFilter))
        {
            fileSearch["metadata_filter"] = _options.MetadataFilter.Trim();
        }

        var tool = new Dictionary<string, object>
        {
            ["file_search"] = fileSearch
        };

        return new RagPayload(null, new object[] { tool });
    }

    private async Task<string> CreateStoreAsync(string displayName, CancellationToken cancellationToken)
    {
        var payload = new { displayName = displayName };
        var json = JsonSerializer.Serialize(payload);
        using var response = await _http.PostAsync(
            "fileSearchStores",
            new StringContent(json, Encoding.UTF8, "application/json"),
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractError(body));
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("name", out var nameElement))
        {
            throw new InvalidOperationException("Gemini File Search store creation did not return a name.");
        }

        var name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Gemini File Search store creation returned an empty name.");
        }

        return name;
    }

    private async Task UploadToStoreAsync(string storeName, byte[] bytes, string mimeType, string displayName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("GEMINI_API_KEY is not set.");
        }

        var startRequest = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/upload/v1beta/{storeName}:uploadToFileSearchStore");
        startRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        startRequest.Headers.Add("X-Goog-Upload-Command", "start");
        startRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", bytes.Length.ToString());
        startRequest.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);

        var body = new Dictionary<string, object>
        {
            ["displayName"] = displayName,
            ["mimeType"] = mimeType
        };

        var chunking = BuildChunkingConfig();
        if (chunking != null)
        {
            body["chunkingConfig"] = chunking;
        }

        startRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var startResponse = await _http.SendAsync(startRequest, cancellationToken).ConfigureAwait(false);
        var startBody = await startResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!startResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractError(startBody));
        }

        if (!startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var urls))
        {
            throw new InvalidOperationException("Gemini File Search upload URL not returned.");
        }

        var uploadUrl = urls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            throw new InvalidOperationException("Gemini File Search upload URL not returned.");
        }

        var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
        uploadRequest.Content = new ByteArrayContent(bytes);
        uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        uploadRequest.Content.Headers.ContentLength = bytes.Length;

        using var uploadResponse = await _http.SendAsync(uploadRequest, cancellationToken).ConfigureAwait(false);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractError(uploadBody));
        }

        var op = ParseOperation(uploadBody);
        if (!string.IsNullOrWhiteSpace(op.Error))
        {
            throw new InvalidOperationException(op.Error);
        }

        if (!op.Done && !string.IsNullOrWhiteSpace(op.Name))
        {
            _log?.Invoke($"RAG Gemini waiting for indexing: {op.Name}");
            await WaitOperationAsync(op.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitOperationAsync(string name, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < _options.OperationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(_options.OperationPollInterval, cancellationToken).ConfigureAwait(false);

            using var response = await _http.GetAsync(name, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ExtractError(body));
            }

            var op = ParseOperation(body);
            if (!string.IsNullOrWhiteSpace(op.Error))
            {
                throw new InvalidOperationException(op.Error);
            }

            if (op.Done)
            {
                _log?.Invoke($"RAG Gemini indexing done: {name}");
                return;
            }
        }

        throw new TimeoutException("Gemini File Search indexing timed out.");
    }

    private async Task WaitForDocumentsActiveAsync(string storeName, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < _options.OperationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await GetStoreDocumentStatsAsync(storeName, cancellationToken).ConfigureAwait(false);
            if (status != null)
            {
                _log?.Invoke($"RAG Gemini docs: active={status.Value.active}, pending={status.Value.pending}, failed={status.Value.failed}");
                if (status.Value.pending == 0)
                {
                    return;
                }
            }

            await Task.Delay(_options.OperationPollInterval, cancellationToken).ConfigureAwait(false);
        }

        _log?.Invoke("RAG Gemini docs: pending after timeout, continuing anyway.");
    }

    private async Task TryLogStoreStatsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_storeName))
        {
            return;
        }

        try
        {
            using var response = await _http.GetAsync(_storeName, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke($"RAG Gemini store stats error: {ExtractError(body)}");
                return;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var active = root.TryGetProperty("activeDocumentsCount", out var activeElem) ? activeElem.GetString() : null;
            var pending = root.TryGetProperty("pendingDocumentsCount", out var pendingElem) ? pendingElem.GetString() : null;
            var failed = root.TryGetProperty("failedDocumentsCount", out var failedElem) ? failedElem.GetString() : null;
            var size = root.TryGetProperty("sizeBytes", out var sizeElem) ? sizeElem.GetString() : null;
            _log?.Invoke($"RAG Gemini store stats: active={active ?? "?"}, pending={pending ?? "?"}, failed={failed ?? "?"}, sizeBytes={size ?? "?"}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"RAG Gemini store stats failed: {ex.Message}");
        }
    }

    private async Task<(int active, int pending, int failed)?> GetStoreDocumentStatsAsync(string storeName, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync($"{storeName}/documents?pageSize=20", cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke($"RAG Gemini list documents error: {ExtractError(body)}");
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("documents", out var docs) || docs.ValueKind != JsonValueKind.Array)
            {
                return (0, 0, 0);
            }

            var active = 0;
            var pending = 0;
            var failed = 0;
            foreach (var d in docs.EnumerateArray())
            {
                var state = d.TryGetProperty("state", out var stateElem) ? stateElem.GetString() : null;
                switch (state)
                {
                    case "STATE_ACTIVE":
                        active++;
                        break;
                    case "STATE_PENDING":
                        pending++;
                        break;
                    case "STATE_FAILED":
                        failed++;
                        break;
                }
            }

            return (active, pending, failed);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"RAG Gemini list documents failed: {ex.Message}");
            return null;
        }
    }

    private object? BuildChunkingConfig()
    {
        if (_options.ChunkMaxTokensPerChunk <= 0 && _options.ChunkMaxOverlapTokens <= 0)
        {
            return null;
        }

        var whiteSpace = new Dictionary<string, object>();
        if (_options.ChunkMaxTokensPerChunk > 0)
        {
            whiteSpace["maxTokensPerChunk"] = _options.ChunkMaxTokensPerChunk;
        }

        if (_options.ChunkMaxOverlapTokens > 0)
        {
            whiteSpace["maxOverlapTokens"] = _options.ChunkMaxOverlapTokens;
        }

        return new Dictionary<string, object>
        {
            ["whiteSpaceConfig"] = whiteSpace
        };
    }

    private static OperationInfo ParseOperation(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var done = root.TryGetProperty("done", out var doneElement) && doneElement.GetBoolean();
            string? error = null;
            if (root.TryGetProperty("error", out var errorElement) &&
                errorElement.TryGetProperty("message", out var messageElement))
            {
                error = messageElement.GetString();
            }

            return new OperationInfo(name, done, error);
        }
        catch
        {
            return new OperationInfo(null, true, null);
        }
    }

    private static string NormalizeStoreName(string? storeName)
    {
        if (string.IsNullOrWhiteSpace(storeName))
        {
            return string.Empty;
        }

        var trimmed = storeName.Trim();
        if (trimmed.StartsWith("fileSearchStores/", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = trimmed.Substring("fileSearchStores/".Length);
            return IsValidStoreId(suffix) ? "fileSearchStores/" + suffix : string.Empty;
        }

        return IsValidStoreId(trimmed) ? "fileSearchStores/" + trimmed : string.Empty;
    }

    private static bool IsValidStoreId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (id.Length > 40)
        {
            return false;
        }

        foreach (var ch in id)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string ResolveFolder(string folder)
    {
        if (Path.IsPathRooted(folder))
        {
            return folder;
        }

        var baseCandidate = Path.Combine(AppContext.BaseDirectory, folder);
        if (Directory.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        var cwdCandidate = Path.Combine(Environment.CurrentDirectory, folder);
        if (Directory.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }

        var fromCwd = FindInParents(Environment.CurrentDirectory, folder, 6);
        if (!string.IsNullOrWhiteSpace(fromCwd))
        {
            return fromCwd;
        }

        var fromBase = FindInParents(AppContext.BaseDirectory, folder, 6);
        if (!string.IsNullOrWhiteSpace(fromBase))
        {
            return fromBase;
        }

        return baseCandidate;
    }

    private static string? FindInParents(string start, string folder, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(start))
        {
            return null;
        }

        var dir = new DirectoryInfo(start);
        for (var i = 0; i <= maxDepth && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, folder);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private bool IsAllowedFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length == 0)
        {
            return false;
        }

        if (_options.AllowedExtensions.Count > 0)
        {
            return _options.AllowedExtensions.Contains(ext);
        }

        return ext is ".txt" or ".md" or ".markdown" or ".json" or ".csv" or ".log" or ".yaml" or ".yml" or ".xml" or ".ini"
            or ".cs" or ".py" or ".js" or ".ts" or ".java" or ".cpp" or ".h" or ".hpp" or ".pdf" or ".doc" or ".docx" or ".ppt"
            or ".pptx" or ".xls" or ".xlsx" or ".rtf";
    }

    private async Task<byte[]?> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (_options.MaxFileSizeMb > 0 && info.Length > _options.MaxFileSizeMb * 1024L * 1024L)
            {
                _log?.Invoke($"RAG skip large file: {info.Name} ({info.Length} bytes)");
                return null;
            }

            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"RAG read failed: {Path.GetFileName(path)} - {ex.Message}");
            return null;
        }
    }

    private static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".markdown" => "text/markdown",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".log" => "text/plain",
            ".yaml" => "text/yaml",
            ".yml" => "text/yaml",
            ".xml" => "application/xml",
            ".ini" => "text/plain",
            ".cs" => "text/plain",
            ".py" => "text/plain",
            ".js" => "text/plain",
            ".ts" => "text/plain",
            ".java" => "text/plain",
            ".cpp" => "text/plain",
            ".h" => "text/plain",
            ".hpp" => "text/plain",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".rtf" => "text/rtf",
            _ => "application/octet-stream"
        };
    }

    private static string ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? "Gemini API error";
            }
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(json) ? "Gemini API error" : json;
    }

    private static int ToPercent(int index, int total)
    {
        if (total <= 0)
        {
            return 0;
        }

        var value = (index + 1) * 100.0 / total;
        return (int)Math.Clamp(Math.Round(value), 0, 100);
    }

    private sealed record OperationInfo(string? Name, bool Done, string? Error);
}

public sealed record GeminiFileSearchOptions(
    bool Enabled,
    string? StoreName,
    string StoreDisplayName,
    bool UploadOnStart,
    string Folder,
    int MaxFiles,
    int MaxFileSizeMb,
    int TopK,
    string? MetadataFilter,
    int QueryMaxChars,
    string? DefaultQuery,
    bool UseForAudio,
    bool UseForScreenshot,
    bool UseForFollowUp,
    HashSet<string> AllowedExtensions,
    TimeSpan OperationTimeout,
    TimeSpan OperationPollInterval,
    int ChunkMaxTokensPerChunk,
    int ChunkMaxOverlapTokens);
