# Native camera source

Adapted from Simon Mourier's MIT-licensed VCamSample, commit
58b6d4b0e0c2c97fdffd66d23e7c8da6b89f570d:
https://github.com/smourier/VCamSample
License: ../../THIRD-PARTY-VCamSample-LICENSE.txt.
Uses Microsoft WIL 1.0.260126.7 headers (THIRD-PARTY-WIL-LICENSE.txt) and Windows SDK C++/WinRT.

CamStudio changes: replace drawing and color conversion with loopback NV12 input;
advertise 1920x1080, 1280x720, 640x360 at 30 fps; honor negotiated dimensions;
CPU allocator buffers with pitch-aware contiguous copies; fix stream state comparison;
defer source activation until Frame Server attributes are available; release source,
allocator and socket on shutdown. Same CLSID and protocol as the former C# source.

Build from repository root: ./windows/build-native-camera.ps1
The desktop project invokes this automatically for regular builds and publishing.
The C++ runtime is statically linked; only Windows libraries are imported.
The old CamStudio.VCam C# project is retained as a reference and is no longer deployed.

Native tests: build windows/tests/NativeCameraSmoke/NativeCameraSmoke.vcxproj with VS MSBuild,
then run the desktop protocol smoke with the native test executable and camera DLL as its
two arguments. See ../../packaging/STORE.md for package qualification requirements.
