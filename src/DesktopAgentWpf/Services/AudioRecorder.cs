using System.IO;
using NAudio.Wave;
using NAudio.MediaFoundation;
using NAudio.CoreAudioApi;

namespace DesktopAgentWpf.Services;

public sealed class AudioRecorder : IDisposable
{
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _sysCapture;
    private WaveFileWriter? _micWriter;
    private WaveFileWriter? _sysWriter;
    private TaskCompletionSource<bool>? _micStopped;
    private TaskCompletionSource<bool>? _sysStopped;
    private MMDevice? _outputDevice;
    private MMDevice? _inputDevice;

    private string? _micRawPath;
    private string? _sysRawPath;

    public bool IsRecording { get; private set; }
    public string? SessionFolder { get; private set; }

    public AudioRecorder()
    {
        MediaFoundationApi.Startup();
    }

    public void Start(string sessionFolder, string? outputDeviceId = null, string? inputDeviceId = null)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Recording already in progress.");
        }

        SessionFolder = sessionFolder;
        Directory.CreateDirectory(SessionFolder);

        _micRawPath = Path.Combine(SessionFolder, "mic_raw.wav");
        _sysRawPath = Path.Combine(SessionFolder, "system_raw.wav");

        _inputDevice?.Dispose();
        _inputDevice = null;
        if (!string.IsNullOrWhiteSpace(inputDeviceId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _inputDevice = enumerator.GetDevice(inputDeviceId);
            }
            catch
            {
                _inputDevice = null;
            }
        }

        _micCapture = _inputDevice != null ? new WasapiCapture(_inputDevice) : new WasapiCapture();
        _outputDevice?.Dispose();
        _outputDevice = null;
        if (!string.IsNullOrWhiteSpace(outputDeviceId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _outputDevice = enumerator.GetDevice(outputDeviceId);
            }
            catch
            {
                _outputDevice = null;
            }
        }
        _sysCapture = _outputDevice != null ? new WasapiLoopbackCapture(_outputDevice) : new WasapiLoopbackCapture();

        _micWriter = new WaveFileWriter(_micRawPath, _micCapture.WaveFormat);
        _sysWriter = new WaveFileWriter(_sysRawPath, _sysCapture.WaveFormat);

        _micStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sysStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _micCapture.DataAvailable += (_, e) => _micWriter?.Write(e.Buffer, 0, e.BytesRecorded);
        _sysCapture.DataAvailable += (_, e) => _sysWriter?.Write(e.Buffer, 0, e.BytesRecorded);

        _micCapture.RecordingStopped += (_, _) => _micStopped?.TrySetResult(true);
        _sysCapture.RecordingStopped += (_, _) => _sysStopped?.TrySetResult(true);

        _micCapture.StartRecording();
        _sysCapture.StartRecording();
        IsRecording = true;
    }

    public async Task<AudioResult> StopAsync()
    {
        if (!IsRecording)
        {
            throw new InvalidOperationException("No active recording.");
        }

        _micCapture?.StopRecording();
        _sysCapture?.StopRecording();

        if (_micStopped != null && _sysStopped != null)
        {
            await Task.WhenAll(_micStopped.Task, _sysStopped.Task).ConfigureAwait(false);
        }

        _micCapture?.Dispose();
        _sysCapture?.Dispose();
        _micWriter?.Dispose();
        _sysWriter?.Dispose();
        _outputDevice?.Dispose();
        _outputDevice = null;
        _inputDevice?.Dispose();
        _inputDevice = null;

        IsRecording = false;

        if (_micRawPath == null || _sysRawPath == null || SessionFolder == null)
        {
            throw new InvalidOperationException("Recording paths are missing.");
        }

        var micWavPath = Path.Combine(SessionFolder, "mic.wav");
        var sysWavPath = Path.Combine(SessionFolder, "system.wav");

        ConvertToTarget(_micRawPath, micWavPath);
        ConvertToTarget(_sysRawPath, sysWavPath);

        return new AudioResult(micWavPath, sysWavPath);
    }

    private static void ConvertToTarget(string inputPath, string outputPath)
    {
        using var reader = new AudioFileReader(inputPath);
        var targetFormat = new WaveFormat(16000, 16, 1);
        using var resampler = new MediaFoundationResampler(reader, targetFormat) { ResamplerQuality = 60 };
        WaveFileWriter.CreateWaveFile(outputPath, resampler);
    }

    public void Dispose()
    {
        if (IsRecording)
        {
            try
            {
                _micCapture?.StopRecording();
                _sysCapture?.StopRecording();
            }
            catch
            {
            }
        }

        _micCapture?.Dispose();
        _sysCapture?.Dispose();
        _micWriter?.Dispose();
        _sysWriter?.Dispose();
        _outputDevice?.Dispose();
        _outputDevice = null;
        _inputDevice?.Dispose();
        _inputDevice = null;
        MediaFoundationApi.Shutdown();
    }

    public async Task<AudioProbeResult> ProbeAsync(string? inputDeviceId, string? outputDeviceId, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Cannot probe while recording.");
        }

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? inputDevice = null;
        MMDevice? outputDevice = null;

        if (!string.IsNullOrWhiteSpace(inputDeviceId))
        {
            try { inputDevice = enumerator.GetDevice(inputDeviceId); } catch { inputDevice = null; }
        }

        if (!string.IsNullOrWhiteSpace(outputDeviceId))
        {
            try { outputDevice = enumerator.GetDevice(outputDeviceId); } catch { outputDevice = null; }
        }

        using var micCapture = inputDevice != null ? new WasapiCapture(inputDevice) : new WasapiCapture();
        using var sysCapture = outputDevice != null ? new WasapiLoopbackCapture(outputDevice) : new WasapiLoopbackCapture();

        float micPeak = 0f;
        float sysPeak = 0f;
        var micStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sysStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        micCapture.DataAvailable += (_, e) =>
        {
            micPeak = Math.Max(micPeak, ComputePeak(e.Buffer, e.BytesRecorded, micCapture.WaveFormat));
        };
        sysCapture.DataAvailable += (_, e) =>
        {
            sysPeak = Math.Max(sysPeak, ComputePeak(e.Buffer, e.BytesRecorded, sysCapture.WaveFormat));
        };

        micCapture.RecordingStopped += (_, _) => micStopped.TrySetResult(true);
        sysCapture.RecordingStopped += (_, _) => sysStopped.TrySetResult(true);

        micCapture.StartRecording();
        sysCapture.StartRecording();

        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);

        micCapture.StopRecording();
        sysCapture.StopRecording();

        await Task.WhenAll(micStopped.Task, sysStopped.Task).ConfigureAwait(false);

        inputDevice?.Dispose();
        outputDevice?.Dispose();

        return new AudioProbeResult(micPeak, sysPeak);
    }

    private static float ComputePeak(byte[] buffer, int bytes, WaveFormat format)
    {
        if (bytes <= 0)
        {
            return 0f;
        }

        float peak = 0f;
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            return 0f;
        }

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var i = 0; i + 3 < bytes; i += 4)
            {
                var sample = BitConverter.ToSingle(buffer, i);
                var value = Math.Abs(sample);
                if (value > peak) peak = value;
            }
            return peak;
        }

        if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 1 < bytes; i += 2)
            {
                var sample = BitConverter.ToInt16(buffer, i);
                var value = Math.Abs(sample / 32768f);
                if (value > peak) peak = value;
            }
            return peak;
        }

        if (format.BitsPerSample == 32)
        {
            for (var i = 0; i + 3 < bytes; i += 4)
            {
                var sample = BitConverter.ToInt32(buffer, i);
                var value = Math.Abs(sample / 2147483648f);
                if (value > peak) peak = value;
            }
            return peak;
        }

        for (var i = 0; i < bytes; i += bytesPerSample)
        {
            var value = Math.Abs(buffer[i] / 128f);
            if (value > peak) peak = value;
        }

        return peak;
    }
}

public sealed record AudioResult(string MicPath, string SystemPath);
public sealed record AudioProbeResult(float MicPeak, float SystemPeak);
