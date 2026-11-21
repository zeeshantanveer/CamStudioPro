namespace CamStudio.VCam
{
    public static class Shared
    {
        // COM class id of the virtual camera media source. The desktop app passes it to MFCreateVirtualCamera.
        public const string CLSID_VCam = "3c1f6a52-8e0b-4b7d-9f2a-6d4e8c1b7a35";

        // Loopback port where the desktop app serves decoded NV12 frames to this media source.
        public const int FramePort = 47802;
    }
}
