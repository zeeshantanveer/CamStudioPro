using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CamStudio.Desktop.Core;

/// <summary>Answers phones' UDP "csp-discover" broadcasts with this PC's identity.</summary>
public sealed class DiscoveryResponder : IDisposable
{
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public DiscoveryResponder(AppSettings settings) => _settings = settings;

    public string? Error { get; private set; }

    public void Start()
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
            _ = Task.Run(() => LoopAsync(_cts.Token));
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var udp = _udp!;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (result.Buffer.Length > 1024)
                    continue;

                var json = JsonNode.Parse(Encoding.UTF8.GetString(result.Buffer)) as JsonObject;
                if (json?["t"]?.GetValue<string>() != "csp-discover")
                    continue;

                var reply = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    t = "csp-announce",
                    v = Protocol.Version,
                    id = _settings.PcId,
                    name = Environment.MachineName,
                    os = NetInfo.OsName(),
                    port = Protocol.SessionPort,
                });
                await udp.SendAsync(reply, result.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // malformed datagram or transient socket error: keep listening
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
    }
}
