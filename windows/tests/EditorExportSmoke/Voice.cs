using System.Diagnostics;
using System.Globalization;
using System.IO;
using CamStudio.Editor.Engine;
using CamStudio.Editor.Model;

/// <summary>
/// "Enhance voice" checks on real speech (Windows text-to-speech): noise removal (speech model vs spectral),
/// two-pass loudness targets, de-esser, compressor, click/clipping repair, music ducking, pause removal
/// and project JSON compatibility. Prints the measured numbers.
/// </summary>
static class VoiceTests
{
    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static async Task<List<string>> RunAsync(string work)
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails.Add(what); }
        Console.WriteLine("voice: generating speech with Windows text-to-speech…");

        // ---------------------------------------------------------------- material
        var s1 = Path.Combine(work, "tts1.wav");
        var s2 = Path.Combine(work, "tts2.wav");
        var s3 = Path.Combine(work, "tts3.wav");
        await Tts("She sells sea shells by the seashore and the shells she sells are surely seashells sold to sixty sisters", s1);
        await Tts("This second sentence is spoken much more quietly than the others, almost a whisper.", s2);
        await Tts("Success is simply the sum of small efforts, repeated day in and day out, said the speaker.", s3);
        var d1 = await Duration(s1);
        var d2 = await Duration(s2);
        var d3 = await Duration(s3);
        const double pause = 2.0;
        var total = d1 + pause + d2 + pause + d3;
        // Regions (seconds) in the assembled speech.
        var p1 = (Start: d1, End: d1 + pause);
        var p2 = (Start: d1 + pause + d2, End: d1 + pause + d2 + pause);
        var sentence1 = (Start: 0.2, End: d1 - 0.2);
        var sentence3 = (Start: p2.End + 0.2, End: total - 0.2);

        var clean = Path.Combine(work, "speech-clean.wav");
        await Ff("-i", s1, "-i", s2, "-i", s3, "-filter_complex",
            $"[0]aresample=48000,aformat=channel_layouts=mono,apad=pad_dur={Num(pause)}[a];" +
            $"[1]aresample=48000,aformat=channel_layouts=mono,volume=-12dB,apad=pad_dur={Num(pause)}[b];" +
            "[2]aresample=48000,aformat=channel_layouts=mono[c];[a][b][c]concat=n=3:v=0:a=1",
            "-c:a", "pcm_s16le", clean);
        // Noisy: pink noise, 50 Hz hum, clicks every 1.37 s, and the first sentence driven into clipping.
        var noisy = Path.Combine(work, "speech-noisy.wav");
        await Ff("-i", clean, "-filter_complex",
            $"[0]volume='if(lt(t,{Num(d1)}),3.5,1)':eval=frame[sp];" +
            $"anoisesrc=c=pink:a=0.025:r=48000:d={Num(total)}[n];" +
            $"sine=f=50:sample_rate=48000:d={Num(total)},volume=0.05[h];" +
            $"aevalsrc='if(lt(mod(t,1.37),0.0004),0.8,0)':s=48000:d={Num(total)}[k];" +
            "[sp][n][h][k]amix=inputs=4:normalize=0:duration=first",
            "-ac", "1", "-c:a", "pcm_s16le", noisy);
        var speechVideo = Path.Combine(work, "speech-clean.mp4");
        await Ff("-f", "lavfi", "-i", $"color=c=0x223344:s=640x360:r=30:d={Num(total)}", "-i", clean,
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "256k", "-shortest", speechVideo);
        Console.WriteLine($"  speech: {Num(total)} s (sentences {Num(d1)} / {Num(d2)} / {Num(d3)} s, two {Num(pause)} s pauses)");

        EditorProject AudioProject(string file, Action<MusicClip> set, LoudnessTarget target = LoudnessTarget.Off)
        {
            var p = new EditorProject { Name = "voice", LoudnessTarget = target };
            var m = new MusicClip { Path = file, SourceDuration = total, Start = 0, In = 0, Out = total, Volume = 1, FadeIn = 0, FadeOut = 0 };
            set(m);
            p.Music.Add(m);
            return p;
        }
        async Task<string> ExportWav(EditorProject p, string name)
        {
            var file = Path.Combine(work, name);
            await ExportBuilder.ExportAsync(p, new ExportOptions(0, 0, 30, ExportQuality.Standard, "", file, AudioFormat.Wav), null, CancellationToken.None);
            return file;
        }

        // ---------------------------------------------------------------- 0. quiet and uneven speech (user report: "too quiet / uneven")
        // Sentences alternate loud / quiet (−11 dB, like moving away from the mic), the whole thing ≈ −36 LUFS,
        // with a little mic noise. Enhance voice (Podcast) + YouTube target must make it loud enough and even.
        var uneven = Path.Combine(work, "speech-quiet-uneven.wav");
        const double gapPause = 1.5;
        await Ff("-i", s1, "-i", s2, "-i", s3, "-filter_complex",
            $"[0]aresample=48000,aformat=channel_layouts=mono,apad=pad_dur={Num(gapPause)}[a];" +
            $"[1]aresample=48000,aformat=channel_layouts=mono,volume=-11dB,apad=pad_dur={Num(gapPause)}[b];" +
            "[2]aresample=48000,aformat=channel_layouts=mono[c];[a][b][c]concat=n=3:v=0:a=1,volume=-16dB[sp];" +
            $"anoisesrc=c=pink:a=0.0015:r=48000:d={Num(d1 + d2 + d3 + 2 * gapPause + 1)}[n];[sp][n]amix=inputs=2:normalize=0:duration=first",
            "-c:a", "pcm_s16le", uneven);
        var u1 = (Start: 0.3, End: d1 - 0.3);
        var u2 = (Start: d1 + gapPause + 0.3, End: d1 + gapPause + d2 - 0.3);
        var u3 = (Start: d1 + gapPause + d2 + gapPause + 0.3, End: d1 + gapPause + d2 + gapPause + d3 - 0.3);
        var uPauses = new[] { (Start: d1 + 0.25, End: d1 + gapPause - 0.25), (Start: d1 + gapPause + d2 + 0.25, End: d1 + gapPause + d2 + gapPause - 0.25) };
        async Task<(double Loud, double Quiet, double Gap, double Floor)> Levels(string file)
        {
            var a = await Mean(file, u1.Start, u1.End - u1.Start);
            var b = await Mean(file, u2.Start, u2.End - u2.Start);
            var c = await Mean(file, u3.Start, u3.End - u3.Start);
            var f = (await Mean(file, uPauses[0].Start, uPauses[0].End - uPauses[0].Start) + await Mean(file, uPauses[1].Start, uPauses[1].End - uPauses[1].Start)) / 2;
            var loud = (a + c) / 2;
            return (loud, b, loud - b, f);
        }
        var uIn = await Loudness.MeasureFileAsync(uneven);
        var uInLv = await Levels(uneven);
        EditorProject UnevenProject(bool enhance, VoiceStyle style = VoiceStyle.Podcast) =>
            AudioProject(uneven, m => { m.Enhance = enhance; m.Style = style; m.NoiseReduction = NoiseLevel.Medium; }, LoudnessTarget.YouTube);
        var uRefFile = await ExportWav(UnevenProject(false), "uneven-ref.wav");      // enhance off, but normalized
        var uEnhFile = await ExportWav(UnevenProject(true), "uneven-enhanced.wav");  // Enhance voice (Podcast)
        var uRef = await Loudness.MeasureFileAsync(uRefFile);
        var uEnh = await Loudness.MeasureFileAsync(uEnhFile);
        var refLv = await Levels(uRefFile);
        var enhLv = await Levels(uEnhFile);
        Console.WriteLine($"  quiet & uneven input:      {uIn}; loud sentences {uInLv.Loud:0.0} dB, quiet {uInLv.Quiet:0.0} dB (gap {uInLv.Gap:0.0} dB), pause floor {uInLv.Floor:0.0} dB");
        Console.WriteLine($"  enhance off, YouTube:      {uRef}; gap {refLv.Gap:0.0} dB, pause floor {refLv.Floor:0.0} dB");
        Console.WriteLine($"  Enhance (Podcast), YouTube: {uEnh}; gap {enhLv.Gap:0.0} dB, pause floor {enhLv.Floor:0.0} dB");
        foreach (var style in new[] { VoiceStyle.Natural, VoiceStyle.Broadcast })
        {
            var f = await ExportWav(UnevenProject(true, style), $"uneven-{style}.wav");
            var lv = await Levels(f);
            Console.WriteLine($"  Enhance ({style}), YouTube: gap {lv.Gap:0.0} dB, pause floor {lv.Floor:0.0} dB");
        }
        Check(uEnh != null && Math.Abs(uEnh.Integrated + 14) <= 1, $"loud enough: −14 ±1 LUFS ({uEnh?.Integrated:0.0})");
        Check(uEnh != null && uEnh.TruePeak <= -0.9, $"true peak ≤ −1 dBTP ({uEnh?.TruePeak:0.0})");
        Check(enhLv.Gap <= 3, $"loud vs quiet sentences within 3 dB ({uInLv.Gap:0.0} → {enhLv.Gap:0.0} dB)");
        Check(enhLv.Floor <= refLv.Floor + 3, $"no noise pumping: pause floor {enhLv.Floor:0.0} dB vs {refLv.Floor:0.0} dB without Enhance");

        // ---------------------------------------------------------------- 1. noise removal: speech model vs spectral
        Console.WriteLine($"voice: speech model {(VoiceChain.ModelPath != null ? "found: " + VoiceChain.ModelPath : "MISSING")}");
        Check(VoiceChain.ModelPath != null, "RNNoise speech model is bundled next to the editor");
        var rawFloor = await Mean(noisy, p1.Start + 0.4, pause - 0.8);
        var rawSpeech = await Mean(noisy, sentence3.Start, sentence3.End - sentence3.Start);
        var results = new Dictionary<string, (double Floor, double Speech)>();
        foreach (var (engine, label) in new[] { (NoiseEngine.SpeechModel, "speech model (arnndn)"), (NoiseEngine.Spectral, "spectral (afftdn)") })
        {
            VoiceChain.Engine = engine;
            foreach (var level in new[] { NoiseLevel.Light, NoiseLevel.Medium, NoiseLevel.Strong })
            {
                var f = await ExportWav(AudioProject(noisy, m => { m.Enhance = true; m.NoiseReduction = level; }), $"voice-{engine}-{level}.wav");
                var floor = await Mean(f, p1.Start + 0.4, pause - 0.8);
                var speech = await Mean(f, sentence3.Start, sentence3.End - sentence3.Start);
                results[$"{engine}/{level}"] = (floor, speech);
                Console.WriteLine($"  {label,-22} {level,-6}: pause floor {rawFloor:0.0} → {floor:0.0} dB ({rawFloor - floor:0.0} dB less), speech {rawSpeech:0.0} → {speech:0.0} dB");
            }
        }
        VoiceChain.Engine = NoiseEngine.Auto;
        var chosen = VoiceChain.UsesSpeechModel ? NoiseEngine.SpeechModel : NoiseEngine.Spectral;
        var med = results[$"{chosen}/Medium"];
        var other = results[$"{(chosen == NoiseEngine.SpeechModel ? NoiseEngine.Spectral : NoiseEngine.SpeechModel)}/Medium"];
        Console.WriteLine($"  Auto uses {chosen}: Medium removes {rawFloor - med.Floor:0.0} dB of noise vs {rawFloor - other.Floor:0.0} dB for the other engine");
        var strong = results[$"{chosen}/Strong"];
        double Snr((double Floor, double Speech) x) => x.Speech - x.Floor;
        var rawSnr = rawSpeech - rawFloor;
        Console.WriteLine($"  speech-to-noise: raw {rawSnr:0.0} dB → Medium {Snr(med):0.0} dB, Strong {Snr(strong):0.0} dB");
        Check(rawFloor - med.Floor >= 10, $"Medium: noise in pauses drops ≥ 10 dB ({rawFloor - med.Floor:0.0} dB)");
        Check(rawFloor - strong.Floor >= 13, $"Strong: noise in pauses drops ≥ 13 dB ({rawFloor - strong.Floor:0.0} dB)");
        // Does noise removal eat speech? Same chain with noise reduction off vs Medium, compared on speech level.
        var noNr = await ExportWav(AudioProject(noisy, m => { m.Enhance = true; m.NoiseReduction = NoiseLevel.Off; }), "voice-enhance-no-nr.wav");
        var noNrSpeech = await Mean(noNr, sentence3.Start, sentence3.End - sentence3.Start);
        Console.WriteLine($"  speech level with Enhance: noise reduction off {noNrSpeech:0.0} dB, Medium {med.Speech:0.0} dB");
        Check(Math.Abs(med.Speech - noNrSpeech) <= 2, $"noise removal keeps the speech level (within ±2 dB: {med.Speech - noNrSpeech:+0.0;-0.0} dB)");
        Check(rawFloor - med.Floor >= rawFloor - other.Floor - 1, "the chosen engine removes at least as much noise as the other");

        // ---------------------------------------------------------------- 2. two-pass loudness targets
        foreach (var (target, goal) in new[] { (LoudnessTarget.YouTube, -14.0), (LoudnessTarget.Podcast, -16.0) })
        {
            var f = await ExportWav(AudioProject(noisy, m => { m.Enhance = true; m.Style = VoiceStyle.Podcast; m.NoiseReduction = NoiseLevel.Medium; }, target), $"voice-loud-{target}.wav");
            var r = await Loudness.MeasureFileAsync(f);
            Console.WriteLine($"  loudness target {target}: {r}");
            Check(r != null && Math.Abs(r.Integrated - goal) <= 1, $"{target} lands within ±1 LU of {goal} LUFS ({r?.Integrated:0.0})");
            Check(r != null && r.TruePeak <= -0.9, $"{target} true peak ≤ −1 dBTP ({r?.TruePeak:0.0})");
        }

        // ---------------------------------------------------------------- 3. de-esser
        VoiceChain.DeEsser = false;
        var noDeEss = await ExportWav(AudioProject(noisy, m => { m.Enhance = true; m.Style = VoiceStyle.Broadcast; m.NoiseReduction = NoiseLevel.Medium; }), "voice-deess-off.wav");
        VoiceChain.DeEsser = true;
        var deEss = await ExportWav(AudioProject(noisy, m => { m.Enhance = true; m.Style = VoiceStyle.Broadcast; m.NoiseReduction = NoiseLevel.Medium; }), "voice-deess-on.wav");
        var sibOff = await Mean(noDeEss, sentence3.Start, sentence3.End - sentence3.Start, "highpass=f=6000,lowpass=f=10000");
        var sibOn = await Mean(deEss, sentence3.Start, sentence3.End - sentence3.Start, "highpass=f=6000,lowpass=f=10000");
        Console.WriteLine($"  de-esser: 6–10 kHz energy on sibilant speech {sibOff:0.0} → {sibOn:0.0} dB");
        Check(sibOn < sibOff - 0.5, $"de-esser reduces 6–10 kHz energy ({sibOff - sibOn:0.0} dB)");

        // ---------------------------------------------------------------- 4. compressor: loudness range
        var plain = await ExportWav(AudioProject(noisy, m => { m.NoiseReduction = NoiseLevel.Medium; }), "voice-plain.wav");
        var lraPlain = (await Loudness.MeasureFileAsync(plain))?.Range ?? double.NaN;
        var lraEnh = (await Loudness.MeasureFileAsync(deEss))?.Range ?? double.NaN;
        Console.WriteLine($"  compressor: loudness range {lraPlain:0.0} LU (noise reduction only) → {lraEnh:0.0} LU (Broadcast)");
        Check(lraEnh < lraPlain - 1, $"compression narrows the loudness range ({lraPlain - lraEnh:0.0} LU)");

        // ---------------------------------------------------------------- 5. click & clipping repair
        var (clipRaw, spikeRaw) = await Damage(noisy);
        var enhanced = Path.Combine(work, $"voice-{NoiseEngine.SpeechModel}-Medium.wav");
        VoiceChain.Engine = NoiseEngine.Auto;
        var autoFile = await ExportWav(AudioProject(noisy, m => { m.Enhance = true; m.NoiseReduction = NoiseLevel.Medium; }), "voice-auto.wav");
        var (clipEnh, spikeEnh) = await Damage(autoFile);
        Console.WriteLine($"  repair: samples at full scale {clipRaw} → {clipEnh}; impulse jumps {spikeRaw} → {spikeEnh}");
        Check(clipEnh < clipRaw / 10.0 + 1, "clipped samples are gone");
        Check(spikeEnh < spikeRaw / 4.0 + 1, "click impulses are removed");

        // ---------------------------------------------------------------- 6. music ducking
        var tone = Path.Combine(work, "music-14k.wav");
        await Ff("-f", "lavfi", "-i", $"sine=f=14000:sample_rate=48000:d={Num(total + 1)}", "-af", "volume=-20dB", "-c:a", "pcm_s16le", tone);
        EditorProject DuckProject(bool duck)
        {
            var p = new EditorProject { Name = "duck", Width = 640, Height = 360, DuckMusic = duck, DuckAmount = DuckAmount.Medium, LoudnessTarget = LoudnessTarget.Off };
            p.Video.Add(new VideoClip { Path = speechVideo, HasAudio = true, SourceDuration = total, In = 0, Out = total });
            p.Music.Add(new MusicClip { Path = tone, SourceDuration = total + 1, Start = 0, In = 0, Out = total, Volume = 1, FadeIn = 0, FadeOut = 0 });
            p.Layout();
            return p;
        }
        foreach (var duck in new[] { true, false })
        {
            var f = await ExportWav(DuckProject(duck), $"voice-duck-{duck}.wav");
            // A steep, narrow band around the 14 kHz music tone: speech leaks nothing into it (−91 dB).
            const string band = "bandpass=f=14000:width_type=h:w=300,bandpass=f=14000:width_type=h:w=300,bandpass=f=14000:width_type=h:w=300,bandpass=f=14000:width_type=h:w=300";
            var during = await Mean(f, sentence3.Start, sentence3.End - sentence3.Start, band);
            // Measure the pause after the release has let the music come back (release 600 ms).
            var between = await Mean(f, p1.Start + 1.0, pause - 1.2, band);
            Console.WriteLine($"  ducking {(duck ? "on " : "off")}: music during speech {during:0.0} dB, in pauses {between:0.0} dB ({between - during:0.0} dB drop)");
            if (duck) Check(between - during >= 6, $"music drops ≥ 6 dB while speaking ({between - during:0.0} dB)");
            else Check(Math.Abs(between - during) < 2, $"no ducking when switched off ({between - during:0.0} dB)");
        }

        // ---------------------------------------------------------------- 7. remove pauses
        var vp = new EditorProject { Name = "pauses", Width = 640, Height = 360 };
        vp.Video.Add(new VideoClip { Path = speechVideo, HasAudio = true, SourceDuration = total, In = 0, Out = total });
        vp.Layout();
        var before = vp.Duration;
        var cuts = await PauseDetector.FindAsync(speechVideo, 0, total);
        Console.WriteLine($"  pauses found: {string.Join(", ", cuts.Select(c => $"{c.Start:0.00}–{c.End:0.00}"))}");
        var history = new History();
        history.Record(vp);
        var (count, removed) = vp.RemoveRanges(vp.Video[0], cuts);
        // Independent reference: the real silent spans of the clean speech (10 ms RMS below −60 dBFS).
        var silences = await SilentRuns(clean, 0.7);
        var trueInner = silences.Where(r => r.Start < p1.End && r.End > p1.Start || r.Start < p2.End && r.End > p2.Start).ToList();
        var expected = trueInner.Sum(r => r.End - r.Start - 2 * PauseDetector.Padding);
        Console.WriteLine($"  true pauses (clean speech): {string.Join(", ", trueInner.Select(r => $"{r.Start:0.00}–{r.End:0.00}"))}");
        bool Inside((double Start, double End) c, (double Start, double End) region) => c.Start < region.End && c.End > region.Start;
        var inner = cuts.Where(c => Inside(c, p1) || Inside(c, p2)).ToList();
        var innerSeconds = inner.Sum(c => c.End - c.Start);
        var inSpeech = cuts.Where(c => Inside(c, (sentence1.Start + 0.5, sentence1.End - 0.5)) || Inside(c, (sentence3.Start + 0.5, sentence3.End - 0.5))).ToList();
        Console.WriteLine($"  removed {count} pauses, {removed:0.00} s in total; the two inserted pauses: {innerSeconds:0.00} s " +
                          $"(true pauses minus padding: {expected:0.00} s); timeline {before:0.00} → {vp.Duration:0.00} s");
        Check(inner.Count == 2, $"finds both inserted pauses ({inner.Count}; {count} cuts in total including dead air at the ends)");
        Check(trueInner.Count == 2 && Math.Abs(innerSeconds - expected) <= 0.4, $"cuts ≈ the pauses minus padding ({innerSeconds:0.00} s vs {expected:0.00} s, ±0.4)");
        Check(inSpeech.Count == 0, "nothing is cut from inside a sentence");
        var pausedOut = await ExportWav(vp, "voice-pauses-removed.wav");
        var outDur = await Duration(pausedOut);
        Check(Math.Abs(outDur - vp.Duration) < 0.15, $"export is the shorter length ({outDur:0.00} s)");
        var undone = history.Undo(vp);
        Check(undone != null && undone.Video.Count == 1 && Math.Abs(undone.Duration - before) < 0.01, "undo restores the clip");
        // Music track: parts close up and a later music clip moves earlier.
        var mp = new EditorProject { Name = "pauses-music" };
        var speechClip = new MusicClip { Path = clean, SourceDuration = total, Start = 1, In = 0, Out = total, Volume = 1 };
        var later = new MusicClip { Path = clean, SourceDuration = total, Start = 1 + total + 0.5, In = 0, Out = 2, Volume = 1 };
        mp.Music.AddRange(new[] { speechClip, later });
        var mcuts = await PauseDetector.FindAsync(clean, 0, total);
        var (mc, ms) = mp.RemoveRanges(speechClip, mcuts);
        var parts = mp.Music.Where(m => m != later).OrderBy(m => m.Start).ToList();
        var contiguous = parts.Zip(parts.Skip(1), (a, b) => Math.Abs(a.End - b.Start) < 1e-6).All(x => x);
        Check(mc >= 2 && contiguous && Math.Abs(later.Start - (1 + total + 0.5 - ms)) < 1e-6, $"music: {mc} pauses cut, parts close up, later clip moves {ms:0.00} s earlier");

        // ---------------------------------------------------------------- 8. JSON compatibility
        var legacy = "{\"FormatVersion\":1,\"Name\":\"old\",\"Video\":[{\"Path\":\"x.mp4\",\"HasAudio\":true,\"In\":0,\"Out\":2,\"NoiseReduction\":\"Light\"}],\"Music\":[{\"Path\":\"m.m4a\",\"In\":0,\"Out\":2}]}";
        var old = EditorProject.FromJson(legacy);
        Check(!old.Video[0].Enhance && old.Video[0].Style == VoiceStyle.Natural && old.LoudnessTarget == LoudnessTarget.Off && !old.DuckMusic
              && old.Video[0].NoiseReduction == NoiseLevel.Light, "old project loads with the new features off (ducking off, as it exported before)");
        var fresh = new EditorProject { LoudnessTarget = LoudnessTarget.Podcast, DuckAmount = DuckAmount.Strong };
        fresh.Video.Add(new VideoClip { Path = "y.mp4", Enhance = true, Style = VoiceStyle.Broadcast });
        fresh.Music.Add(new MusicClip { Path = "z.m4a", Enhance = true, Style = VoiceStyle.Podcast });
        var back = EditorProject.FromJson(fresh.ToJson());
        Check(back.LoudnessTarget == LoudnessTarget.Podcast && back.DuckMusic && back.DuckAmount == DuckAmount.Strong
              && back.Video[0].Enhance && back.Video[0].Style == VoiceStyle.Broadcast && back.Music[0].Style == VoiceStyle.Podcast,
              "new voice and loudness fields round-trip");
        Check(new EditorProject().DuckMusic, "new projects duck music by default");
        Check(new EditorProject().LoudnessTarget == LoudnessTarget.YouTube, "new projects export at YouTube loudness (−14 LUFS) by default");

        // ---------------------------------------------------------------- 9. meter on a timeline mix
        var meterProject = AudioProject(noisy, m => { m.Enhance = true; m.NoiseReduction = NoiseLevel.Medium; });
        var meter = await ExportBuilder.MeasureMixAsync(meterProject, new ExportOptions(0, 0, 30, ExportQuality.Standard, "", "", AudioFormat.Wav), null, CancellationToken.None);
        var fileReading = await Loudness.MeasureFileAsync(autoFile);
        Console.WriteLine($"  meter (timeline mix): {meter} · {(meter != null ? Loudness.Verdict(meter, LoudnessTarget.Off) : "-")}; exported file: {fileReading}");
        Check(meter != null && fileReading != null && Math.Abs(meter.Integrated - fileReading.Integrated) <= 0.5,
              "meter matches the exported file (±0.5 LU)");
        return fails;
    }

    // ---------------------------------------------------------------- helpers

    private static async Task Tts(string text, string wav)
    {
        var script = "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                     $"$s.SetOutputToWaveFile('{wav.Replace("'", "''")}'); $s.Speak('{text.Replace("'", "''")}'); $s.Dispose()";
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 || !File.Exists(wav)) throw new Exception("Text-to-speech failed: " + err);
    }

    private static async Task Ff(params string[] a)
    {
        var r = await FFmpeg.RunAsync(FFmpeg.FfmpegPath!, new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(a));
        if (r.ExitCode != 0) throw new Exception("ffmpeg: " + r.Stderr);
    }

    private static async Task<double> Duration(string file)
    {
        var r = await FFmpeg.RunAsync(FFmpeg.FfprobePath!, new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file });
        return double.Parse(r.StdoutText.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>Mean volume (dB) of a region, optionally through a band filter.</summary>
    private static async Task<double> Mean(string file, double from, double length, string? filter = null)
    {
        var af = (filter != null ? filter + "," : "") + "volumedetect";
        var r = await FFmpeg.RunAsync(FFmpeg.FfmpegPath!, new[]
            { "-hide_banner", "-ss", Num(from), "-t", Num(length), "-i", file, "-af", af, "-f", "null", "-" });
        var line = r.Stderr.Split('\n').FirstOrDefault(l => l.Contains("mean_volume:"));
        if (line == null) return double.NaN;
        return double.Parse(line[(line.IndexOf("mean_volume:") + 12)..].Replace("dB", "").Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>Runs of 10 ms windows quieter than −60 dBFS lasting at least <paramref name="min"/> seconds.</summary>
    private static async Task<List<(double Start, double End)>> SilentRuns(string file, double min)
    {
        var r = await FFmpeg.RunAsync(FFmpeg.FfmpegPath!, new[] { "-v", "error", "-i", file, "-ac", "1", "-ar", "48000", "-f", "s16le", "-" });
        var b = r.Stdout;
        const int win = 480;
        var runs = new List<(double, double)>();
        int? start = null;
        var windows = b.Length / 2 / win;
        for (var w = 0; w <= windows; w++)
        {
            var silent = false;
            if (w < windows)
            {
                double sum = 0;
                for (var i = 0; i < win; i++) { var s = (short)(b[(w * win + i) * 2] | b[(w * win + i) * 2 + 1] << 8) / 32768.0; sum += s * s; }
                silent = 10 * Math.Log10(sum / win + 1e-12) < -60;
            }
            if (silent && start == null) start = w;
            if (!silent && start is int s0)
            {
                if ((w - s0) * 0.01 >= min) runs.Add((s0 * 0.01, w * 0.01));
                start = null;
            }
        }
        return runs;
    }

    /// <summary>Samples stuck at full scale (clipping) and sample-to-sample jumps over half scale (clicks).</summary>
    private static async Task<(int Clipped, int Spikes)> Damage(string file)
    {
        var r = await FFmpeg.RunAsync(FFmpeg.FfmpegPath!, new[] { "-v", "error", "-i", file, "-ac", "1", "-f", "s16le", "-" });
        var b = r.Stdout;
        int clipped = 0, spikes = 0, prev = 0;
        for (var i = 0; i + 1 < b.Length; i += 2)
        {
            int s = (short)(b[i] | b[i + 1] << 8);
            if (Math.Abs(s) >= 32700) clipped++;
            if (Math.Abs(s - prev) > 16384) spikes++;
            prev = s;
        }
        return (clipped, spikes);
    }
}
