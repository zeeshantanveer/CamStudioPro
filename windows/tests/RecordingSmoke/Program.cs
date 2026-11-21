using CamStudio.Desktop.Media;

// Native encoder smoke check; pass a workspace directory for generated artifacts.
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "recording-smoke-output");
async Task<string> Record(bool audio, bool changeResolution)
{
    var recorder = new VideoRecorder(320, 240, 30, output);
    await recorder.Ready;
    using var enhancer = new VoiceEnhancer();
    var picture = new Nv12Frame(320, 240);
    Array.Fill(picture.Data, (byte)96, 0, 320 * 240);
    Array.Fill(picture.Data, (byte)128, 320 * 240, 320 * 240 / 2);
    for (var i = 0; i < 45; i++)
    {
        recorder.PushVideo(picture);
        if (audio)
        {
            var processed = enhancer.Process(new byte[3200], new());
            if (!enhancer.Status.StartsWith("Active")) throw new Exception(enhancer.Status);
            if (processed.Length > 0) recorder.PushAudio(processed);
        }
        await Task.Delay(33);
    }
    if (changeResolution) recorder.PushVideo(new Nv12Frame(640, 480));
    recorder.Finish();
    await recorder.Completion; // Camera may open at a different size during a screen recording.
    if (new FileInfo(recorder.FilePath).Length < 1000) throw new Exception("MP4 output is missing or empty.");
    Console.WriteLine(recorder.FilePath);
    return recorder.FilePath;
}
await Record(audio: true, changeResolution: false);
await Record(audio: false, changeResolution: false);
await Record(audio: true, changeResolution: true);

foreach (var resume in new[] { true, false })
{
    var recorder = new VideoRecorder(320, 240, 30, output);
    await recorder.Ready;
    var picture = new Nv12Frame(320, 240);
    async Task Feed(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            recorder.PushVideo(picture);
            recorder.PushAudio(new byte[3200]);
            await Task.Delay(33);
        }
    }
    await Feed(18);
    recorder.SetPaused(true);
    var pausedElapsed = recorder.Elapsed;
    await Feed(45);
    if (!recorder.IsPaused || recorder.Elapsed != pausedElapsed)
        throw new Exception("The recording timer advanced while paused.");
    if (resume)
    {
        recorder.SetPaused(false);
        await Feed(18);
        if (recorder.IsPaused || recorder.Elapsed <= pausedElapsed)
            throw new Exception("The recording timer did not resume.");
    }
    recorder.Finish();
    await recorder.Completion;
    Console.WriteLine($"{(resume ? "PAUSE_RESUME" : "STOP_PAUSED")}: {recorder.FilePath}");
}
Console.WriteLine("Recording smoke checks passed.");
