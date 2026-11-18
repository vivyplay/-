using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Suisheng;

public sealed record MicrophoneInfo(int Id, string Name);
public sealed record RecordedAudio(float[] Samples, bool HasSound, bool EndedBySilence = false);

internal sealed class SilenceTracker
{
    private double elapsed, lastSound;
    private bool heard;
    public bool Update(double peak, double duration, double waitSeconds)
    {
        elapsed += duration;
        if (peak > 0.025) { heard = true; lastSound = elapsed; }
        return heard && elapsed - lastSound >= waitSeconds;
    }
}

public sealed class AudioRecorder : IDisposable
{
    private readonly object gate = new();
    private WaveInEvent? device;
    private MemoryStream? pcm;
    private TaskCompletionSource<RecordedAudio>? completion;
    private bool stopping;
    private bool hasSound;
    private bool endedBySilence;
    private SilenceTracker? silenceTracker;
    public event Action<double, double>? Meter;
    public event Action<Exception?>? Ended;

    public static IReadOnlyList<MicrophoneInfo> GetDevices()
    {
        var result = new List<MicrophoneInfo> { new(-1, "系统默认麦克风") };
        for (int i = 0; i < WaveIn.DeviceCount; i++) result.Add(new(i, WaveIn.GetCapabilities(i).ProductName));
        return result;
    }

    public void Start(int microphone, int maxSeconds, bool detectSilence = false, double silenceWaitSeconds = 2)
    {
        lock (gate)
        {
            if (device != null) throw new InvalidOperationException("录音正在进行。");
            if (WaveIn.DeviceCount == 0) throw new InvalidOperationException("未找到麦克风，请连接设备并允许录音权限。");
            if (microphone >= WaveIn.DeviceCount) throw new InvalidOperationException("所选麦克风已不可用，请重新选择。");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pcm = new MemoryStream();
            hasSound = false;
            endedBySilence = false;
            silenceTracker = detectSilence ? new SilenceTracker() : null;
            stopping = false;
            device = new WaveInEvent { DeviceNumber = microphone, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
            device.DataAvailable += (_, e) =>
            {
                double peak = 0, seconds;
                bool shouldStop;
                lock (gate)
                {
                    if (pcm == null || stopping) return;
                    for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                        peak = Math.Max(peak, Math.Abs((short)(e.Buffer[i] | e.Buffer[i + 1] << 8) / 32768.0));
                    hasSound |= peak > 0.015;
                    pcm.Write(e.Buffer, 0, e.BytesRecorded);
                    seconds = pcm.Length / 32000.0;
                    endedBySilence = silenceTracker?.Update(peak, e.BytesRecorded / 32000.0, silenceWaitSeconds) == true;
                    shouldStop = ReachedMaximum(seconds, maxSeconds) || endedBySilence;
                }
                Meter?.Invoke(peak, seconds);
                if (shouldStop) _ = StopAsync();
            };
            device.RecordingStopped += (_, e) =>
            {
                lock (gate)
                {
                    byte[] bytes = pcm?.ToArray() ?? [];
                    float[] samples = new float[bytes.Length / 2];
                    for (int i = 0; i < samples.Length; i++) samples[i] = (short)(bytes[2 * i] | bytes[2 * i + 1] << 8) / 32768f;
                    if (e.Exception == null) completion?.TrySetResult(new(samples, hasSound, endedBySilence));
                    else completion?.TrySetException(new InvalidOperationException("麦克风录音中断，请检查设备或权限。", e.Exception));
                }
                Ended?.Invoke(e.Exception);
            };
            try { device.StartRecording(); }
            catch { device.Dispose(); device = null; pcm.Dispose(); pcm = null; completion = null; throw; }
        }
    }

    internal static bool ReachedMaximum(double seconds, int maxSeconds) => seconds >= maxSeconds;

    public Task<RecordedAudio> StopAsync()
    {
        lock (gate)
        {
            if (device == null || completion == null) return Task.FromResult(new RecordedAudio([], false));
            if (!stopping) { stopping = true; device.StopRecording(); }
            return completion.Task;
        }
    }

    public void Release()
    {
        lock (gate)
        {
            device?.Dispose();
            device = null;
            pcm?.Dispose();
            pcm = null;
            completion = null;
            silenceTracker = null;
        }
    }

    public void Dispose() => Release();
}
