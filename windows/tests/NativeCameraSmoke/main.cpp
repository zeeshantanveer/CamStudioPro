#include <windows.h>
#include <appmodel.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfvirtualcamera.h>
#include <mfreadwrite.h>
#include <wrl/client.h>
#include <propvarutil.h>
#include <vector>
#include <cstdio>
using Microsoft::WRL::ComPtr;
void Check(HRESULT hr) { if(FAILED(hr)) { printf("FAIL HRESULT 0x%08lx\n",hr); throw hr; } }
ComPtr<IMFMediaEvent> Event(IMFMediaEventGenerator* queue, MediaEventType wanted) {
    auto deadline=GetTickCount64()+5000;
    while(GetTickCount64()<deadline) {
        ComPtr<IMFMediaEvent> event;
        auto hr=queue->GetEvent(MF_EVENT_FLAG_NO_WAIT,&event);
        if(hr==MF_E_NO_EVENTS_AVAILABLE) { Sleep(1); continue; }
        Check(hr); HRESULT status; Check(event->GetStatus(&status)); Check(status);
        MediaEventType kind; Check(event->GetType(&kind));
        if(kind==wanted) return event;
    }
    Check(HRESULT_FROM_WIN32(WAIT_TIMEOUT)); return {};
}
int wmain(int argc,wchar_t** argv) {
 try {
    if(argc<2) return 2;
    Check(CoInitializeEx(nullptr,COINIT_MULTITHREADED)); Check(MFStartup(MF_VERSION));
    if (wcscmp(argv[1], L"--registered")==0) {
        // Full Windows registration path: no direct DLL loading or manual class factory.
        if (argc>2) { FILE* log; _wfreopen_s(&log,argv[2],L"w",stdout); }
        UINT32 packageLength=0; auto packageStatus=GetCurrentPackageFullName(&packageLength,nullptr);
        printf("Package identity status: %ld (122 means packaged)\n",packageStatus); fflush(stdout);
        const GUID registeredClsid={0x3c1f6a52,0x8e0b,0x4b7d,{0x9f,0x2a,0x6d,0x4e,0x8c,0x1b,0x7a,0x35}};
        ComPtr<IMFActivate> registeredActivate;
        auto classHr=CoCreateInstance(registeredClsid,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&registeredActivate));
        printf("Registered source CoCreateInstance: 0x%08lx\n",classHr); fflush(stdout);
        registeredActivate.Reset();
        ComPtr<IMFVirtualCamera> camera;
        auto hr=MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,MFVirtualCameraLifetime_Session,
            MFVirtualCameraAccess_CurrentUser,L"CamStudio Pro",L"{3c1f6a52-8e0b-4b7d-9f2a-6d4e8c1b7a35}",nullptr,0,&camera);
        printf("MFCreateVirtualCamera: 0x%08lx\n",hr); fflush(stdout); Check(hr);
        hr=camera->Start(nullptr); printf("IMFVirtualCamera.Start: 0x%08lx\n",hr); fflush(stdout);
        if (SUCCEEDED(hr)) {
            ComPtr<IMFMediaSource> source; hr=camera->GetMediaSource(&source);
            printf("Frame Server GetMediaSource: 0x%08lx\n",hr); fflush(stdout);
        }
        if (SUCCEEDED(hr) && argc>3) {
            wchar_t* link=nullptr; UINT32 length=0;
            Check(camera->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,&link,&length));
            ComPtr<IMFAttributes> attributes; Check(MFCreateAttributes(&attributes,2));
            Check(attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID));
            hr=attributes->SetString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,link); CoTaskMemFree(link); Check(hr);
            ComPtr<IMFMediaSource> device; Check(MFCreateDeviceSource(attributes.Get(),&device));
            ComPtr<IMFSourceReader> reader; Check(MFCreateSourceReaderFromMediaSource(device.Get(),nullptr,&reader));
            ComPtr<IMFMediaType> type; Check(MFCreateMediaType(&type));
            Check(type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video)); Check(type->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_NV12));
            Check(MFSetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,640,360)); Check(MFSetAttributeRatio(type.Get(),MF_MT_FRAME_RATE,30,1));
            Check(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM,nullptr,type.Get()));
            int capturedFrames=0;
            for(int attempts=0;capturedFrames<3 && attempts<20;++attempts) {
                DWORD flags=0; LONGLONG timestamp=0; ComPtr<IMFSample> sample;
                Check(reader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM,0,nullptr,&flags,&timestamp,&sample));
                printf("ReadSample flags: 0x%08lx\n",flags); fflush(stdout);
                if(!sample) { if(flags & (MF_SOURCE_READERF_ENDOFSTREAM|MF_SOURCE_READERF_ERROR)) return 9; continue; }
                ComPtr<IMFMediaBuffer> buffer; Check(sample->ConvertToContiguousBuffer(&buffer));
                BYTE* pixels=nullptr; DWORD size=0; Check(buffer->Lock(&pixels,nullptr,&size));
                bool good=size==640*360*3/2;
                if(good) for(DWORD i=0;i<size;++i) if(pixels[i]!=(i<640*360 ? 90:135)) { good=false; break; }
                Check(buffer->Unlock());
                if(!good) { printf("FAIL Frame Server pixel contents, length %lu\n",size); return 10; }
                printf("PASS Windows Frame Server capture 640x360 sample %d, timestamp %lld\n",capturedFrames+1,timestamp); fflush(stdout); ++capturedFrames;
            }
            if(capturedFrames!=3) { printf("FAIL not enough camera samples\n"); return 11; }
            reader.Reset(); device->Shutdown();
        }
        camera->Shutdown(); Check(hr); printf("PASS registered camera API activation\n"); return 0;
    }
    auto module=LoadLibraryW(argv[1]); if(!module) Check(HRESULT_FROM_WIN32(GetLastError()));
    auto factoryFunction=reinterpret_cast<HRESULT(STDAPICALLTYPE*)(REFCLSID,REFIID,void**)>(GetProcAddress(module,"DllGetClassObject"));
    if(!factoryFunction) return 3;
    const GUID clsid={0x3c1f6a52,0x8e0b,0x4b7d,{0x9f,0x2a,0x6d,0x4e,0x8c,0x1b,0x7a,0x35}};
    const bool black=argc>2;
    for(DWORD format=0;format<3;++format) {
        ComPtr<IClassFactory> factory; Check(factoryFunction(clsid,IID_PPV_ARGS(&factory)));
        ComPtr<IMFActivate> activate; Check(factory->CreateInstance(nullptr,IID_PPV_ARGS(&activate)));
        ComPtr<IMFMediaSource> source; Check(activate->ActivateObject(IID_PPV_ARGS(&source)));
        ComPtr<IMFSampleAllocatorControl> control; Check(source.As(&control));
        ComPtr<IMFVideoSampleAllocatorEx> allocator; Check(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&allocator)));
        Check(control->SetDefaultAllocator(0,allocator.Get()));
        ComPtr<IMFPresentationDescriptor> pd; Check(source->CreatePresentationDescriptor(&pd));
        Check(pd->SelectStream(0)); BOOL selected; ComPtr<IMFStreamDescriptor> sd;
        Check(pd->GetStreamDescriptorByIndex(0,&selected,&sd));
        ComPtr<IMFMediaTypeHandler> handler; Check(sd->GetMediaTypeHandler(&handler));
        ComPtr<IMFMediaType> type; Check(handler->GetMediaTypeByIndex(format,&type));
        Check(handler->SetCurrentMediaType(type.Get())); UINT32 w,h; Check(MFGetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,&w,&h));
        PROPVARIANT position{}; Check(source->Start(pd.Get(),nullptr,&position));
        auto event=Event(source.Get(),MENewStream); PROPVARIANT value{}; Check(event->GetValue(&value));
        ComPtr<IMFMediaStream> stream; Check(value.punkVal->QueryInterface(IID_PPV_ARGS(&stream))); PropVariantClear(&value);
        Event(source.Get(),MESourceStarted); Event(stream.Get(),MEStreamStarted);
        for(int i=0;i<6;++i) {
            Check(stream->RequestSample(nullptr)); auto sampleEvent=Event(stream.Get(),MEMediaSample);
            PROPVARIANT sampleValue{}; Check(sampleEvent->GetValue(&sampleValue));
            ComPtr<IMFSample> sample; Check(sampleValue.punkVal->QueryInterface(IID_PPV_ARGS(&sample))); PropVariantClear(&sampleValue);
            LONGLONG duration; Check(sample->GetSampleDuration(&duration)); if(duration!=333333) return 4;
            ComPtr<IMFMediaBuffer> buffer; Check(sample->GetBufferByIndex(0,&buffer));
            std::vector<BYTE> data(static_cast<size_t>(w)*h*3/2);
            ComPtr<IMF2DBuffer> plane;
            if(SUCCEEDED(buffer.As(&plane))) Check(plane->ContiguousCopyTo(data.data(),static_cast<DWORD>(data.size())));
            else { BYTE* ptr; DWORD size; Check(buffer->Lock(&ptr,nullptr,&size)); if(size!=data.size()) return 5; memcpy(data.data(),ptr,data.size()); Check(buffer->Unlock()); }
            for(size_t p=0;p<data.size();++p) if(data[p]!=(p<static_cast<size_t>(w)*h ? (black?16:90) : (black?128:135))) {
                printf("FAIL pixels at %zu: %d (%ux%u)\n",p,data[p],w,h); return 6;
            }
        }
        Check(source->Stop()); Check(source->Shutdown()); Check(activate->ShutdownObject());
        printf("PASS native COM activation, NV12 %ux%u, 6 samples, %s, stop/shutdown\n",w,h,black?"black fallback":"live phone protocol");
    }
    auto canUnload = reinterpret_cast<HRESULT(STDAPICALLTYPE*)()>(GetProcAddress(module,"DllCanUnloadNow"));
    if (!canUnload || canUnload()!=S_OK) { printf("FAIL leaked COM objects\n"); return 8; }
    if (GetModuleHandleW(L"coreclr.dll")) { printf("FAIL native source loaded CLR\n"); return 7; }
    printf("PASS no leaked COM objects and no CLR loaded\n");
    FreeLibrary(module); MFShutdown(); CoUninitialize(); return 0;
 } catch(...) { return 1; }
}
