using System.Text.Json.Nodes;
using CamStudio.Desktop.Core;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
Check(BeautyReport.FromJson(new JsonObject()) == null, "Old phones must not advertise beauty support.");
var body = JsonNode.Parse("""
{"cmd":"beautyState","beautyVersion":1,"beautyAck":"edit-42","beautyOriginal":true,
 "beautyTracking":"Face tracked","beauty":{"enabled":true,"noseWidth":35,"noseAlar":999,"smile":-2,"chinLength":-200}}
""")!.AsObject();
var report = BeautyReport.FromJson(body)!;
Check(report.Settings.Enabled && report.Original && report.Acknowledgement == "edit-42", "Acknowledgement/compare state lost.");
Check(report.Settings.Values["noseWidth"] == 35 && report.Settings.Values["noseAlar"] == 100 &&
      report.Settings.Values["smile"] == 0 && report.Settings.Values["chinLength"] == -100, "Settings must be normalized.");
body["beautyVersion"] = 2;
Check(BeautyReport.FromJson(body) == null, "Unknown protocol version must not enable editing.");
body["beautyVersion"] = "1";
Check(BeautyReport.FromJson(body) == null, "Malformed capability version must be rejected.");
var edit = Protocol.FrameJson(Protocol.Control, new { cmd = "beauty", changes = new { noseTip = 25 }, requestId = "edit-43" });
using var wire = new MemoryStream(edit);
var message = await Protocol.ReadAsync(wire, new byte[5], new byte[8], CancellationToken.None);
Check(message.Type == Protocol.Control && message.Json()["changes"]!["noseTip"]!.GetValue<int>() == 25,
    "Partial edits must survive protocol framing.");
Check(message.Json()["changes"]!.AsObject().Count == 1, "An edit must not overwrite unrelated phone settings.");
Console.WriteLine("Beauty protocol checks passed: capabilities, acknowledgement, bounds, malformed input, partial edit framing.");
