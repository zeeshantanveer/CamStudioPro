using System.Buffers.Binary;
using System.Diagnostics;
using CamStudio.Desktop.Media;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static byte[] Noise(int seconds, bool hum = false)
{
    var rng = new Random(47801);
    var bytes = new byte[seconds * 48000 * 2];
    for (var i = 0; i < bytes.Length / 2; i++)
    {
        var sample = (rng.NextDouble() * 2 - 1) * .035;
        if (hum) sample += .04 * Math.Sin(2 * Math.PI * 60 * i / 48000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(sample * 32767));
    }
    return bytes;
}
static double Rms(byte[] bytes, int skip = 0)
{
    double energy = 0;
    for (var i = skip; i + 1 < bytes.Length; i += 2)
    {
        var value = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i)) / 32768.0;
        energy += value * value;
    }
    return Math.Sqrt(energy / ((bytes.Length - skip) / 2));
}
static byte[] Run(byte[] input, VoiceEnhancementOptions options, int chunk)
{
    using var processor = new VoiceEnhancer();
    using var output = new MemoryStream();
    for (var offset = 0; offset < input.Length; offset += chunk)
        output.Write(processor.Process(input.AsSpan(offset, Math.Min(chunk, input.Length - offset)), options));
    Check(processor.Status.StartsWith("Active"), processor.Status);
    return output.ToArray();
}

var noise = Noise(5);
if (args.Contains("--expect-missing-native"))
{
    using var processor = new VoiceEnhancer();
    Check(processor.Process(noise, new()).SequenceEqual(noise), "Missing native dependency must preserve original audio.");
    Check(processor.Status.StartsWith("Enhancement unavailable"), "Missing native dependency must be visible.");
    Check(processor.Process(noise, new()).SequenceEqual(noise), "Fallback must continue for later packets.");
    Console.WriteLine("Missing native dependency fallback passed.");
    return;
}
using (var processor = new VoiceEnhancer())
{
    Check(processor.Process(noise, new(Enabled: false)).SequenceEqual(noise), "Off must preserve PCM exactly.");
    processor.Configure(44100, 2);
    Check(processor.Process(noise, new()).SequenceEqual(noise), "Unsupported formats must bypass safely.");
    Check(processor.Status.StartsWith("Bypassed"), "Format bypass must be visible.");
}
var strong = new VoiceEnhancementOptions(NoiseReduction: "Strong");
var watch = Stopwatch.StartNew();
var reduced = Run(noise, strong, 1920);
watch.Stop();
Check(reduced.Length == noise.Length, "Normal packets must preserve duration.");
var attenuation = 20 * Math.Log10(Rms(noise, 96000) / Math.Max(1e-12, Rms(reduced, 96000)));
Console.WriteLine($"Steady noise reduction: {attenuation:0.0} dB; 5 seconds processed in {watch.Elapsed.TotalMilliseconds:0} ms");
Check(attenuation > 10, "Expected meaningful suppression of steady noise.");
var hum = Noise(5, hum: true);
var humOutput = Run(hum, strong, 1920);
var humAttenuation = 20 * Math.Log10(Rms(hum, 96000) / Math.Max(1e-12, Rms(humOutput, 96000)));
Console.WriteLine($"Noise with 60 Hz hum reduction: {humAttenuation:0.0} dB");
Check(humAttenuation > 10, "Hum must not defeat noise suppression.");
var fragmented = Run(noise, strong, 137);
Check(fragmented.SequenceEqual(reduced), "Arbitrary byte fragmentation must not change audio or lose samples.");
var silence = Run(new byte[48000 * 2], new(), 1920);
Check(Rms(silence) == 0, "Automatic volume must not create noise in silence.");
foreach (var strength in new[] { "Low", "Balanced", "Strong" })
foreach (var preset in new[] { "Natural", "Broadcast" })
{
    var loud = new byte[48000 * 2];
    for (var i = 0; i < loud.Length; i += 2)
        BinaryPrimitives.WriteInt16LittleEndian(loud.AsSpan(i), (short)(i % 4 == 0 ? 32767 : -32768));
    var output = Run(loud, new(NoiseReduction: strength, Preset: preset), 1920);
    for (var i = 0; i < output.Length; i += 2)
        Check(Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i))) <= 30802, "Limiter exceeded ceiling.");
}
using (var processor = new VoiceEnhancer())
{
    processor.Process(noise.AsSpan(0, 137), strong);
    processor.Configure(48000, 1);
    Check(processor.Process(new byte[960], strong).All(b => b == 0), "Session reset leaked old audio.");
}
if (args.Length > 0)
{
    var clean = File.ReadAllBytes(args[0]); // Optional 48 kHz mono PCM speech fixture.
    var noisy = clean.ToArray();
    var rng = new Random(123);
    for (var i = 0; i + 1 < noisy.Length; i += 2)
    {
        var sample = BinaryPrimitives.ReadInt16LittleEndian(noisy.AsSpan(i)) + (rng.NextDouble() * 2 - 1) * 1000;
        BinaryPrimitives.WriteInt16LittleEndian(noisy.AsSpan(i), (short)Math.Clamp(sample, -32768, 32767));
    }
    var directory = Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
    File.WriteAllBytes(Path.Combine(directory, "speech-noisy.pcm"), noisy);
    var enhanced = Run(noisy, new(), 1920);
    File.WriteAllBytes(Path.Combine(directory, "speech-enhanced.pcm"), enhanced);
    Check(Rms(enhanced) > .005, "Speech fixture was suppressed to silence.");
    Console.WriteLine($"Speech output RMS: {Rms(enhanced):0.0000}; audio comparisons saved in {directory}");
}
Console.WriteLine("Voice enhancement smoke checks passed.");
