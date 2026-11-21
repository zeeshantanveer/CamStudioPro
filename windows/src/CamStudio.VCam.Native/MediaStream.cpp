#include "pch.h"
#include "Undocumented.h"
#include "Tools.h"
#include "EnumNames.h"
#include "MFTools.h"
#include "FrameClient.h"
#include "MediaStream.h"
#include "MediaSource.h"

HRESULT MediaStream::Initialize(IMFMediaSource* source, int index)
{
	RETURN_HR_IF_NULL(E_POINTER, source);
	_source = source;
	_index = index;

	RETURN_IF_FAILED(SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_STREAM_ID, index));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes::MFFrameSourceTypes_Color));

	RETURN_IF_FAILED(MFCreateEventQueue(&_queue));

    auto types = wil::make_unique_cotaskmem_array<wil::com_ptr_nothrow<IMFMediaType>>(3);
    const UINT32 sizes[][2] = {{1920,1080},{1280,720},{640,360}};
    for (size_t i=0; i<3; ++i) {
        wil::com_ptr_nothrow<IMFMediaType> t;
        RETURN_IF_FAILED(MFCreateMediaType(&t));
        RETURN_IF_FAILED(t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        RETURN_IF_FAILED(t->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
        RETURN_IF_FAILED(MFSetAttributeSize(t.get(), MF_MT_FRAME_SIZE, sizes[i][0], sizes[i][1]));
        RETURN_IF_FAILED(t->SetUINT32(MF_MT_DEFAULT_STRIDE, sizes[i][0]));
        RETURN_IF_FAILED(t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        RETURN_IF_FAILED(t->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
        RETURN_IF_FAILED(MFSetAttributeRatio(t.get(), MF_MT_FRAME_RATE, 30, 1));
        RETURN_IF_FAILED(MFSetAttributeRatio(t.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
        types[i] = t.detach();
    }

	RETURN_IF_FAILED_MSG(MFCreateStreamDescriptor(_index, (DWORD)types.size(), types.get(), &_descriptor), "MFCreateStreamDescriptor failed");

	wil::com_ptr_nothrow<IMFMediaTypeHandler> handler;
	RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
	TraceMFAttributes(handler.get(), L"MediaTypeHandler");
	RETURN_IF_FAILED(handler->SetCurrentMediaType(types[0]));

	return S_OK;
}

HRESULT MediaStream::Start(IMFMediaType* type)
{
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_allocator);

    wil::com_ptr_nothrow<IMFMediaTypeHandler> handler;
    wil::com_ptr_nothrow<IMFMediaType> selected;
    RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
    if (type) { RETURN_IF_FAILED(handler->SetCurrentMediaType(type)); }
    RETURN_IF_FAILED(handler->GetCurrentMediaType(&selected));
    RETURN_IF_FAILED(selected->GetGUID(MF_MT_SUBTYPE, &_format));
    RETURN_HR_IF(MF_E_INVALIDMEDIATYPE, _format != MFVideoFormat_NV12);
    RETURN_IF_FAILED(MFGetAttributeSize(selected.get(), MF_MT_FRAME_SIZE, &_width, &_height));
    RETURN_IF_FAILED(_allocator->InitializeSampleAllocator(10, selected.get()));

	RETURN_IF_FAILED(_queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr));
	_state = MF_STREAM_STATE_RUNNING;
	return S_OK;
}

HRESULT MediaStream::Stop()
{
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_allocator);

	RETURN_IF_FAILED(_allocator->UninitializeSampleAllocator());
	RETURN_IF_FAILED(_queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr));
	_state = MF_STREAM_STATE_STOPPED;
	return S_OK;
}

MFSampleAllocatorUsage MediaStream::GetAllocatorUsage()
{
	return MFSampleAllocatorUsage_UsesProvidedAllocator;
}

HRESULT MediaStream::SetAllocator(IUnknown* allocator)
{
	RETURN_HR_IF_NULL(E_POINTER, allocator);
	_allocator.reset();
	RETURN_HR(allocator->QueryInterface(&_allocator));
}

HRESULT MediaStream::SetD3DManager(IUnknown*)
{
    // Serve CPU NV12 buffers. Frame Server can upload/convert for its consumer.
    return S_OK;
}

void MediaStream::Shutdown()
{
    winrt::slim_lock_guard lock(_lock);
	if (_queue)
	{
		LOG_IF_FAILED_MSG(_queue->Shutdown(), "Queue shutdown failed");
		_queue.reset();
	}

	_frames.Reset();
	_allocator.reset();
	_descriptor.reset();
	_source.reset();
	_attributes.reset();
}

// IMFMediaEventGenerator
STDMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState)
{
	//WINTRACE(L"MediaSource::BeginGetEvent");
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->BeginGetEvent(pCallback, punkState));
	return S_OK;
}

STDMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent)
{
	//WINTRACE(L"MediaStream::EndGetEvent");
	RETURN_HR_IF_NULL(E_POINTER, ppEvent);
	*ppEvent = nullptr;
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->EndGetEvent(pResult, ppEvent));
	return S_OK;
}

STDMETHODIMP MediaStream::GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent)
{
	WINTRACE(L"MediaStream::GetEvent");
	RETURN_HR_IF_NULL(E_POINTER, ppEvent);
	*ppEvent = nullptr;
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->GetEvent(dwFlags, ppEvent));
	return S_OK;
}

STDMETHODIMP MediaStream::QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue)
{
	WINTRACE(L"MediaStream::QueueEvent");
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue));
	return S_OK;
}

// IMFMediaStream
STDMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** ppMediaSource)
{
	WINTRACE(L"MediaSource::GetMediaSource");
	RETURN_HR_IF_NULL(E_POINTER, ppMediaSource);
	*ppMediaSource = nullptr;
	RETURN_HR_IF(MF_E_SHUTDOWN, !_source);

	RETURN_IF_FAILED(_source.copy_to(ppMediaSource));
	return S_OK;
}

STDMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor)
{
	WINTRACE(L"MediaStream::GetStreamDescriptor");
	RETURN_HR_IF_NULL(E_POINTER, ppStreamDescriptor);
	*ppStreamDescriptor = nullptr;
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_descriptor);

	RETURN_IF_FAILED(_descriptor.copy_to(ppStreamDescriptor));
	return S_OK;
}

STDMETHODIMP MediaStream::RequestSample(IUnknown* pToken)
{
	//WINTRACE(L"MediaStream::RequestSample pToken:%p", pToken);
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_allocator || !_queue);
    RETURN_HR_IF(MF_E_INVALIDREQUEST, _state != MF_STREAM_STATE_RUNNING);

	wil::com_ptr_nothrow<IMFSample> sample;
	RETURN_IF_FAILED(_allocator->AllocateSample(&sample));
	RETURN_IF_FAILED(sample->SetSampleTime(MFGetSystemTime()));
	RETURN_IF_FAILED(sample->SetSampleDuration(333333));

    _pixels.resize(static_cast<size_t>(_width) * _height * 3 / 2);
    if (!_frames.Fill(_pixels.data(), _pixels.size(), _width, _height)) {
        memset(_pixels.data(), 16, static_cast<size_t>(_width)*_height);
        memset(_pixels.data()+static_cast<size_t>(_width)*_height, 128, _pixels.size()-static_cast<size_t>(_width)*_height);
    }
    wil::com_ptr_nothrow<IMFMediaBuffer> buffer;
    RETURN_IF_FAILED(sample->GetBufferByIndex(0, &buffer));
    wil::com_ptr_nothrow<IMF2DBuffer> plane;
    if (SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(&plane)))) {
        RETURN_IF_FAILED(plane->ContiguousCopyFrom(_pixels.data(), static_cast<DWORD>(_pixels.size())));
    } else {
        BYTE* data = nullptr; DWORD capacity = 0;
        RETURN_IF_FAILED(buffer->Lock(&data, &capacity, nullptr));
        auto unlock = wil::scope_exit([&] { buffer->Unlock(); });
        RETURN_HR_IF(MF_E_BUFFERTOOSMALL, capacity < _pixels.size());
        memcpy(data, _pixels.data(), _pixels.size());
    }
    RETURN_IF_FAILED(buffer->SetCurrentLength(static_cast<DWORD>(_pixels.size())));
    auto& outSample = sample;

	if (pToken)
	{
		RETURN_IF_FAILED(outSample->SetUnknown(MFSampleExtension_Token, pToken));
	}
	RETURN_IF_FAILED(_queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, outSample.get()));
	return S_OK;
}

// IMFMediaStream2
STDMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE value)
{
	WINTRACE(L"MediaStream::SetStreamState current:%u value:%u", _state, value);
	if (_state == value)
		return S_OK;
	switch (value)
	{
	case MF_STREAM_STATE_PAUSED:
		if (_state != MF_STREAM_STATE_RUNNING)
			RETURN_HR(MF_E_INVALID_STATE_TRANSITION);

		_state = value;
		break;

	case MF_STREAM_STATE_RUNNING:
		RETURN_IF_FAILED(Start(nullptr));
		break;

	case MF_STREAM_STATE_STOPPED:
		RETURN_IF_FAILED(Stop());
		break;

	default:
		RETURN_HR(MF_E_INVALID_STATE_TRANSITION);
		break;
	}
	return S_OK;
}

STDMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* value)
{
	WINTRACE(L"MediaStream::GetStreamState state:%u", _state);
	RETURN_HR_IF_NULL(E_POINTER, value);
	*value = _state;
	return S_OK;
}

// IKsControl
STDMETHODIMP_(NTSTATUS) MediaStream::KsProperty(PKSPROPERTY property, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	WINTRACE(L"MediaStream::KsProperty len:%u data:%p dataLength:%u", length, data, dataLength);
	RETURN_HR_IF_NULL(E_POINTER, property);
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	winrt::slim_lock_guard lock(_lock);

	WINTRACE(L"MediaStream::KsProperty prop:%s", PKSIDENTIFIER_ToString(property, length).c_str());

	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaStream::KsMethod(PKSMETHOD method, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	WINTRACE(L"MediaStream::KsMethod len:%u data:%p dataLength:%u", length, data, dataLength);
	RETURN_HR_IF_NULL(E_POINTER, method);
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	winrt::slim_lock_guard lock(_lock);

	WINTRACE(L"MediaStream::KsMethod method:%s", PKSIDENTIFIER_ToString(method, length).c_str());

	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaStream::KsEvent(PKSEVENT evt, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	WINTRACE(L"MediaStream::KsEvent evt:%p len:%u data:%p dataLength:%u", evt, length, data, dataLength);
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	winrt::slim_lock_guard lock(_lock);

	WINTRACE(L"MediaStream::KsEvent event:%s", PKSIDENTIFIER_ToString(evt, length).c_str());
	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}
