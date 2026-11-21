using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DirectN;

namespace CamStudio.Desktop.Media;

/// <summary>Encodes the received picture and phone microphone to an MP4 on a dedicated worker.</summary>
public sealed class VideoRecorder
{
    private sealed record Packet(byte[] Data, long Time, bool Audio);
    private readonly Channel<Packet> _packets = Channel.CreateBounded<Packet>(120);
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private readonly int _width, _height, _fps;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _inputError;
    private bool _finished;
    private bool _paused;

    public static string RecordingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "CamStudio Pro");
    public string FilePath { get; }
    public Task Ready => _ready.Task;
    public Task Completion { get; }
    public TimeSpan Elapsed { get { lock (_gate) return _clock.Elapsed; } }
    public bool IsPaused { get { lock (_gate) return _paused; } }

    public VideoRecorder(int width, int height, int fps, string? outputDirectory = null)
    {
        _width = width;
        _height = height;
        _fps = Math.Clamp(fps, 1, 120);
        outputDirectory ??= RecordingsDirectory;
        Directory.CreateDirectory(outputDirectory);
        FilePath = Path.Combine(outputDirectory, $"CamStudio-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}-{Guid.NewGuid().ToString("N")[..6]}.mp4");
        Completion = Task.Run(Encode);
    }

    public void PushVideo(Nv12Frame frame)
    {
        lock (_gate)
        {
            if (_finished || _paused || !_clock.IsRunning) return;
            var time = _clock.Elapsed.Ticks;
            var data = frame.Data;
            if (frame.Width != _width || frame.Height != _height)
            {
                data = new byte[_width * _height * 3 / 2];
                Nv12Scaler.Fit(frame, data, _width, _height);
            }
            Enqueue(new Packet(data, time, false));
        }
    }

    public void PushAudio(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            if (_finished || _paused || !_clock.IsRunning) return;
            Enqueue(new Packet(pcm.ToArray(), _clock.Elapsed.Ticks, true));
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_finished || !_ready.Task.IsCompletedSuccessfully || _paused == paused) return;
            _paused = paused;
            if (paused) _clock.Stop();
            else _clock.Start();
        }
    }

    // Called under _gate so timestamps, pause, and queue admission agree across producers.
    private void Enqueue(Packet packet)
    {
        if (!_packets.Writer.TryWrite(packet))
        {
            _inputError = new IOException("Recording could not keep up with the stream. The captured portion has been saved.");
            Finish();
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            _finished = true;
            _clock.Stop();
            _packets.Writer.TryComplete();
        }
    }

    private void Encode()
    {
        IMFSinkWriter? writer = null;
        bool started = false;
        try
        {
            MFFunctions.MFStartup();
            Functions.MFCreateSinkWriterFromURL(FilePath, null, null, out writer).ThrowOnError();
            using var videoOut = MFFunctions.MFCreateMediaType();
            SetVideo(videoOut.Object, MFConstants.MFVideoFormat_H264);
            videoOut.Object.SetUINT32(MFConstants.MF_MT_AVG_BITRATE, 8_000_000).ThrowOnError();
            writer.AddStream(videoOut.Object, out var videoStream).ThrowOnError();
            using var videoIn = MFFunctions.MFCreateMediaType();
            SetVideo(videoIn.Object, MFConstants.MFVideoFormat_NV12);
            writer.SetInputMediaType(videoStream, videoIn.Object, null).ThrowOnError();

            using var audioOut = MFFunctions.MFCreateMediaType();
            SetAudio(audioOut.Object, MFConstants.MFAudioFormat_AAC);
            audioOut.Object.SetUINT32(MFConstants.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16_000).ThrowOnError();
            writer.AddStream(audioOut.Object, out var audioStream).ThrowOnError();
            using var audioIn = MFFunctions.MFCreateMediaType();
            SetAudio(audioIn.Object, MFConstants.MFAudioFormat_PCM);
            audioIn.Object.SetUINT32(MFConstants.MF_MT_AUDIO_BLOCK_ALIGNMENT, 2).ThrowOnError();
            audioIn.Object.SetUINT32(MFConstants.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 96_000).ThrowOnError();
            writer.SetInputMediaType(audioStream, audioIn.Object, null).ThrowOnError();
            writer.BeginWriting().ThrowOnError();
            started = true;
            lock (_gate)
            {
                if (!_finished) _clock.Start();
                _ready.TrySetResult();
            }
            long lastVideo = -1, lastAudio = -1;
            while (_packets.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_packets.Reader.TryRead(out var packet))
                {
                    var time = Math.Max(packet.Time, (packet.Audio ? lastAudio : lastVideo) + 1);
                    var duration = packet.Audio ? packet.Data.Length / 2 * 10_000_000L / 48_000 : 10_000_000L / _fps;
                    // PCM timestamps describe the beginning of the received audio block.
                    if (packet.Audio)
                        time = Math.Max(lastAudio + 1, time - duration);
                    using var sample = MFFunctions.MFCreateSample();
                    using var buffer = MFFunctions.MFCreateMemoryBuffer((uint)packet.Data.Length);
                    buffer.WithLock((ptr, _, _) => Marshal.Copy(packet.Data, 0, ptr, packet.Data.Length));
                    buffer.SetCurrentLength((uint)packet.Data.Length);
                    sample.AddBuffer(buffer);
                    sample.Object.SetSampleTime(time).ThrowOnError();
                    sample.Object.SetSampleDuration(duration).ThrowOnError();
                    writer.WriteSample(packet.Audio ? audioStream : videoStream, sample.Object).ThrowOnError();
                    if (packet.Audio) lastAudio = time; else lastVideo = time;
                }
            }
            writer.Finalize().ThrowOnError();
            started = false;
            if (_inputError != null) throw _inputError;
        }
        catch (Exception error)
        {
            _ready.TrySetException(error);
            if (started)
            {
                try { writer?.Finalize(); } catch { }
            }
            throw;
        }
        finally
        {
            Finish();
            if (writer != null) Marshal.ReleaseComObject(writer);
        }
    }

    private void SetVideo(IMFMediaType type, Guid subtype)
    {
        type.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
        type.SetGUID(MFConstants.MF_MT_SUBTYPE, subtype).ThrowOnError();
        type.SetSize(MFConstants.MF_MT_FRAME_SIZE, (uint)_width, (uint)_height);
        type.SetRatio(MFConstants.MF_MT_FRAME_RATE, (uint)_fps, 1);
        type.SetRatio(MFConstants.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        type.SetUINT32(MFConstants.MF_MT_INTERLACE_MODE, 2).ThrowOnError();
    }

    private static void SetAudio(IMFMediaType type, Guid subtype)
    {
        type.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Audio).ThrowOnError();
        type.SetGUID(MFConstants.MF_MT_SUBTYPE, subtype).ThrowOnError();
        type.SetUINT32(MFConstants.MF_MT_AUDIO_NUM_CHANNELS, 1).ThrowOnError();
        type.SetUINT32(MFConstants.MF_MT_AUDIO_SAMPLES_PER_SECOND, 48_000).ThrowOnError();
        type.SetUINT32(MFConstants.MF_MT_AUDIO_BITS_PER_SAMPLE, 16).ThrowOnError();
    }
}
