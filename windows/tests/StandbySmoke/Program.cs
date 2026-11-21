using System.Text.Json;
using System.IO;
using CamStudio.Desktop.Core;
using CamStudio.Desktop.Media;

static void Check(bool value,string message) { if (!value) throw new Exception(message); }
var settings=JsonSerializer.Deserialize<AppSettings>("{\"BubbleSize\":\"Large\"}")!;
Check(!settings.BubbleInPreview && !settings.BubbleInRecordings && !settings.BubbleInStreams,"Upgrade must default every bubble off");
var screen=new Nv12Frame(320,240); screen.Data.AsSpan(0,320*240).Fill(235); screen.Data.AsSpan(320*240).Fill(128);
var camera=new Nv12Frame(320,240); camera.Data.AsSpan(0,320*240).Fill(70); camera.Data.AsSpan(320*240).Fill(128);
var original=screen.Data.ToArray();
for(int flags=0;flags<8;flags++) {
    var p=(flags&1)!=0; var r=(flags&2)!=0; var s=(flags&4)!=0;
    var outputs=SceneCompositor.BuildOutputs(screen,camera,true,SceneCompositor.BubbleSize.Medium,p,r,s);
    foreach(var (frame,enabled) in new[]{(outputs.Preview,p),(outputs.Recording,r),(outputs.Stream,s)})
        Check(enabled ? !frame.Data.SequenceEqual(original) : frame.Data.SequenceEqual(original),"Bubble leaked into wrong output");
    Check(screen.Data.SequenceEqual(original),"Compositing mutated shared screen-only pixels");
    var closed=SceneCompositor.BuildOutputs(screen,null,true,SceneCompositor.BubbleSize.Medium,p,r,s);
    Check(closed.Preview.Data.SequenceEqual(original) && closed.Recording.Data.SequenceEqual(original) && closed.Stream.Data.SequenceEqual(original),"Closed camera left a stale bubble");
}
using(var scene=new SceneCompositor()) {
    var frames=0;scene.Output=_=>frames++;
    scene.CameraFrame(camera); Check(frames==0,"Camera starts off");
    scene.CameraEnabled=true;scene.CameraFrame(camera);Check(frames==1,"Open camera passes video");
    scene.ClearCamera();scene.CameraFrame(camera);Check(frames==1,"Late frames after close must not revive video");
}
foreach(var enabled in new[]{false,true}) {
    using var wire=new MemoryStream(Protocol.FrameJson(Protocol.Control,new {cmd="camera",enabled,cameraControlVersion=1,requestId="test-request"}));
    var msg=await Protocol.ReadAsync(wire,new byte[5],new byte[8],CancellationToken.None);
    Check(msg.Json()["enabled"]!.GetValue<bool>()==enabled && msg.Json()["requestId"]!.GetValue<string>()=="test-request","Camera on/off contract was lost");
}
Console.WriteLine("PASS: default-off upgrades, independent bubble output pixels, source immutability, closed-camera late frames and camera control framing.");

var studioType = typeof(SceneCompositor).Assembly.GetType("CamStudio.Desktop.Media.StudioMicrophone", true)!;
Exception? studioFailure = null;
var studioThread = new Thread(() =>
{
    try
    {
        Check(!settings.ShowVirtualMic && settings.VirtualMicFollowsAudio, "Virtual mic defaults changed");
        var applyOutput = studioType.GetMethod("ApplyOutput")!;
        var applyPreview = studioType.GetMethod("ApplyPreview")!;
        var styleType = studioType.Assembly.GetType("CamStudio.Desktop.Media.StudioMicStyle", true)!;
        var labelType = studioType.Assembly.GetType("CamStudio.Desktop.Media.StudioLabelStyle", true)!;
        var labelStyle = Activator.CreateInstance(labelType, "Arial", 2, true, 0xFFFFFF, 0x400000, 0x008000)!;
        var hd = new Nv12Frame(1920, 1080);
        var dimScene = new Nv12Frame(320, 240);
        dimScene.Data.AsSpan(0, 320 * 240).Fill(28);
        dimScene.Data.AsSpan(320 * 240).Fill(128);
        double totalMilliseconds = 0;
        byte[]? previous = null;
        foreach (var style in Enum.GetValues(styleType))
        {
            var overlay = Activator.CreateInstance(studioType, "", labelStyle, style, 1, false, true, false)!;
            var result = (Nv12Frame)applyOutput.Invoke(overlay, [screen])!;
            int bottomCenter = 320 * 239 + 160;
            Check(result.Data[bottomCenter] != original[bottomCenter], "Mic stem must meet the bottom center");
            Check(result.Data[320 * 239] == original[320 * 239], "Bottom corner must stay transparent");
            var dimResult = (Nv12Frame)applyOutput.Invoke(overlay, [dimScene])!;
            Check(dimResult.Data[bottomCenter] < result.Data[bottomCenter] - 6, "Mic stand did not dim with the camera scene");
            var dimPreview = new byte[320 * 240 * 4];
            Nv12Scaler.ToBgra(dimScene, dimPreview, 320, 240, mirror: true);
            applyPreview.Invoke(overlay, [dimPreview, 320, 240]);
            Check(screen.Data.SequenceEqual(original), "Mic overlay changed its shared input");
            Check(result.Data.AsSpan(0, 320 * 120).SequenceEqual(original.AsSpan(0, 320 * 120)), "Bottom mic painted outside its region");
            Check(previous == null || !previous.SequenceEqual(result.Data), "Both styles rendered the same mic");
            previous = result.Data;
            var preview = new byte[320 * 240 * 4];
            Nv12Scaler.ToBgra(screen, preview, 320, 240, mirror: true);
            var before = preview[bottomCenter * 4];
            applyPreview.Invoke(overlay, [preview, 320, 240]);
            Check(preview[bottomCenter * 4] != before, "Mic stem did not reach the bottom of the mirrored preview");
            Check(dimPreview[bottomCenter * 4] < preview[bottomCenter * 4] - 6, "Preview mic did not dim with the camera scene");
            var withLabel = Activator.CreateInstance(studioType, "Studio test", labelStyle, style, 1, false, true, false)!;
            var labeled = (Nv12Frame)applyOutput.Invoke(withLabel, [dimScene])!;
            Check(labeled.Data[320 * 239 + 60] < labeled.Data[320 * 239 + 260], "Gradient must run left to right along the bottom");
            Check(labeled.Data[320 * 225 + 60] == dimResult.Data[320 * 225 + 60], "Lower third started too high");
            var labelPreview = new byte[320 * 240 * 4];
            Nv12Scaler.ToBgra(dimScene, labelPreview, 320, 240, mirror: true);
            applyPreview.Invoke(withLabel, [labelPreview, 320, 240]);
            var textColumns = from y in Enumerable.Range(226, 14)
                              from x in Enumerable.Range(0, 320)
                              where labelPreview[(y * 320 + x) * 4] > 200
                              select x;
            var visibleText = textColumns.ToArray();
            Check(visibleText.Length > 0 && Math.Abs((visibleText.Min() + visibleText.Max()) / 2 - 160) <= 3,
                "Lower-third text must stay bright and centered in low light");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 30; i++) applyOutput.Invoke(withLabel, [hd]);
            totalMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
        }
        Console.WriteLine($"PASS: two upright mics, bottom-center anchoring, scene lighting, horizontal gradients, centered text and clean-source isolation. 1080p average {totalMilliseconds / 60:0.00} ms.");
    }
    catch (Exception ex) { studioFailure = ex; }
});
studioThread.SetApartmentState(ApartmentState.STA);
studioThread.Start();
studioThread.Join();
if (studioFailure != null) throw new Exception("Studio overlay smoke test failed", studioFailure);
