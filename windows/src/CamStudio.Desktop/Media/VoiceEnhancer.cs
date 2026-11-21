using System.Buffers.Binary;
using RNNoise.NET;

namespace CamStudio.Desktop.Media;

public sealed record VoiceEnhancementOptions(bool Enabled = true, string NoiseReduction = "Balanced",
    bool AutomaticVolume = true, string Preset = "Natural");

/// <summary>One instance per phone session. All methods run on the audio receive thread.</summary>
public sealed class VoiceEnhancer : IDisposable
{
    private const int Samples = 480; // RNNoise: 10 ms of 48 kHz mono PCM.
    private readonly byte[] _pending = new byte[Samples * 2];
    private readonly float[] _wet = new float[Samples];
    private readonly float[] _dry = new float[Samples];
    private readonly float[] _previousDry = new float[Samples];
    private readonly Biquad _highPass = Biquad.HighPass(80);
    private readonly Biquad _hum50 = Biquad.Notch(50);
    private readonly Biquad _hum60 = Biquad.Notch(60);
    private Biquad _presence = Biquad.Peak(3000, 1.5);
    private IntPtr _state;
    private int _pendingCount;
    private bool _supported = true;
    private bool _failed;
    private VoiceEnhancementOptions? _lastOptions;
    private double _automaticGain = 1, _envelope, _speechGain = 1;
    public string Status { get; private set; } = "Ready";

    public void Configure(int sampleRate, int channels)
    {
        Reset();
        _supported = sampleRate == 48000 && channels == 1;
    }

    public byte[] Process(ReadOnlySpan<byte> pcm, VoiceEnhancementOptions options)
    {
        var originalPcm = pcm;
        if (options != _lastOptions)
        {
            Reset();
            _lastOptions = options;
            _presence = Biquad.Peak(3000, options.Preset == "Broadcast" ? 3 : 1.5);
        }
        if (!options.Enabled || !_supported || _failed)
        {
            Status = !options.Enabled ? "Off" : !_supported ? "Bypassed: requires 48 kHz mono audio" : Status;
            return originalPcm.ToArray();
        }

        try
        {
            if (_state == IntPtr.Zero)
            {
                _state = Native.rnnoise_create(IntPtr.Zero);
                if (_state == IntPtr.Zero) throw new InvalidOperationException("Noise suppression could not initialize.");
            }
            var output = new byte[(_pendingCount + pcm.Length) / _pending.Length * _pending.Length];
            var written = 0;
            while (!pcm.IsEmpty)
            {
                var count = Math.Min(pcm.Length, _pending.Length - _pendingCount);
                pcm[..count].CopyTo(_pending.AsSpan(_pendingCount));
                _pendingCount += count;
                pcm = pcm[count..];
                if (_pendingCount != _pending.Length) continue;
                ProcessFrame(output.AsSpan(written, _pending.Length), options);
                written += _pending.Length;
                _pendingCount = 0;
            }
            Status = $"Active · {options.NoiseReduction} · {options.Preset}";
            return output;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            // Never interrupt video or disconnect the phone if the native dependency is unavailable.
            Reset();
            _failed = true;
            Status = "Unavailable · original audio (" + error.Message + ")";
            // The current input must be preserved even when native loading fails.
            return originalPcm.ToArray();
        }
    }

    private unsafe void ProcessFrame(Span<byte> output, VoiceEnhancementOptions options)
    {
        for (var i = 0; i < Samples; i++)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(_pending.AsSpan(i * 2)) / 32768f;
            _dry[i] = (float)_hum60.Process(_hum50.Process(_highPass.Process(value)));
            _wet[i] = _dry[i] * 32768;
        }
        float speechProbability;
        fixed (float* ptr = _wet)
            speechProbability = Native.rnnoise_process_frame(_state, ptr, ptr);

        var wetAmount = options.NoiseReduction switch { "Low" => .65, "Strong" => 1.0, _ => .9 };
        // Gently lower residual noise between words rather than abruptly gating the microphone.
        var floor = options.NoiseReduction switch { "Low" => .55, "Strong" => .12, _ => .25 };
        var speechTarget = floor + (1 - floor) * Math.Clamp((speechProbability - .1) / .5, 0, 1);
        var speechStep = (speechTarget - _speechGain) / Samples;
        double energy = 0;
        for (var i = 0; i < Samples; i++)
        {
            _wet[i] /= 32768;
            energy += _wet[i] * _wet[i];
        }
        var rms = Math.Sqrt(energy / Samples);
        // Freeze gain in noise/silence so automatic leveling does not amplify the noise floor.
        if (options.AutomaticVolume && speechProbability > .6 && rms > .008)
        {
            var target = Math.Clamp(.12 / rms, .35, 3);
            _automaticGain += (target - _automaticGain) * (target < _automaticGain ? .12 : .005);
        }

        var threshold = options.Preset == "Broadcast" ? .126 : .2;
        for (var i = 0; i < Samples; i++)
        {
            // RNNoise overlap/add delays audio one frame. Align the dry path before blending.
            var sample = _wet[i] * wetAmount + _previousDry[i] * (1 - wetAmount);
            _speechGain += speechStep * (speechTarget > _speechGain ? 1 : .1);
            sample *= _speechGain;
            sample = _presence.Process(sample) * (options.AutomaticVolume ? _automaticGain : 1);
            var absolute = Math.Abs(sample);
            var coefficient = absolute > _envelope ? .00416 : .000208;
            _envelope += (absolute - _envelope) * coefficient;
            if (_envelope > threshold)
                sample *= Math.Pow(threshold / _envelope, 1 - 1 / 3.0);
            // Hard ceiling protects both PCM conversion and downstream playback from overshoot.
            sample = Math.Clamp(sample, -.94, .94);
            BinaryPrimitives.WriteInt16LittleEndian(output[(i * 2)..], (short)Math.Round(sample * 32767));
        }
        _dry.CopyTo(_previousDry, 0);
    }

    public void Reset()
    {
        if (_state != IntPtr.Zero) Native.rnnoise_destroy(_state);
        _state = IntPtr.Zero;
        _pendingCount = 0;
        _failed = false;
        _automaticGain = 1;
        _envelope = 0;
        _speechGain = 1;
        Array.Clear(_previousDry);
        _highPass.Reset();
        _hum50.Reset();
        _hum60.Reset();
        _presence.Reset();
    }

    public void Dispose() => Reset();

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _z1, _z2;
        public double Process(double input)
        {
            var output = b0 * input + _z1;
            _z1 = b1 * input - a1 * output + _z2;
            _z2 = b2 * input - a2 * output;
            return output;
        }
        public void Reset() => _z1 = _z2 = 0;
        public static Biquad HighPass(double frequency)
        {
            var w = 2 * Math.PI * frequency / 48000;
            var cos = Math.Cos(w);
            var alpha = Math.Sin(w) / (2 * .707);
            var a0 = 1 + alpha;
            return new((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }
        public static Biquad Peak(double frequency, double db)
        {
            var w = 2 * Math.PI * frequency / 48000;
            var a = Math.Pow(10, db / 40);
            var alpha = Math.Sin(w) / 2;
            var a0 = 1 + alpha / a;
            return new((1 + alpha * a) / a0, -2 * Math.Cos(w) / a0, (1 - alpha * a) / a0,
                -2 * Math.Cos(w) / a0, (1 - alpha / a) / a0);
        }
        public static Biquad Notch(double frequency)
        {
            var w = 2 * Math.PI * frequency / 48000;
            var alpha = Math.Sin(w) / (2 * 10);
            var a0 = 1 + alpha;
            return new(1 / a0, -2 * Math.Cos(w) / a0, 1 / a0, -2 * Math.Cos(w) / a0, (1 - alpha) / a0);
        }
    }
}
