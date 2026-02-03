using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopAgentWpf.Services;

public sealed class GeminiEmbeddingsService : IRagProvider
{
    private readonly GeminiEmbeddingsOptions _options;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private List<EmbeddingChunk> _chunks = new();
    private bool _ready;

    public GeminiEmbeddingsService(string? apiKey, GeminiEmbeddingsOptions options, Action<string>? log)
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
                StatusChanged?.Invoke("RAG: Gemini Embeddings needs GEMINI_API_KEY.");
                return;
            }

            var folder = ResolveFolder(_options.Folder);
            _log?.Invoke($"RAG folder resolved to: {folder}");
            if (!Directory.Exists(folder))
            {
                StatusChanged?.Invoke($"RAG: folder not found: {folder}");
                return;
            }

            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Where(IsAllowedFile)
                .Take(_options.MaxFiles)
                .ToList();

            if (files.Count == 0)
            {
                StatusChanged?.Invoke("RAG: no files to index");
                return;
            }

            StatusChanged?.Invoke($"RAG: embedding {files.Count} files...");
            ProgressChanged?.Invoke(new RagProgress(0, "Starting..."));

            _chunks = new List<EmbeddingChunk>();
            var bufferTexts = new List<string>();
            var bufferSources = new List<string>();

            var totalFiles = files.Count;
            for (var i = 0; i < totalFiles; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[i];
                var fileName = Path.GetFileName(file);
                ProgressChanged?.Invoke(new RagProgress(ToPercent(i, totalFiles), $"Indexing {fileName} ({i + 1}/{totalFiles})"));

                var text = await ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                foreach (var chunk in ChunkText(text, _options.ChunkChars, _options.ChunkOverlap))
                {
                    bufferTexts.Add(chunk);
                    bufferSources.Add(fileName);

                    if (bufferTexts.Count >= _options.BatchSize)
                    {
                        await EmbedBatchAsync(bufferTexts, bufferSources, cancellationToken).ConfigureAwait(false);
                        bufferTexts.Clear();
                        bufferSources.Clear();
                    }
                }
            }

            if (bufferTexts.Count > 0)
            {
                await EmbedBatchAsync(bufferTexts, bufferSources, cancellationToken).ConfigureAwait(false);
                bufferTexts.Clear();
                bufferSources.Clear();
            }

            if (_chunks.Count == 0)
            {
                StatusChanged?.Invoke("RAG: no chunks created (empty files)");
                return;
            }

            _ready = true;
            ProgressChanged?.Invoke(new RagProgress(100, "Ready"));
            StatusChanged?.Invoke($"RAG: ready ({_chunks.Count} chunks)");
            _log?.Invoke($"RAG embeddings indexed {_chunks.Count} chunks from {files.Count} files.");
        }
        catch (Exception ex)
        {
            _ready = false;
            _log?.Invoke($"RAG embeddings init error: {ex.Message}");
            StatusChanged?.Invoke($"RAG: error - {ex.Message}");
        }
    }

    public RagPayload BuildPayload(string? query)
    {
        if (!Enabled || !_ready || _chunks.Count == 0)
        {
            return new RagPayload(null, null);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new RagPayload(null, null);
        }

        var embedding = EmbedQueryAsync(query, CancellationToken.None).GetAwaiter().GetResult();
        if (embedding == null || embedding.Length == 0)
        {
            return new RagPayload(null, null);
        }

        var scored = new List<(EmbeddingChunk chunk, float score)>(_chunks.Count);
        foreach (var chunk in _chunks)
        {
            var score = Dot(embedding, chunk.Vector);
            if (score >= _options.MinScore)
            {
                scored.Add((chunk, score));
            }
        }

        var top = scored.OrderByDescending(s => s.score).Take(_options.TopK).ToList();
        if (top.Count == 0)
        {
            return new RagPayload(null, null);
        }

        var sb = new StringBuilder();
        foreach (var (chunk, score) in top)
        {
            var header = $"Source: {chunk.Source} (score {score:F2})";
            if (sb.Length + header.Length + 2 > _options.MaxContextChars)
            {
                break;
            }

            sb.AppendLine(header);
            var remaining = _options.MaxContextChars - sb.Length - 2;
            var text = chunk.Text.Length > remaining ? chunk.Text.Substring(0, remaining) : chunk.Text;
            sb.AppendLine(text);
            sb.AppendLine("---");

            if (sb.Length >= _options.MaxContextChars)
            {
                break;
            }
        }

        var context = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(context) ? new RagPayload(null, null) : new RagPayload(context, null);
    }

    private async Task EmbedBatchAsync(List<string> texts, List<string> sources, CancellationToken cancellationToken)
    {
        var vectors = await EmbedAsync(texts, sources, _options.TaskTypeDocument, cancellationToken).ConfigureAwait(false);
        if (vectors.Count != texts.Count)
        {
            _log?.Invoke($"RAG embeddings batch mismatch: expected {texts.Count}, got {vectors.Count}");
        }

        var count = Math.Min(texts.Count, vectors.Count);
        for (var i = 0; i < count; i++)
        {
            _chunks.Add(new EmbeddingChunk(sources[i], texts[i], vectors[i]));
        }
    }

    private async Task<float[]?> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        var list = await EmbedAsync(new List<string> { query }, null, _options.TaskTypeQuery, cancellationToken).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    private async Task<List<float[]>> EmbedAsync(List<string> texts, List<string>? titles, string taskType, CancellationToken cancellationToken)
    {
        if (texts.Count == 1)
        {
            var payload = new Dictionary<string, object>
            {
                ["model"] = $"models/{_options.Model}",
                ["content"] = new Dictionary<string, object>
                {
                    ["parts"] = new object[] { new Dictionary<string, object> { ["text"] = texts[0] } }
                },
                ["taskType"] = taskType
            };

            if (_options.OutputDimensionality > 0)
            {
                payload["outputDimensionality"] = _options.OutputDimensionality;
            }

            if (string.Equals(taskType, "RETRIEVAL_DOCUMENT", StringComparison.OrdinalIgnoreCase) &&
                titles != null && titles.Count > 0 && !string.IsNullOrWhiteSpace(titles[0]))
            {
                payload["title"] = titles[0];
            }

            var json = JsonSerializer.Serialize(payload);
            var sw = Stopwatch.StartNew();
            using var response = await _http.PostAsync(
                $"models/{_options.Model}:embedContent",
                new StringContent(json, Encoding.UTF8, "application/json"),
                cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ExtractError(body));
            }

            var vectors = ExtractEmbeddings(body);
            if (vectors.Count == 0)
            {
                var single = ExtractSingleEmbedding(body);
                if (single.Length > 0)
                {
                    vectors = new List<float[]> { single };
                }
            }

            _log?.Invoke($"RAG embeddings ({taskType}) in {sw.Elapsed.TotalMilliseconds:F0} ms, items={vectors.Count}");
            return vectors;
        }

        var requests = new List<object>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var request = new Dictionary<string, object>
            {
                ["model"] = $"models/{_options.Model}",
                ["content"] = new Dictionary<string, object>
                {
                    ["parts"] = new object[] { new Dictionary<string, object> { ["text"] = texts[i] } }
                },
                ["taskType"] = taskType
            };

            if (_options.OutputDimensionality > 0)
            {
                request["outputDimensionality"] = _options.OutputDimensionality;
            }

            if (string.Equals(taskType, "RETRIEVAL_DOCUMENT", StringComparison.OrdinalIgnoreCase) &&
                titles != null && i < titles.Count && !string.IsNullOrWhiteSpace(titles[i]))
            {
                request["title"] = titles[i];
            }

            requests.Add(request);
        }

        var batchPayload = new Dictionary<string, object>
        {
            ["requests"] = requests
        };

        var batchJson = JsonSerializer.Serialize(batchPayload);
        var batchSw = Stopwatch.StartNew();
        using var batchResponse = await _http.PostAsync(
            $"models/{_options.Model}:batchEmbedContents",
            new StringContent(batchJson, Encoding.UTF8, "application/json"),
            cancellationToken).ConfigureAwait(false);
        var batchBody = await batchResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        batchSw.Stop();

        if (!batchResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractError(batchBody));
        }

        var batchVectors = ExtractEmbeddings(batchBody);
        _log?.Invoke($"RAG embeddings batch ({taskType}) in {batchSw.Elapsed.TotalMilliseconds:F0} ms, items={batchVectors.Count}");
        if (batchVectors.Count == 0)
        {
            _log?.Invoke($"RAG embeddings batch response: {Truncate(batchBody)}");
        }

        return batchVectors;
    }

    private static List<float[]> ExtractEmbeddings(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("embeddings", out var embeddings))
        {
            return new List<float[]>();
        }

        var list = new List<float[]>();
        foreach (var embedding in embeddings.EnumerateArray())
        {
            if (!embedding.TryGetProperty("values", out var values))
            {
                continue;
            }

            var vector = new float[values.GetArrayLength()];
            var idx = 0;
            foreach (var value in values.EnumerateArray())
            {
                vector[idx++] = value.GetSingle();
            }

            Normalize(vector);
            list.Add(vector);
        }

        return list;
    }

    private static float[] ExtractSingleEmbedding(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("embedding", out var embedding))
        {
            return Array.Empty<float>();
        }

        if (!embedding.TryGetProperty("values", out var values))
        {
            return Array.Empty<float>();
        }

        var vector = new float[values.GetArrayLength()];
        var idx = 0;
        foreach (var value in values.EnumerateArray())
        {
            vector[idx++] = value.GetSingle();
        }

        Normalize(vector);
        return vector;
    }

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }

        if (sum <= 0)
        {
            return;
        }

        var inv = 1.0 / Math.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] * inv);
        }
    }

    private static float Dot(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < len; i++)
        {
            sum += a[i] * b[i];
        }

        return (float)sum;
    }

    private string ResolveFolder(string folder)
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
            or ".cs" or ".py" or ".js" or ".ts" or ".java" or ".cpp" or ".h" or ".hpp";
    }

    private async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (_options.MaxFileSizeMb > 0 && info.Length > _options.MaxFileSizeMb * 1024L * 1024L)
            {
                _log?.Invoke($"RAG skip large file: {info.Name} ({info.Length} bytes)");
                return string.Empty;
            }

            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"RAG read failed: {Path.GetFileName(path)} - {ex.Message}");
            return string.Empty;
        }
    }

    private static IEnumerable<string> ChunkText(string text, int chunkChars, int overlap)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        if (chunkChars <= 0)
        {
            yield return text.Trim();
            yield break;
        }

        var cleaned = Regex.Replace(text, @"\s+", " ").Trim();
        if (cleaned.Length <= chunkChars)
        {
            yield return cleaned;
            yield break;
        }

        var step = Math.Max(1, chunkChars - overlap);
        for (var i = 0; i < cleaned.Length; i += step)
        {
            var length = Math.Min(chunkChars, cleaned.Length - i);
            var chunk = cleaned.Substring(i, length).Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                yield return chunk;
            }

            if (i + length >= cleaned.Length)
            {
                yield break;
            }
        }
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

    private static string Truncate(string text, int max = 600)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        return text.Substring(0, max) + "…";
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

    private sealed record EmbeddingChunk(string Source, string Text, float[] Vector);
}

public sealed record GeminiEmbeddingsOptions(
    bool Enabled,
    string Folder,
    int MaxFiles,
    int MaxFileSizeMb,
    int ChunkChars,
    int ChunkOverlap,
    int TopK,
    int MaxContextChars,
    float MinScore,
    int QueryMaxChars,
    string? DefaultQuery,
    bool UseForAudio,
    bool UseForScreenshot,
    bool UseForFollowUp,
    HashSet<string> AllowedExtensions,
    string Model,
    string TaskTypeDocument,
    string TaskTypeQuery,
    int OutputDimensionality,
    int BatchSize);
