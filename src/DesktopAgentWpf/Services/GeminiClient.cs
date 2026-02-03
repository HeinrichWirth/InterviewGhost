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

public sealed class GeminiClient
{
    private const string DefaultBaseSystemPrompt = "You are a live-call teleprompter. Give a short, speakable answer. Provide 1-2 variants (one concise, one slightly longer). Then add 2-4 bullet points of what to say or ask next. If context is missing, ask exactly one clarifying question and give a neutral filler line. Be concise and practical. No meta commentary. If the task requires code, first give a brief, professional explanation of the solution, then output a separate Python code block. The solution must be highly efficient and interview-ready for top companies. Comments in the code should read like your thinking process while writing it.";
    private const string AudioAnswerInstructions = "You will receive two audio inputs: MIC (me) and SYSTEM (the other person). Use both to understand the context and answer as a live-call teleprompter. Do NOT return transcripts or JSON. Return only the final answer.";
    private const int InlineDataMaxEncodedBytes = 20 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly Action<string>? _log;
    private readonly bool _verbose;
    private readonly int _maxTextChars;
    private readonly int _answerMaxTokens;
    private readonly string? _thinkingLevel;
    private readonly string? _responseLanguage;
    private readonly string _baseSystemPrompt;

    public GeminiClient(
        string? apiKey,
        string model,
        Action<string>? log,
        bool verbose,
        int maxTextChars,
        int answerMaxTokens,
        string? thinkingLevel,
        string? responseLanguage,
        string? baseSystemPrompt)
    {
        _apiKey = apiKey;
        _model = model;
        _log = log;
        _verbose = verbose;
        _maxTextChars = Math.Clamp(maxTextChars, 200, 8000);
        _answerMaxTokens = Math.Clamp(answerMaxTokens, 256, 8192);
        _thinkingLevel = thinkingLevel;
        _responseLanguage = string.IsNullOrWhiteSpace(responseLanguage) ? null : responseLanguage.Trim();
        _baseSystemPrompt = string.IsNullOrWhiteSpace(baseSystemPrompt) ? DefaultBaseSystemPrompt : baseSystemPrompt.Trim();

        _http = new HttpClient
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/")
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            _http.DefaultRequestHeaders.Add("x-goog-api-key", _apiKey);
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<string> TranscribeAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var bytes = await File.ReadAllBytesAsync(wavPath, cancellationToken).ConfigureAwait(false);
        Log($"Transcribe: {Path.GetFileName(wavPath)} ({bytes.Length} bytes)");

        var audioPart = await BuildAudioPartAsync(bytes, "audio/wav", Path.GetFileNameWithoutExtension(wavPath), cancellationToken)
            .ConfigureAwait(false);
        Log($"Transcribe audio part mode: {audioPart.Mode}");

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = "Transcribe the audio accurately. Return only the text, no explanations, no abbreviations." },
                        audioPart.Part
                    }
                }
            },
            generationConfig = BuildGenerationConfig(0.1, 4096)
        };

        var text = await SendGenerateContentAsync(payload, "transcribe", cancellationToken).ConfigureAwait(false);
        Log($"Transcribe response chars: {text.Length}");
        Log($"Transcribe text preview: {Truncate(text)}");
        return text;
    }

    public async Task<string> GetAnswerFromAudioAsync(
        string micPath,
        string systemPath,
        IReadOnlyList<ChatMessage> history,
        bool micSilent,
        bool systemSilent,
        string? ragContext = null,
        object[]? ragTools = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        Log($"Answer from audio (single call). micSilent={micSilent}, systemSilent={systemSilent}, history={history.Count}");

        var toolHint = ragTools is { Length: > 0 }
            ? "\n\nFile Search tool is available. You MUST use it before answering. If it returns no useful results, answer from general knowledge without refusing."
            : string.Empty;
        var systemInstruction = BuildSystemPrompt() + "\n\n" + AudioAnswerInstructions + toolHint;

        var parts = new List<object>
        {
            new
            {
                text =
                    "You are given two audios: microphone (mic) and system audio (system). " +
                    "Use them to understand the conversation and provide the best possible reply. " +
                    "Return only the final answer."
            }
        };

        var ragBlock = BuildRagBlock(ragContext);
        if (!string.IsNullOrWhiteSpace(ragBlock))
        {
            parts.Add(new { text = ragBlock });
        }

        if (micSilent)
        {
            parts.Add(new { text = "MIC AUDIO: missing (silence)." });
        }
        else
        {
            var micBytes = await File.ReadAllBytesAsync(micPath, cancellationToken).ConfigureAwait(false);
            Log($"Audio mic bytes: {micBytes.Length}");
            var micPart = await BuildAudioPartAsync(micBytes, "audio/wav", Path.GetFileNameWithoutExtension(micPath), cancellationToken)
                .ConfigureAwait(false);
            Log($"Mic audio part mode: {micPart.Mode}");
            parts.Add(new { text = "MIC AUDIO:" });
            parts.Add(micPart.Part);
        }

        if (systemSilent)
        {
            parts.Add(new { text = "SYSTEM AUDIO: missing (silence)." });
        }
        else
        {
            var sysBytes = await File.ReadAllBytesAsync(systemPath, cancellationToken).ConfigureAwait(false);
            Log($"Audio system bytes: {sysBytes.Length}");
            var sysPart = await BuildAudioPartAsync(sysBytes, "audio/wav", Path.GetFileNameWithoutExtension(systemPath), cancellationToken)
                .ConfigureAwait(false);
            Log($"System audio part mode: {sysPart.Mode}");
            parts.Add(new { text = "SYSTEM AUDIO:" });
            parts.Add(sysPart.Part);
        }

        var contents = BuildContents(history, parts.ToArray());
        var payload = new Dictionary<string, object>
        {
            ["system_instruction"] = new { parts = new object[] { new { text = systemInstruction } } },
            ["contents"] = contents,
            ["generationConfig"] = BuildGenerationConfig(0.2, _answerMaxTokens)
        };
        if (ragTools is { Length: > 0 })
        {
            Log("RAG tools attached to request (file search).");
            payload["tools"] = ragTools;
        }

        var text = await SendGenerateContentAsync(payload, "audio+answer", cancellationToken).ConfigureAwait(false);
        Log($"Audio+answer response chars: {text.Length}");
        Log($"Audio+answer preview: {Truncate(text)}");
        return text;
    }

    public async Task<string> GetAnswerFromTranscriptsAsync(string micTranscript, string systemTranscript, IReadOnlyList<ChatMessage> history, string? ragContext = null, object[]? ragTools = null, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        Log($"Answer from transcripts. Mic chars={micTranscript.Length}, System chars={systemTranscript.Length}, history={history.Count}");

        var ragBlock = BuildRagBlock(ragContext);
        var toolHint = ragTools is { Length: > 0 }
            ? "\n\nFile Search tool is available. You MUST use it before answering. If it returns no useful results, answer from general knowledge without refusing."
            : string.Empty;
        var systemInstruction = BuildSystemPrompt() + toolHint;
        var userText = $"{(string.IsNullOrWhiteSpace(ragBlock) ? "" : ragBlock + "\n\n")}" +
                       $"mic transcript:\n{micTranscript}\n\nsystem transcript:\n{systemTranscript}";
        var contents = BuildContents(history, new object[]
        {
            new { text = userText }
        });
        var payload = new Dictionary<string, object>
        {
            ["system_instruction"] = new { parts = new object[] { new { text = systemInstruction } } },
            ["contents"] = contents,
            ["generationConfig"] = BuildGenerationConfig(0.2, _answerMaxTokens)
        };
        if (ragTools is { Length: > 0 })
        {
            Log("RAG tools attached to request (file search).");
            payload["tools"] = ragTools;
        }

        var text = await SendGenerateContentAsync(payload, "answer", cancellationToken).ConfigureAwait(false);
        Log($"Answer response chars: {text.Length}");
        Log($"Answer preview: {Truncate(text)}");
        return text;
    }

    public async Task<string> GetAnswerFromScreenshotAsync(string pngPath, IReadOnlyList<ChatMessage> history, string? ragContext = null, object[]? ragTools = null, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var bytes = await File.ReadAllBytesAsync(pngPath, cancellationToken).ConfigureAwait(false);
        Log($"Screenshot analysis: {Path.GetFileName(pngPath)} ({bytes.Length} bytes), history={history.Count}");

        var imagePart = new
        {
            inline_data = new
            {
                mime_type = "image/png",
                data = Convert.ToBase64String(bytes)
            }
        };

        var ragBlock = BuildRagBlock(ragContext);
        var toolHint = ragTools is { Length: > 0 }
            ? "\n\nFile Search tool is available. You MUST use it before answering. If it returns no useful results, answer from general knowledge without refusing."
            : string.Empty;
        var systemInstruction = BuildSystemPrompt() + toolHint;
        var prompt = $"{(string.IsNullOrWhiteSpace(ragBlock) ? "" : ragBlock + "\n\n")}" +
                     "Here is a screenshot. Help me understand what is on screen and what to reply.";
        var contents = BuildContents(history, new object[]
        {
            new { text = prompt },
            imagePart
        });
        var payload = new Dictionary<string, object>
        {
            ["system_instruction"] = new { parts = new object[] { new { text = systemInstruction } } },
            ["contents"] = contents,
            ["generationConfig"] = BuildGenerationConfig(0.2, _answerMaxTokens)
        };
        if (ragTools is { Length: > 0 })
        {
            Log("RAG tools attached to request (file search).");
            payload["tools"] = ragTools;
        }

        var text = await SendGenerateContentAsync(payload, "screenshot", cancellationToken).ConfigureAwait(false);
        Log($"Screenshot answer chars: {text.Length}");
        Log($"Screenshot answer preview: {Truncate(text)}");
        return text;
    }

    public async Task<string> GetAnswerFromFollowUpAsync(string userText, IReadOnlyList<ChatMessage> history, string? ragContext = null, object[]? ragTools = null, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        Log($"Follow-up request chars={userText.Length}, history={history.Count}");

        var ragBlock = BuildRagBlock(ragContext);
        var toolHint = ragTools is { Length: > 0 }
            ? "\n\nFile Search tool is available. You MUST use it before answering. If it returns no useful results, answer from general knowledge without refusing."
            : string.Empty;
        var systemInstruction = BuildSystemPrompt() + toolHint;
        var prompt = $"{(string.IsNullOrWhiteSpace(ragBlock) ? "" : ragBlock + "\n\n")}" +
                     $"User follow-up:\n{userText}";
        var contents = BuildContents(history, new object[]
        {
            new { text = prompt }
        });
        var payload = new Dictionary<string, object>
        {
            ["system_instruction"] = new { parts = new object[] { new { text = systemInstruction } } },
            ["contents"] = contents,
            ["generationConfig"] = BuildGenerationConfig(0.2, _answerMaxTokens)
        };
        if (ragTools is { Length: > 0 })
        {
            Log("RAG tools attached to request (file search).");
            payload["tools"] = ragTools;
        }

        var text = await SendGenerateContentAsync(payload, "followup", cancellationToken).ConfigureAwait(false);
        Log($"Follow-up answer chars: {text.Length}");
        Log($"Follow-up answer preview: {Truncate(text)}");
        return text;
    }

    private static List<object> BuildContents(IReadOnlyList<ChatMessage> history, object[] currentParts)
    {
        var contents = new List<object>(history.Count + 1);

        foreach (var msg in history)
        {
            if (string.IsNullOrWhiteSpace(msg.Text))
            {
                continue;
            }

            var role = msg.Role == "model" ? "model" : "user";
            contents.Add(new
            {
                role,
                parts = new object[]
                {
                    new { text = msg.Text }
                }
            });
        }

        contents.Add(new
        {
            role = "user",
            parts = currentParts
        });

        return contents;
    }

    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder(_baseSystemPrompt);

        if (!string.IsNullOrWhiteSpace(_responseLanguage))
        {
            sb.Append("\n\nAnswer in the language: ")
                .Append(_responseLanguage)
                .Append(".");
        }

        return sb.ToString();
    }

    private static string? BuildRagBlock(string? ragContext)
    {
        if (string.IsNullOrWhiteSpace(ragContext))
        {
            return null;
        }

        return "REFERENCE CONTEXT (use only if relevant):\n" + ragContext;
    }

    private object BuildGenerationConfig(double temperature, int maxOutputTokens)
    {
        var config = new Dictionary<string, object>
        {
            ["temperature"] = temperature,
            ["maxOutputTokens"] = maxOutputTokens
        };

        var thinkingConfig = BuildThinkingConfig();
        if (thinkingConfig != null)
        {
            config["thinkingConfig"] = thinkingConfig;
        }

        return config;
    }

    private object? BuildThinkingConfig()
    {
        var level = NormalizeThinkingLevel(_thinkingLevel);
        if (level == null)
        {
            return null;
        }

        var modelLower = _model.ToLowerInvariant();
        if (modelLower.Contains("2.5"))
        {
            Log("ThinkingLevel ignored for Gemini 2.5 models. Use thinkingBudget instead.");
            return null;
        }

        if (modelLower.Contains("pro") && (level == "MINIMAL" || level == "MEDIUM"))
        {
            Log($"ThinkingLevel {level} not supported by Gemini 3 Pro. Using LOW.");
            level = "LOW";
        }

        return new Dictionary<string, object>
        {
            ["thinkingLevel"] = level
        };
    }

    private static string? NormalizeThinkingLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return null;
        }

        var value = level.Trim().ToLowerInvariant();
        return value switch
        {
            "auto" or "default" or "dynamic" or "unspecified" => null,
            "minimal" or "min" or "off" => "MINIMAL",
            "low" => "LOW",
            "medium" or "med" => "MEDIUM",
            "high" => "HIGH",
            _ => null
        };
    }

    private async Task<PartInfo> BuildAudioPartAsync(byte[] bytes, string mimeType, string displayName, CancellationToken cancellationToken)
    {
        var estimatedBase64Bytes = ((bytes.Length + 2) / 3) * 4;
        if (estimatedBase64Bytes <= InlineDataMaxEncodedBytes)
        {
            return new PartInfo(
                new
                {
                    inline_data = new
                    {
                        mime_type = mimeType,
                        data = Convert.ToBase64String(bytes)
                    }
                },
                "inline");
        }

        var uploaded = await UploadFileAsync(bytes, mimeType, displayName, cancellationToken).ConfigureAwait(false);
        return new PartInfo(
            new
            {
                file_data = new
                {
                    mime_type = uploaded.MimeType,
                    file_uri = uploaded.Uri
                }
            },
            "file");
    }

    private async Task<UploadedFile> UploadFileAsync(byte[] bytes, string mimeType, string displayName, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("GEMINI_API_KEY is not set.");
        }

        Log($"Uploading audio to Gemini Files API ({bytes.Length} bytes)...");

        var startRequest = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/upload/v1beta/files?key={_apiKey}");
        startRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        startRequest.Headers.Add("X-Goog-Upload-Command", "start");
        startRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", bytes.Length.ToString());
        startRequest.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
        startRequest.Content = new StringContent(
            JsonSerializer.Serialize(new { file = new { display_name = displayName } }),
            Encoding.UTF8,
            "application/json");

        using var startResponse = await _http.SendAsync(startRequest, cancellationToken).ConfigureAwait(false);
        var startBody = await startResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!startResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractError(startBody));
        }

        if (!startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var urls))
        {
            throw new InvalidOperationException("Upload URL not returned by Gemini API.");
        }

        var uploadUrl = urls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            throw new InvalidOperationException("Upload URL not returned by Gemini API.");
        }

        var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
        uploadRequest.Content = new ByteArrayContent(bytes);
        uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        uploadRequest.Content.Headers.ContentLength = bytes.Length;

        using var uploadResponse = await _http.SendAsync(uploadRequest, cancellationToken).ConfigureAwait(false);
        var body = await uploadResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractError(body));
        }

        var uploaded = ExtractUploadedFile(body, mimeType);
        Log($"Upload complete. File URI: {uploaded.Uri}");
        return uploaded;
    }

    private async Task<string> SendGenerateContentAsync(object payload, string operation, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        Log($"Gemini {operation} generateContent: model={_model}, payloadBytes={Encoding.UTF8.GetByteCount(json)}");

        var sw = Stopwatch.StartNew();
        using var response = await _http.PostAsync(
            $"models/{_model}:generateContent",
            new StringContent(json, Encoding.UTF8, "application/json"),
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            Log($"Gemini {operation} error in {sw.Elapsed.TotalMilliseconds:F0} ms: {body}");
            throw new InvalidOperationException(ExtractError(body));
        }

        var text = ExtractResponseText(body);
        if (string.IsNullOrWhiteSpace(text))
        {
            LogDiagnostics(body, operation);
            throw new InvalidOperationException($"Gemini returned an empty response for {operation}. Try a different model or a shorter prompt.");
        }
        LogGrounding(body);
        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        var cps = text.Length / seconds;
        Log($"Gemini {operation} response in {sw.Elapsed.TotalMilliseconds:F0} ms, chars={text.Length}, {cps:F1} chars/sec");
        return text;
    }


    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("GEMINI_API_KEY is not set.");
        }
    }

    private void Log(string message)
    {
        if (_verbose)
        {
            _log?.Invoke(message);
        }
    }

    private string Truncate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.Length <= _maxTextChars)
        {
            return text;
        }

        return text.Substring(0, _maxTextChars) + "…";
    }

    private static string ExtractResponseText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates))
        {
            return string.Empty;
        }

        var textBuilder = new StringBuilder();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content))
            {
                continue;
            }

            if (!content.TryGetProperty("parts", out var parts))
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textElement))
                {
                    textBuilder.Append(textElement.GetString());
                }
            }

            if (textBuilder.Length > 0)
            {
                break;
            }
        }

        return textBuilder.ToString();
    }

    private void LogGrounding(string json)
    {
        if (!_verbose)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates))
            {
                return;
            }

            var found = false;
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (candidate.TryGetProperty("groundingMetadata", out var grounding) ||
                    candidate.TryGetProperty("grounding_metadata", out grounding))
                {
                    found = true;
                    if (grounding.TryGetProperty("groundingChunks", out var chunks) ||
                        grounding.TryGetProperty("grounding_chunks", out chunks))
                    {
                        var count = chunks.ValueKind == JsonValueKind.Array ? chunks.GetArrayLength() : 0;
                        Log($"Gemini grounding chunks: {count}");
                        return;
                    }

                    Log("Gemini grounding metadata present.");
                    return;
                }
            }

            if (!found)
            {
                Log("Gemini grounding metadata: none");
            }
        }
        catch
        {
        }
    }

    private void LogDiagnostics(string json, string operation)
    {
        if (!_verbose)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("promptFeedback", out var feedback) ||
                doc.RootElement.TryGetProperty("prompt_feedback", out feedback))
            {
                if (feedback.TryGetProperty("blockReason", out var blockReason) ||
                    feedback.TryGetProperty("block_reason", out blockReason))
                {
                    Log($"Gemini {operation} blockReason: {blockReason.GetString()}");
                }
            }

            if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array &&
                candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("finishReason", out var finish) ||
                    candidate.TryGetProperty("finish_reason", out finish))
                {
                    Log($"Gemini {operation} finishReason: {finish.GetString()}");
                }
            }

            Log($"Gemini {operation} raw response (truncated): {Truncate(json)}");
        }
        catch
        {
            Log($"Gemini {operation} raw response (unparsed, truncated): {Truncate(json)}");
        }
    }


    private static UploadedFile ExtractUploadedFile(string json, string fallbackMime)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("file", out var file))
        {
            throw new InvalidOperationException("Gemini file upload did not return file metadata.");
        }

        var uri = file.TryGetProperty("uri", out var uriElement) ? uriElement.GetString() : null;
        var mime = fallbackMime;
        if (file.TryGetProperty("mimeType", out var mimeElement))
        {
            mime = mimeElement.GetString() ?? fallbackMime;
        }
        else if (file.TryGetProperty("mime_type", out var mimeElementAlt))
        {
            mime = mimeElementAlt.GetString() ?? fallbackMime;
        }

        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new InvalidOperationException("Gemini file upload did not return a file URI.");
        }

        return new UploadedFile(uri, mime);
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

    private sealed record PartInfo(object Part, string Mode);
    private sealed record UploadedFile(string Uri, string MimeType);
}
