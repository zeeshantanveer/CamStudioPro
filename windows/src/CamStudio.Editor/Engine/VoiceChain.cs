using System.Globalization;
using System.IO;
using CamStudio.Editor.Model;

namespace CamStudio.Editor.Engine;

/// <summary>Every audio clean-up setting of one clip (the preview-proxy cache key uses all of them).</summary>
public sealed record AudioCleanup(NoiseLevel Noise, bool RemoveHum, bool Normalize, bool Enhance, VoiceStyle Style)
{
    public static AudioCleanup From(IAudioClip c) => new(c.NoiseReduction, c.RemoveHum, c.Normalize, c.Enhance, c.Style);
    public bool Any => Noise != NoiseLevel.Off || RemoveHum || Normalize || Enhance;
}

/// <summary>Which noise remover to use (Auto = speech model when its file is present).</summary>
public enum NoiseEngine { Auto, SpeechModel, Spectral }

/// <summary>
/// Builds the per-clip voice chain. Order (each step only when enabled):
///   repair (adeclick, adeclip) → rumble high-pass 80 Hz → mains-hum notches (Hum filter or Enhance) →
///   speech levelling (dynaudnorm, Enhance) → noise removal (speech model with Enhance, else spectral) →
///   mud cut → presence boost → air shelf → de-esser → compressor → safety limiter → per-clip loudness.
///
/// Levelling evens out speech that comes and goes (someone moving away from the mic). It runs BEFORE
/// noise removal so the noise it lifts in pauses is removed afterwards: on quiet speech (−36 LUFS) with
/// sentences 12 dB apart, the gap shrank to 1.6–2.4 dB and the pause noise floor (at equal loudness) went
/// down, not up. A fixed-threshold compressor alone left the 12 dB gap untouched (quiet speech never
/// reaches its threshold), and speechnorm only reached ~8 dB; dynaudnorm was picked by those numbers.
///
/// "Enhance voice" styles only change strengths:
///
///   | step                      | Natural          | Podcast          | Broadcast        |
///   |---------------------------|------------------|------------------|------------------|
///   | levelling (dynaudnorm)    | f500 g31 max ×8  | f200 g31 max ×20 | f150 g15 max ×30 |
///   | mud cut @ 250 Hz          | −2 dB (Q 1.0)    | −3 dB            | −4 dB            |
///   | presence @ 3.5 kHz        | +2 dB (Q 0.9)    | +3 dB            | +4 dB            |
///   | air shelf @ 10 kHz        | +1.5 dB          | +2.5 dB          | +3 dB            |
///   | de-esser intensity        | 0.30             | 0.45             | 0.60             |
///   | compressor (peaks)        | 2:1 @ −18 dBFS   | 3:1 @ −20 dBFS   | 4:1 @ −22 dBFS   |
///
/// The leveller sets the level, so the compressor has no make-up gain. Attack 10 ms / release 150 ms; a
/// −0.4 dBFS limiter catches anything the boosts push up. The project loudness target sets the final level.
/// </summary>
public static class VoiceChain
{
    public sealed record StyleNumbers(string Leveller, double MudDb, double PresenceDb, double AirDb, double DeEss, double Ratio, double ThresholdDb, double MakeupDb);

    public static readonly IReadOnlyDictionary<VoiceStyle, StyleNumbers> Styles = new Dictionary<VoiceStyle, StyleNumbers>
    {
        [VoiceStyle.Natural] = new("dynaudnorm=f=500:g=31:p=0.9:m=8", -2, 2, 1.5, 0.30, 2, -18, 0),
        [VoiceStyle.Podcast] = new("dynaudnorm=f=200:g=31:p=0.9:m=20", -3, 3, 2.5, 0.45, 3, -20, 0),
        [VoiceStyle.Broadcast] = new("dynaudnorm=f=150:g=15:p=0.9:m=30", -4, 4, 3.0, 0.60, 4, -22, 0),
    };

    /// <summary>Tests can switch levelling off to measure what it does.</summary>
    public static bool Leveller { get; set; } = true;

    /// <summary>Tests can force a noise remover to compare them; the app uses Auto.</summary>
    public static NoiseEngine Engine { get; set; } = NoiseEngine.Auto;

    /// <summary>Tests can switch the de-esser off to measure what it does.</summary>
    public static bool DeEsser { get; set; } = true;

    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The bundled RNNoise speech model (Assets/rnnoise/sh.rnnn next to the app), or null.</summary>
    public static string? ModelPath { get; } = FindModel();

    private static string? FindModel()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(VoiceChain).Assembly.Location) ?? "" })
        {
            var path = Path.Combine(dir, "Assets", "rnnoise", "sh.rnnn");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>True when the speech model is available (and not switched off for a test).</summary>
    public static bool UsesSpeechModel => Engine != NoiseEngine.Spectral && ModelPath != null;

    /// <summary>
    /// The speech model is only used for voice: with "Enhance voice" on (or forced by a test). A speech model
    /// treats music and tones as noise, so plain "Noise reduction" keeps the general spectral remover.
    /// </summary>
    public static bool SpeechModelFor(AudioCleanup c) =>
        UsesSpeechModel && (c.Enhance || Engine == NoiseEngine.SpeechModel);

    /// <param name="noiseFloorDb">Measured noise floor of the source (see <see cref="NoiseProfile"/>); null = typical default.</param>
    public static string? Build(AudioCleanup c, double? noiseFloorDb)
    {
        var parts = new List<string>();
        if (c.Enhance)
        {
            // Repair first: clicks/pops, then flattened (clipped) peaks.
            parts.Add("adeclick");
            parts.Add("adeclip");
        }
        if (c.RemoveHum || c.Enhance)
            parts.Add("highpass=f=80"); // rumble, handling noise, plosive thumps
        if (c.RemoveHum || c.Enhance)
            foreach (var f in new[] { 50, 60, 100, 120 })
                parts.Add($"bandreject=f={f}:width_type=q:w=12"); // mains hum and first harmonic

        if (c.Enhance && Leveller)
            parts.Add(Styles[c.Style].Leveller); // before noise removal: lifted pause noise is removed next

        if (c.Noise != NoiseLevel.Off)
        {
            var floor = noiseFloorDb ?? NoiseProfile.DefaultFloorDb;
            if (SpeechModelFor(c))
            {
                // Speech-trained RNNoise model. Measured on noisy speech (pink noise ≈ −36 dBFS, smoke test):
                // one pass at 80 % ≈ −7 dB of noise, one pass + 70 % second pass ≈ −14 dB, two passes ≈ −17 dB,
                // speech level unchanged in all three. A spectral pass after the model added < 0.5 dB, so none.
                var model = $"arnndn=m={ExportBuilder.FilterPath(ModelPath!)}";
                switch (c.Noise)
                {
                    case NoiseLevel.Light: parts.Add(model + ":mix=0.8"); break;
                    case NoiseLevel.Medium: parts.Add(model); parts.Add(model + ":mix=0.7"); break;
                    default: parts.Add(model); parts.Add(model); break;
                }
            }
            else
            {
                // Spectral noise reduction aimed at the measured noise floor; strength = dB of reduction.
                var nf = NoiseProfile.AfftdnFloor(floor);
                var nr = c.Noise switch { NoiseLevel.Light => 10, NoiseLevel.Medium => 18, _ => 28 };
                parts.Add($"afftdn=nr={nr}:nf={N(nf)}");
            }
        }

        if (c.Enhance)
        {
            var s = Styles[c.Style];
            parts.Add($"equalizer=f=250:width_type=q:w=1.0:g={N(s.MudDb)}");       // less boxy / muddy
            parts.Add($"equalizer=f=3500:width_type=q:w=0.9:g={N(s.PresenceDb)}");  // clarity, intelligibility
            parts.Add($"highshelf=f=10000:g={N(s.AirDb)}");                         // air
            if (DeEsser) parts.Add($"deesser=i={N(s.DeEss)}:m=0.5:f=0.5:s=o");     // tame harsh "s" sounds
            parts.Add($"acompressor=threshold={N(Db(s.ThresholdDb))}:ratio={N(s.Ratio)}:attack=10:release=150:knee=4:makeup={N(Db(s.MakeupDb))}");
            parts.Add("alimiter=limit=0.95:level=disabled");
        }
        if (c.Normalize) parts.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    private static double Db(double db) => Math.Pow(10, db / 20);
}
