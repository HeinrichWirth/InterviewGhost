using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace DesktopAgentWpf.Services;

public sealed class RagService : IRagProvider
{
    private readonly RagOptions _options;
    private readonly Action<string>? _log;
    private RagIndex? _index;

    public RagService(RagOptions options, Action<string>? log)
    {
        _options = options;
        _log = log;
    }

    public bool Enabled => _options.Enabled;
    public bool IsReady => _index != null;
    public bool UseForAudio => _options.UseForAudio;
    public bool UseForScreenshot => _options.UseForScreenshot;
    public bool UseForFollowUp => _options.UseForFollowUp;
    public int QueryMaxChars => _options.QueryMaxChars;
    public string? DefaultQuery => _options.DefaultQuery;

    public event Action<RagProgress>? ProgressChanged;
    public event Action<string>? StatusChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            StatusChanged?.Invoke("RAG: disabled");
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

        StatusChanged?.Invoke($"RAG: indexing {files.Count} files...");
        ProgressChanged?.Invoke(new RagProgress(0, "Starting..."));

        var chunks = new List<RagChunk>();
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[i];
            var fileName = Path.GetFileName(file);
            ProgressChanged?.Invoke(new RagProgress(ToPercent(i, files.Count), $"Indexing {fileName} ({i + 1}/{files.Count})"));

            var text = await ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (var chunkText in ChunkText(text, _options.ChunkChars, _options.ChunkOverlap))
            {
                var tokens = Tokenize(chunkText);
                if (tokens.Count == 0)
                {
                    continue;
                }

                var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var token in tokens)
                {
                    tf.TryGetValue(token, out var count);
                    tf[token] = count + 1;
                }

                var unique = new HashSet<string>(tf.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var term in unique)
                {
                    df.TryGetValue(term, out var count);
                    df[term] = count + 1;
                }

                chunks.Add(new RagChunk(fileName, chunkText, tf));
            }
        }

        if (chunks.Count == 0)
        {
            StatusChanged?.Invoke("RAG: no chunks created (empty files)");
            return;
        }

        var idf = BuildIdf(df, chunks.Count);
        foreach (var chunk in chunks)
        {
            chunk.BuildVector(idf);
        }

        _index = new RagIndex(chunks, idf, _options);
        ProgressChanged?.Invoke(new RagProgress(100, "Ready"));
        StatusChanged?.Invoke($"RAG: ready ({chunks.Count} chunks)");
        _log?.Invoke($"RAG indexed {chunks.Count} chunks from {files.Count} files.");
    }

    public string GetContext(string? query)
    {
        if (!Enabled || _index == null)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        return _index.BuildContext(query);
    }

    public RagPayload BuildPayload(string? query)
    {
        var context = GetContext(query);
        if (string.IsNullOrWhiteSpace(context))
        {
            return new RagPayload(null, null);
        }

        return new RagPayload(context, null);
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

    private static List<string> Tokenize(string text)
    {
        var matches = Regex.Matches(text, @"[\p{L}\p{Nd}]+");
        var tokens = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            var value = match.Value.ToLowerInvariant();
            if (value.Length < 2)
            {
                continue;
            }
            tokens.Add(value);
        }
        return tokens;
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

    private static Dictionary<string, float> BuildIdf(Dictionary<string, int> df, int totalDocs)
    {
        var idf = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, freq) in df)
        {
            var value = (float)(Math.Log((totalDocs + 1.0) / (freq + 1.0)) + 1.0);
            idf[term] = value;
        }
        return idf;
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
}

public sealed record RagProgress(int Percent, string Message);

public sealed record RagOptions(
    bool Enabled,
    string Folder,
    int MaxFiles,
    int MaxFileSizeMb,
    int ChunkChars,
    int ChunkOverlap,
    int TopK,
    int MaxContextChars,
    double MinScore,
    int QueryMaxChars,
    string? DefaultQuery,
    bool UseForAudio,
    bool UseForScreenshot,
    bool UseForFollowUp,
    HashSet<string> AllowedExtensions);

internal sealed class RagIndex
{
    private readonly List<RagChunk> _chunks;
    private readonly Dictionary<string, float> _idf;
    private readonly RagOptions _options;

    public RagIndex(List<RagChunk> chunks, Dictionary<string, float> idf, RagOptions options)
    {
        _chunks = chunks;
        _idf = idf;
        _options = options;
    }

    public string BuildContext(string query)
    {
        var queryVec = BuildQueryVector(query, out var queryNorm);
        if (queryVec.Count == 0 || queryNorm <= 0)
        {
            return string.Empty;
        }

        var scored = new List<(RagChunk chunk, double score)>(_chunks.Count);
        foreach (var chunk in _chunks)
        {
            if (chunk.Norm <= 0)
            {
                continue;
            }

            var score = Dot(queryVec, chunk.Vector) / (queryNorm * chunk.Norm);
            if (score >= _options.MinScore)
            {
                scored.Add((chunk, score));
            }
        }

        var top = scored
            .OrderByDescending(s => s.score)
            .Take(_options.TopK)
            .ToList();

        if (top.Count == 0)
        {
            return string.Empty;
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

        return sb.ToString().Trim();
    }

    private Dictionary<string, float> BuildQueryVector(string query, out double norm)
    {
        var tokens = RagServiceTokenize(query);
        var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            tf.TryGetValue(token, out var count);
            tf[token] = count + 1;
        }

        var vec = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        double sum = 0;
        foreach (var (term, count) in tf)
        {
            if (!_idf.TryGetValue(term, out var idf))
            {
                continue;
            }

            var value = count * idf;
            vec[term] = value;
            sum += value * value;
        }

        norm = Math.Sqrt(sum);
        return vec;
    }

    private static double Dot(Dictionary<string, float> a, Dictionary<string, float> b)
    {
        if (a.Count > b.Count)
        {
            (a, b) = (b, a);
        }

        double sum = 0;
        foreach (var (term, value) in a)
        {
            if (b.TryGetValue(term, out var other))
            {
                sum += value * other;
            }
        }

        return sum;
    }

    private static List<string> RagServiceTokenize(string text)
    {
        var matches = Regex.Matches(text, @"[\p{L}\p{Nd}]+");
        var tokens = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            var value = match.Value.ToLowerInvariant();
            if (value.Length < 2)
            {
                continue;
            }
            tokens.Add(value);
        }
        return tokens;
    }
}

internal sealed class RagChunk
{
    public RagChunk(string source, string text, Dictionary<string, int> termFrequency)
    {
        Source = source;
        Text = text;
        TermFrequency = termFrequency;
    }

    public string Source { get; }
    public string Text { get; }
    public Dictionary<string, int> TermFrequency { get; }
    public Dictionary<string, float> Vector { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public double Norm { get; private set; }

    public void BuildVector(Dictionary<string, float> idf)
    {
        Vector = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        double sum = 0;
        foreach (var (term, count) in TermFrequency)
        {
            if (!idf.TryGetValue(term, out var idfValue))
            {
                continue;
            }

            var value = count * idfValue;
            Vector[term] = value;
            sum += value * value;
        }

        Norm = Math.Sqrt(sum);
    }
}
