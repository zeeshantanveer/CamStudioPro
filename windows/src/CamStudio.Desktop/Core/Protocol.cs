using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CamStudio.Desktop.Core;

/// <summary>Wire protocol v1, see docs/PROTOCOL.md.</summary>
public static class Protocol
{
    public const int Version = 1;
    public const int DiscoveryPort = 47800;
    public const int SessionPort = 47801;
    public const int MaxPayload = 8 * 1024 * 1024;

    public const byte Hello = 0x01;
    public const byte HelloAck = 0x02;
    public const byte VideoConfig = 0x03;
    public const byte VideoFrame = 0x04;
    public const byte AudioConfig = 0x05;
    public const byte AudioFrame = 0x06;
    public const byte Ping = 0x07;
    public const byte Pong = 0x08;
    public const byte State = 0x09;
    public const byte Stats = 0x0A;
    public const byte Control = 0x0B;

    public const byte FlagKeyframe = 1;
    public const byte FlagCodecConfig = 2;

    public readonly record struct Message(byte Type, byte[] Payload, int Length)
    {
        public ReadOnlySpan<byte> Span => Payload.AsSpan(0, Length);
        public JsonObject Json() => JsonNode.Parse(Encoding.UTF8.GetString(Span)) as JsonObject ?? new JsonObject();
    }

    /// <summary>Reads one message. Reuses <paramref name="buffer"/> when it is large enough.</summary>
    public static async Task<Message> ReadAsync(Stream stream, byte[] header, byte[] buffer, CancellationToken ct)
    {
        await stream.ReadExactlyAsync(header.AsMemory(0, 5), ct).ConfigureAwait(false);
        var type = header[0];
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));
        if (length < 0 || length > MaxPayload)
            throw new InvalidDataException($"payload too large: {length}");

        var target = buffer.Length >= length ? buffer : new byte[length];
        await stream.ReadExactlyAsync(target.AsMemory(0, length), ct).ConfigureAwait(false);
        return new Message(type, target, length);
    }

    public static byte[] Frame(byte type, ReadOnlySpan<byte> payload)
    {
        var bytes = new byte[5 + payload.Length];
        bytes[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(bytes.AsSpan(5));
        return bytes;
    }

    public static byte[] FrameJson(byte type, object value) =>
        Frame(type, JsonSerializer.SerializeToUtf8Bytes(value));
}
