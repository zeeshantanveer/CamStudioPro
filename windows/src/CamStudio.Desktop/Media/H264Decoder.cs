using System.Runtime.InteropServices;
using DirectN;

namespace CamStudio.Desktop.Media;

/// <summary>A decoded picture: tightly packed NV12 (stride == Width).</summary>
public sealed class Nv12Frame
{
    public Nv12Frame(int width, int height)
    {
        Width = width;
        Height = height;
        Data = new byte[width * height * 3 / 2];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
    public long PtsUs { get; set; }
    public long ReceivedTicks { get; set; }
}

/// <summary>
/// H.264 Annex-B → NV12 using the Media Foundation H.264 decoder MFT (software path, low-latency mode).
/// Not thread safe: call from one thread.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private IMFTransform? _mft;
    private bool _outputTypeSet;
    private int _codedWidth;
    private int _codedHeight;
    private int _stride;
    private uint _outputBufferSize;
    private bool _streaming;

    public int DisplayWidth { get; private set; }
    public int DisplayHeight { get; private set; }

    public void Configure(int width, int height)
    {
        Reset();
        DisplayWidth = width;
        DisplayHeight = height;

        var type = Type.GetTypeFromCLSID(MFConstants.CLSID_MSH264DecoderMFT, true)!;
        _mft = (IMFTransform)System.Activator.CreateInstance(type)!;

        if (_mft.GetAttributes(out var attributes).IsSuccess && attributes != null)
        {
            attributes.SetUINT32(MFConstants.MF_LOW_LATENCY, 1);
            Marshal.ReleaseComObject(attributes);
        }

        Functions.MFCreateMediaType(out var input).ThrowOnError();
        input.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
        input.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_H264).ThrowOnError();
        input.SetSize(MFConstants.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
        input.SetUINT32(MFConstants.MF_MT_INTERLACE_MODE, (uint)_MFVideoInterlaceMode.MFVideoInterlace_Progressive).ThrowOnError();
        _mft.SetInputType(0, input, 0).ThrowOnError();
        Marshal.ReleaseComObject(input);

        SetNv12Output();
        _mft.ProcessMessage(_MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        _mft.ProcessMessage(_MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        _streaming = true;
    }

    private void SetNv12Output()
    {
        var mft = _mft!;
        for (uint i = 0; ; i++)
        {
            var hr = mft.GetOutputAvailableType(0, i, out var candidate);
            if (hr.IsError || candidate == null)
                throw new InvalidOperationException("H.264 decoder offers no NV12 output: " + hr);

            candidate.GetGUID(MFConstants.MF_MT_SUBTYPE, out var subtype).ThrowOnError();
            if (subtype == MFConstants.MFVideoFormat_NV12)
            {
                mft.SetOutputType(0, candidate, 0).ThrowOnError();
                if (candidate.GetUINT64(MFConstants.MF_MT_FRAME_SIZE, out var packed).IsSuccess)
                {
                    _codedWidth = (int)(packed >> 32);
                    _codedHeight = (int)(packed & 0xFFFFFFFF);
                }
                _stride = candidate.GetUINT32(MFConstants.MF_MT_DEFAULT_STRIDE, out var stride).IsSuccess && (int)stride > 0
                    ? (int)stride
                    : _codedWidth;
                Marshal.ReleaseComObject(candidate);
                break;
            }
            Marshal.ReleaseComObject(candidate);
        }

        mft.GetOutputStreamInfo(0, out var info).ThrowOnError();
        _outputBufferSize = Math.Max(info.cbSize, (uint)(_stride * _codedHeight * 3 / 2));
        _outputTypeSet = true;
    }

    /// <summary>Feeds one access unit; returns any pictures the decoder produced.</summary>
    public List<Nv12Frame> Decode(ReadOnlySpan<byte> annexB, long ptsUs)
    {
        var output = new List<Nv12Frame>(1);
        var mft = _mft ?? throw new InvalidOperationException("decoder not configured");

        using (var sample = MFFunctions.MFCreateSample())
        using (var buffer = MFFunctions.MFCreateMemoryBuffer((uint)annexB.Length))
        {
            var data = annexB.ToArray();
            buffer.WithLock((ptr, max, _) => Marshal.Copy(data, 0, ptr, data.Length));
            buffer.SetCurrentLength((uint)data.Length);
            sample.AddBuffer(buffer);
            sample.Object.SetSampleTime(ptsUs * 10).ThrowOnError();

            var hr = mft.ProcessInput(0, sample.Object, 0);
            if (hr == HRESULTS.MF_E_NOTACCEPTING)
            {
                Drain(output);
                hr = mft.ProcessInput(0, sample.Object, 0);
            }
            hr.ThrowOnError();
        }

        Drain(output);
        return output;
    }

    private void Drain(List<Nv12Frame> output)
    {
        var mft = _mft!;
        while (true)
        {
            if (!_outputTypeSet)
                SetNv12Output();

            using var sample = MFFunctions.MFCreateSample();
            using var buffer = MFFunctions.MFCreateMemoryBuffer(_outputBufferSize);
            sample.AddBuffer(buffer);

            HRESULT hr = HRESULTS.S_OK;
            sample.WithComPointer(ptr =>
            {
                var buffers = new _MFT_OUTPUT_DATA_BUFFER[1];
                buffers[0].pSample = ptr;
                hr = mft.ProcessOutput(0, 1, buffers, out _);
                if (buffers[0].pEvents != 0)
                    Marshal.Release(buffers[0].pEvents);
            });

            if (hr == HRESULTS.MF_E_TRANSFORM_NEED_MORE_INPUT)
                return;

            if (hr == HRESULTS.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                _outputTypeSet = false;
                continue;
            }

            hr.ThrowOnError();
            output.Add(CopyOut(buffer));
        }
    }

    private Nv12Frame CopyOut(IComObject<IMFMediaBuffer> buffer)
    {
        // crop the coded picture (e.g. 1920x1088) to the display size the phone announced
        var width = Math.Min(DisplayWidth > 0 ? DisplayWidth : _codedWidth, _codedWidth) & ~1;
        var height = Math.Min(DisplayHeight > 0 ? DisplayHeight : _codedHeight, _codedHeight) & ~1;
        var frame = new Nv12Frame(width, height);
        var stride = _stride;
        var codedHeight = _codedHeight;

        buffer.WithLock((ptr, max, current) =>
        {
            var needed = (long)stride * codedHeight * 3 / 2;
            if (current < needed)
                return;

            var dst = frame.Data;
            for (var y = 0; y < height; y++)
                Marshal.Copy(ptr + y * stride, dst, y * width, width);

            var uvSrc = ptr + stride * codedHeight;
            var uvDst = width * height;
            for (var y = 0; y < height / 2; y++)
                Marshal.Copy(uvSrc + y * stride, dst, uvDst + y * width, width);
        });
        return frame;
    }

    public void Reset()
    {
        if (_mft != null)
        {
            if (_streaming)
            {
                _mft.ProcessMessage(_MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
                _mft.ProcessMessage(_MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, 0);
            }
            Marshal.ReleaseComObject(_mft);
        }
        _mft = null;
        _streaming = false;
        _outputTypeSet = false;
    }

    public void Dispose() => Reset();
}
