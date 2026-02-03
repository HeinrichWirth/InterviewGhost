namespace DesktopAgentWpf.Services;

public interface IRagProvider
{
    bool Enabled { get; }
    bool IsReady { get; }
    bool UseForAudio { get; }
    bool UseForScreenshot { get; }
    bool UseForFollowUp { get; }
    int QueryMaxChars { get; }
    string? DefaultQuery { get; }

    event Action<RagProgress>? ProgressChanged;
    event Action<string>? StatusChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    RagPayload BuildPayload(string? query);
}

public sealed record RagPayload(string? Context, object[]? Tools);
