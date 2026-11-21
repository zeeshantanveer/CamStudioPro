using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CamStudio.Desktop.Media;

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Plays the phone's microphone audio into a chosen Windows playback device.
/// Pick a virtual audio cable (e.g. VB-CABLE "CABLE Input") so meeting apps can select
/// its recording side ("CABLE Output") as their microphone.
/// </summary>
public sealed class AudioSink : IDisposable
{
    private readonly object _lock = new();
    private WasapiOut? _out;
    private BufferedWaveProvider? _buffer;
    private string _deviceId = "";
    private WaveFormat _format = new(48000, 16, 1);

    public float Level { get; private set; }

    public static List<AudioDevice> ListOutputs()
    {
        var list = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName));
        }
        catch
        {
        }
        return list;
    }

    /// <summary>A virtual-cable playback device if one is installed (best default target).</summary>
    public static AudioDevice? FindVirtualCable() =>
        ListOutputs().FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
                                          d.Name.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase) ||
                                          d.Name.Contains("Virtual Audio", StringComparison.OrdinalIgnoreCase));

    public void SetDevice(string deviceId)
    {
        lock (_lock)
        {
            if (deviceId == _deviceId)
                return;
            _deviceId = deviceId;
            StopLocked();
        }
    }

    public void Configure(int sampleRate, int channels)
    {
        lock (_lock)
        {
            var format = new WaveFormat(sampleRate, 16, channels);
            if (!format.Equals(_format))
            {
                _format = format;
                StopLocked();
            }
        }
    }

    public void Push(ReadOnlySpan<byte> pcm)
    {
        // meter (peak of this chunk)
        var peak = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var s = Math.Abs((int)(short)(pcm[i] | (pcm[i + 1] << 8)));
            if (s > peak)
                peak = s;
        }
        Level = peak / 32768f;

        lock (_lock)
        {
            if (string.IsNullOrEmpty(_deviceId))
                return;

            if (_out == null && !StartLocked())
                return;

            var buffer = _buffer!;
            // keep latency bounded: if more than 200 ms is queued, drop the backlog
            if (buffer.BufferedDuration > TimeSpan.FromMilliseconds(200))
                buffer.ClearBuffer();
            buffer.AddSamples(pcm.ToArray(), 0, pcm.Length);
        }
    }

    private bool StartLocked()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(_deviceId);
            _buffer = new BufferedWaveProvider(_format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(1),
            };
            _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
            _out.Init(_buffer);
            _out.Play();
            return true;
        }
        catch
        {
            StopLocked();
            _deviceId = "";
            return false;
        }
    }

    private void StopLocked()
    {
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        _out = null;
        _buffer = null;
    }

    public void Stop()
    {
        lock (_lock)
            StopLocked();
        Level = 0;
    }

    public void Dispose() => Stop();
}
