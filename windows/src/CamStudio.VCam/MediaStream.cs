using System;
using System.Threading;
using DirectN;

namespace CamStudio.VCam
{
    public class MediaStream : MFAttributes, IMFMediaStream2, IKsControl
    {
        public const int NUM_ALLOCATOR_SAMPLES = 10;

        private readonly object _lock = new();
        private readonly MediaSource _source;
        private IComObject<IMFMediaEventQueue>? _queue;
        private IComObject<IMFStreamDescriptor>? _descriptor;
        private IComObject<IMFVideoSampleAllocatorEx>? _allocator;
        private _MF_STREAM_STATE _state;
        private Guid _format;
        private FrameClient _frames = new();
        private uint _width = 1920;
        private uint _height = 1080;
        private byte[] _scratch = Array.Empty<byte>();
        private ulong _frameCount;

        public MediaStream(MediaSource source, uint index)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(source);
                _source = source;

                SetGUID(MFConstants.MF_DEVICESTREAM_STREAM_CATEGORY, KSMedia.PINNAME_VIDEO_CAPTURE).ThrowOnError();
                SetUINT32(MFConstants.MF_DEVICESTREAM_STREAM_ID, index).ThrowOnError();
                SetUINT32(MFConstants.MF_DEVICESTREAM_FRAMESERVER_SHARED, 1).ThrowOnError();
                SetUINT32(MFConstants.MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, (uint)_MFFrameSourceTypes.MFFrameSourceTypes_Color).ThrowOnError();

                Functions.MFCreateEventQueue(out var queue).ThrowOnError();
                _queue = new ComObject<IMFMediaEventQueue>(queue);

                // NV12 only: every Windows camera consumer accepts it and the desktop app produces it directly.
                var sizes = new (uint w, uint h)[] { (1920, 1080), (1280, 720), (640, 360) };
                var mediaTypes = new IMFMediaType[sizes.Length];
                for (var i = 0; i < sizes.Length; i++)
                {
                    var (w, h) = sizes[i];
                    Functions.MFCreateMediaType(out var nv12Type).ThrowOnError();
                    nv12Type.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
                    nv12Type.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_NV12).ThrowOnError();
                    nv12Type.SetSize(MFConstants.MF_MT_FRAME_SIZE, w, h);
                    nv12Type.SetUINT32(MFConstants.MF_MT_DEFAULT_STRIDE, w).ThrowOnError();
                    nv12Type.SetUINT32(MFConstants.MF_MT_INTERLACE_MODE, (uint)_MFVideoInterlaceMode.MFVideoInterlace_Progressive).ThrowOnError();
                    nv12Type.SetUINT32(MFConstants.MF_MT_ALL_SAMPLES_INDEPENDENT, 1).ThrowOnError();
                    nv12Type.SetRatio(MFConstants.MF_MT_FRAME_RATE, 30, 1);
                    nv12Type.SetUINT32(MFConstants.MF_MT_AVG_BITRATE, (uint)(w * h * 12 * 30)).ThrowOnError();
                    nv12Type.SetRatio(MFConstants.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
                    mediaTypes[i] = nv12Type;
                }

                Functions.MFCreateStreamDescriptor(index, mediaTypes.Length, mediaTypes, out var descriptor).ThrowOnError();
                descriptor.GetMediaTypeHandler(out var handler).ThrowOnError();
                handler.SetCurrentMediaType(mediaTypes[0]).ThrowOnError();
                _descriptor = new ComObject<IMFStreamDescriptor>(descriptor);
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT Start(IMFMediaType? type)
        {
            var queue = _queue;
            var allocator = _allocator;
            if (queue == null || allocator == null)
            {
                EventProvider.LogInfo($"MF_E_SHUTDOWN");
                return HRESULTS.MF_E_SHUTDOWN;
            }

            if (type != null)
            {
                allocator.Object.InitializeSampleAllocator(NUM_ALLOCATOR_SAMPLES, type).ThrowOnError();

                type.GetGUID(MFConstants.MF_MT_SUBTYPE, out _format).ThrowOnError();
                if (type.GetUINT64(MFConstants.MF_MT_FRAME_SIZE, out var packed).IsSuccess)
                {
                    _width = (uint)(packed >> 32);
                    _height = (uint)(packed & 0xFFFFFFFF);
                }
                EventProvider.LogInfo($"Format: {_format.GetMFName()} {_width}x{_height}");
            }

            queue.Object.QueueEventParamVar((uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEStreamStarted, Guid.Empty, HRESULTS.S_OK, null).ThrowOnError();
            _state = _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING;
            EventProvider.LogInfo("Started");
            return HRESULTS.S_OK;
        }

        public HRESULT Stop()
        {
            var queue = _queue;
            var allocator = _allocator;
            if (queue == null || allocator == null)
            {
                EventProvider.LogInfo($"MF_E_SHUTDOWN");
                return HRESULTS.MF_E_SHUTDOWN;
            }

            allocator.Object.UninitializeSampleAllocator();
            queue.Object.QueueEventParamVar((uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEStreamStopped, Guid.Empty, HRESULTS.S_OK, null).ThrowOnError();
            _state = _MF_STREAM_STATE.MF_STREAM_STATE_STOPPED;
            return HRESULTS.S_OK;
        }

        public static MFSampleAllocatorUsage GetAllocatorUsage() => MFSampleAllocatorUsage.MFSampleAllocatorUsage_UsesProvidedAllocator;
        public HRESULT SetAllocator(object allocator)
        {
            if (allocator == null)
            {
                EventProvider.LogInfo($"E_POINTER");
                return HRESULTS.E_POINTER;
            }

            if (allocator is not IMFVideoSampleAllocatorEx aex)
            {
                EventProvider.LogInfo($"E_NOINTERFACE");
                return HRESULTS.E_NOINTERFACE;
            }

            _allocator = new ComObject<IMFVideoSampleAllocatorEx>(aex);
            return HRESULTS.S_OK;
        }

        public HRESULT Set3DManager(object manager)
        {
            // Frames arrive from the desktop app as CPU NV12, so we deliberately keep the allocator on
            // system-memory buffers and ignore the DXGI manager (the Frame Server uploads as needed).
            EventProvider.LogInfo($"ignoring D3D manager {manager}");
            return HRESULTS.S_OK;
        }

        //public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
        //{
        //    EventProvider.LogInfo($"iid{iid:B}");
        //    ppv = 0;
        //    return CustomQueryInterfaceResult.NotHandled;
        //}

        public HRESULT GetEvent(uint flags, out IMFMediaEvent evt)
        {
            EventProvider.LogInfo($"flags:{flags}");
            try
            {
                lock (_lock)
                {
                    var queue = _queue;
                    if (queue == null)
                    {
                        evt = null!;
                        EventProvider.LogInfo($"MF_E_SHUTDOWN");
                        return HRESULTS.MF_E_SHUTDOWN;
                    }

                    var hr = queue.Object.GetEvent(flags, out evt);
                    EventProvider.LogInfo($" => {hr}");
                    return hr;
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT BeginGetEvent(IMFAsyncCallback callback, object state)
        {
            //EventProvider.LogInfo($"callback:{callback} state:{state}");
            try
            {
                lock (_lock)
                {
                    var queue = _queue;
                    if (queue == null)
                    {
                        EventProvider.LogInfo($"MF_E_SHUTDOWN");
                        return HRESULTS.MF_E_SHUTDOWN;
                    }

                    var hr = queue.Object.BeginGetEvent(callback, state);
                    //EventProvider.LogInfo($" => {hr}");
                    return hr;
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT EndGetEvent(IMFAsyncResult result, out IMFMediaEvent evt)
        {
            //EventProvider.LogInfo($"result:{result}");
            try
            {
                lock (_lock)
                {
                    var queue = _queue;
                    if (queue == null)
                    {
                        evt = null!;
                        EventProvider.LogInfo($"MF_E_SHUTDOWN");
                        return HRESULTS.MF_E_SHUTDOWN;
                    }

                    var hr = queue.Object.EndGetEvent(result, out evt);
                    //EventProvider.LogInfo($" evt {evt}=> {hr}");
                    return hr;
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT QueueEvent(uint type, Guid extendedType, HRESULT hrStatus, PROPVARIANT value)
        {
            EventProvider.LogInfo($"type:{type} value:{value}");
            try
            {
                lock (_lock)
                {
                    var queue = _queue;
                    if (queue == null)
                    {
                        EventProvider.LogInfo($"MF_E_SHUTDOWN");
                        return HRESULTS.MF_E_SHUTDOWN;
                    }

                    var hr = queue.Object.QueueEventParamVar(type, extendedType, hrStatus, value);
                    EventProvider.LogInfo($" => {hr}");
                    return hr;
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT GetMediaSource(out IMFMediaSource mediaSource)
        {
            EventProvider.LogInfo();
            lock (_lock)
            {
                var source = _source;
                if (source == null)
                {
                    mediaSource = null!;
                    return HRESULTS.MF_E_SHUTDOWN;
                }

                mediaSource = source;
                var hr = HRESULTS.S_OK;
                EventProvider.LogInfo($" => {hr}");
                return hr;
            }
        }

        public HRESULT GetStreamDescriptor(out IMFStreamDescriptor streamDescriptor)
        {
            EventProvider.LogInfo();
            try
            {
                lock (_lock)
                {
                    var descriptor = _descriptor;
                    if (descriptor == null)
                    {
                        streamDescriptor = null!;
                        return HRESULTS.MF_E_SHUTDOWN;
                    }

                    streamDescriptor = descriptor.Object;
                    var hr = HRESULTS.S_OK;
                    EventProvider.LogInfo($" => {hr}");
                    return hr;
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT RequestSample(object token)
        {
            try
            {
                lock (_lock)
                {
                    var queue = _queue;
                    var allocator = _allocator;
                    if (allocator == null || queue == null)
                        return HRESULTS.MF_E_SHUTDOWN;

                    allocator.Object.AllocateSample(out var sample).ThrowOnError();

                    using (var outputSample = new ComObject<IMFSample>(sample))
                    {
                        sample.SetSampleTime(Functions.MFGetSystemTime()).ThrowOnError();
                        sample.SetSampleDuration(333333).ThrowOnError();

                        var size = (int)(_width * _height * 3 / 2);
                        if (_scratch.Length != size)
                            _scratch = new byte[size];

                        if (!_frames.TryFill(_scratch, (int)_width, (int)_height))
                        {
                            // desktop app not reachable: send black
                            _scratch.AsSpan(0, (int)(_width * _height)).Fill(16);
                            _scratch.AsSpan((int)(_width * _height)).Fill(128);
                        }

                        using var buffer = outputSample.GetBufferByIndex(0);
                        var scratch = _scratch;
                        buffer.WithLock((scanline, length, _) =>
                        {
                            var n = Math.Min((int)length, scratch.Length);
                            System.Runtime.InteropServices.Marshal.Copy(scratch, 0, scanline, n);
                        });
                        buffer.SetCurrentLength((uint)size);
                        _frameCount++;

                        if (token != null)
                        {
                            outputSample.Object.SetUnknown(MFConstants.MFSampleExtension_Token, token).ThrowOnError();
                        }

                        queue.Object.QueueEventParamUnk((uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEMediaSample, Guid.Empty, HRESULTS.S_OK, outputSample.Object).ThrowOnError();
                    }

                    // keep the allocator from filling up with samples awaiting finalization
                    if (_frameCount % (NUM_ALLOCATOR_SAMPLES / 2) == 0)
                    {
                        GC.Collect();
                    }
                    return HRESULTS.S_OK;
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT SetStreamState(_MF_STREAM_STATE value)
        {
            EventProvider.LogInfo($"value:{value}");
            try
            {
                if (_state != value)
                {
                    switch (value)
                    {
                        case _MF_STREAM_STATE.MF_STREAM_STATE_STOPPED:
                            return Stop();

                        case _MF_STREAM_STATE.MF_STREAM_STATE_PAUSED:
                            if (_state != _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING)
                            {
                                EventProvider.LogInfo($"MF_E_INVALID_STATE_TRANSITION");
                                return HRESULTS.MF_E_INVALID_STATE_TRANSITION;
                            }

                            _state = value;
                            break;

                        case _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING:
                            return Start(null);

                        default:
                            EventProvider.LogInfo($"MF_E_INVALID_STATE_TRANSITION");
                            return HRESULTS.MF_E_INVALID_STATE_TRANSITION;
                    }
                }
                return HRESULTS.S_OK;
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT GetStreamState(out _MF_STREAM_STATE value)
        {
            value = _state;
            EventProvider.LogInfo($"value:{value}");
            return HRESULTS.S_OK;
        }

        public HRESULT KsProperty(ref KSIDENTIFIER roperty, uint propertyLength, nint propertyData, uint dataLength, out uint bytesReturned)
        {
            EventProvider.LogInfo($"Property:{roperty.GetMFName()} PropertyLength:{propertyLength} DataLength:{dataLength}");
            bytesReturned = 0;
            return HRESULT.FromWin32(Constants.ERROR_SET_NOT_FOUND);
        }

        public HRESULT KsMethod(ref KSIDENTIFIER method, uint methodLength, nint methodData, uint dataLength, out uint bytesReturned)
        {
            EventProvider.LogInfo($"Method:{method.GetMFName()} PropertyLength:{methodLength} DataLength:{dataLength}");
            bytesReturned = 0;
            return HRESULT.FromWin32(Constants.ERROR_SET_NOT_FOUND);
        }

        public HRESULT KsEvent(ref KSIDENTIFIER evt, uint eventLength, nint eventData, uint dataLength, out uint bytesReturned)
        {
            EventProvider.LogInfo($"Event:{evt.GetMFName()} PropertyLength:{eventLength} DataLength:{dataLength}");
            bytesReturned = 0;
            return HRESULT.FromWin32(Constants.ERROR_SET_NOT_FOUND);
        }

        protected override void Dispose(bool disposing)
        {
            EventProvider.LogInfo();
            Interlocked.Exchange(ref _descriptor!, null)?.Dispose();
            Interlocked.Exchange(ref _queue!, null)?.Dispose();
            Interlocked.Exchange(ref _allocator!, null)?.Dispose();
            Interlocked.Exchange(ref _frames!, null)?.Dispose();
            base.Dispose(disposing);
        }
    }
}
