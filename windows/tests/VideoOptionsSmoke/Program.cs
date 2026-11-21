using System.Text.Json.Nodes;
using CamStudio.Desktop.Core;
static void Check(bool ok,string message) { if (!ok) throw new Exception(message); }
Check(VideoOptionsReport.FromJson(new JsonObject())==null,"Old phone must leave controls disabled.");
var state=JsonNode.Parse("""{"videoOptionsVersion":1,"videoResolution":720,"videoBackground":3,"videoNatural":true,"videoOptionsAck":"request-1","videoOptionsError":""}""")!.AsObject();
var report=VideoOptionsReport.FromJson(state)!;
Check(report.Resolution==720 && report.Background==3 && report.Natural && report.Acknowledgement=="request-1","Phone state or acknowledgement was lost.");
state["videoOptionsVersion"]="1";
Check(VideoOptionsReport.FromJson(state)==null,"Malformed capability must not enable remote controls.");
state["videoOptionsVersion"]=2;
Check(VideoOptionsReport.FromJson(state)==null,"Unknown protocol version must not enable controls.");
var bytes=Protocol.FrameJson(Protocol.Control,new {cmd="videoOptions",changes=new {resolution=1080,background=2,natural=true},requestId="request-2"});
using var stream=new MemoryStream(bytes);
var message=await Protocol.ReadAsync(stream,new byte[5],new byte[8],CancellationToken.None);
Check(message.Type==Protocol.Control && message.Json()["cmd"]!.GetValue<string>()=="videoOptions","Wrong control routing.");
Check(message.Json()["changes"]!["resolution"]!.GetValue<int>()==1080,"HD resolution lost in framing.");
Console.WriteLine("PASS: old/malformed capability handling, acknowledgement, HD/background state and control framing.");

Check(ViewerLink.TryNormalize(" https://www.youtube.com/watch?v=abc123 ", "private-stream-key", out var viewer, out _) && viewer == "https://www.youtube.com/watch?v=abc123", "Viewer URL normalization failed.");
Check(!ViewerLink.TryNormalize("rtmp://a.rtmp.youtube.com/live2/private-stream-key", "private-stream-key", out _, out _), "RTMP publishing URL must not be shareable.");
Check(!ViewerLink.TryNormalize("https://user:password@example.com/watch", "", out _, out _), "Credential URL must be rejected.");
Check(!ViewerLink.TryNormalize("https://example.com/watch?token=private-stream-key", "private-stream-key", out _, out _), "Stream key leaked into sharing.");
Check(!ViewerLink.TryNormalize("https://example.com/watch?token=private%2Dstream%2Dkey", "private-stream-key", out _, out _), "Escaped stream key leaked into sharing.");
Check(ViewerLink.TryNormalize("", "private-stream-key", out viewer, out _) && viewer.Length == 0, "Missing viewer link must remain unset rather than fabricated.");
Console.WriteLine("PASS: watch links, missing link, rejected ingest/credential/secret URLs.");

Check(ViewerLink.TryNormalize("https://studio.youtube.com/video/Abc_def-123/livestreaming", "private-key", out viewer, out _) && viewer == "https://www.youtube.com/watch?v=Abc_def-123", "Studio broadcast URL must become a public watch link.");
Check(!ViewerLink.TryNormalize("https://studio.youtube.com/channel/UC/livestreaming", "private-key", out _, out _), "Studio dashboard must not be advertised as a public broadcast.");
Console.WriteLine("PASS: Studio broadcast URL conversion and dashboard rejection.");
