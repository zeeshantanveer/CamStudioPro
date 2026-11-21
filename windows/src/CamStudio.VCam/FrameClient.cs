using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace CamStudio.VCam
{
    /// <summary>
    /// Pulls the latest NV12 frame from the CamStudio desktop app over loopback TCP.
    /// The media source runs inside the Windows Frame Server (a service), so a loopback
    /// socket is the simplest channel that needs no shared kernel-object permissions.
    ///
    /// Request : u32 width, u32 height   (big endian)
    /// Response: u8 status (1 = frame follows), then width*height*3/2 NV12 bytes
    /// </summary>
    internal sealed class FrameClient : IDisposable
    {
        private Socket? _socket;
        private long _nextAttemptTicks;
        private readonly byte[] _request = new byte[8];
        private readonly byte[] _status = new byte[1];

        public bool TryFill(Span<byte> nv12, int width, int height)
        {
            var socket = EnsureConnected();
            if (socket == null)
                return false;

            try
            {
                BinaryPrimitives.WriteUInt32BigEndian(_request.AsSpan(0, 4), (uint)width);
                BinaryPrimitives.WriteUInt32BigEndian(_request.AsSpan(4, 4), (uint)height);
                var sent = 0;
                while (sent < _request.Length)
                {
                    var n = socket.Send(_request.AsSpan(sent));
                    if (n == 0) throw new System.IO.EndOfStreamException();
                    sent += n;
                }

                ReadExactly(socket, _status);
                if (_status[0] != 1)
                    return false;

                ReadExactly(socket, nv12);
                return true;
            }
            catch (Exception e)
            {
                EventProvider.LogInfo("frame fetch failed: " + e.Message);
                Drop();
                return false;
            }
        }

        private Socket? EnsureConnected()
        {
            if (_socket != null)
                return _socket;

            var now = Environment.TickCount64;
            if (now < _nextAttemptTicks)
                return null;

            _nextAttemptTicks = now + 1000;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                ReceiveTimeout = 500,
                SendTimeout = 500,
                ReceiveBufferSize = 4 * 1024 * 1024,
            };
            try
            {
                var result = socket.BeginConnect(new IPEndPoint(IPAddress.Loopback, Shared.FramePort), null, null);
                if (!result.AsyncWaitHandle.WaitOne(200) || !socket.Connected)
                {
                    socket.Dispose();
                    return null;
                }
                socket.EndConnect(result);
                _socket = socket;
                return socket;
            }
            catch
            {
                socket.Dispose();
                return null;
            }
        }

        private static void ReadExactly(Socket socket, Span<byte> buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = socket.Receive(buffer[read..]);
                if (n <= 0)
                    throw new SocketException((int)SocketError.ConnectionReset);
                read += n;
            }
        }

        private void Drop()
        {
            try { _socket?.Dispose(); } catch { }
            _socket = null;
        }

        public void Dispose() => Drop();
    }
}
