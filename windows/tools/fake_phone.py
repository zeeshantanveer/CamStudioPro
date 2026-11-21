"""Simulated phone for testing the Windows client without a device.

Discovers the PC over UDP, connects, and streams an ffmpeg test pattern (H.264) plus a
sine-tone over the CamStudio protocol (docs/PROTOCOL.md). Needs ffmpeg on PATH.

  python fake_phone.py [--host 127.0.0.1] [--seconds 20] [--pair-key KEY] [--size 1280x720]
"""
import argparse, json, socket, struct, subprocess, threading, time, math, sys

HELLO, ACK, VCFG, VFRAME, ACFG, AFRAME, PING, PONG, STATE, STATS, CONTROL = range(1, 12)


def msg(t, payload):
    return struct.pack(">BI", t, len(payload)) + payload


def jmsg(t, obj):
    return msg(t, json.dumps(obj).encode())


def read_exact(s, n):
    b = b""
    while len(b) < n:
        c = s.recv(n - len(b))
        if not c:
            raise ConnectionError("closed")
        b += c
    return b


def read_msg(s):
    t, n = struct.unpack(">BI", read_exact(s, 5))
    return t, read_exact(s, n)


def discover(timeout=3):
    u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    u.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    u.settimeout(timeout)
    u.sendto(b'{"t":"csp-discover","v":1}', ("255.255.255.255", 47800))
    u.sendto(b'{"t":"csp-discover","v":1}', ("127.0.0.1", 47800))
    try:
        data, addr = u.recvfrom(2048)
        return json.loads(data), addr[0]
    except socket.timeout:
        return None, None


def access_units(stream):
    """Split an Annex-B byte stream into access units using AUD NALs (x264 aud=1)."""
    buf = b""
    while True:
        chunk = stream.read(65536)
        if not chunk:
            break
        buf += chunk
        while True:
            i = buf.find(b"\x00\x00\x00\x01\x09", 1)
            if i < 0:
                break
            yield buf[:i]
            buf = buf[i:]
    if buf:
        yield buf


def nal_types(au):
    types, i = [], 0
    while True:
        i = au.find(b"\x00\x00\x01", i)
        if i < 0:
            return types
        types.append(au[i + 3] & 0x1F)
        i += 3


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host")
    ap.add_argument("--seconds", type=float, default=20)
    ap.add_argument("--pair-key")
    ap.add_argument("--device-id", default="fake-phone-0001")
    ap.add_argument("--size", default="1280x720")
    ap.add_argument("--fps", type=int, default=30)
    a = ap.parse_args()

    ann, ip = discover()
    print("discovery:", ann, ip)
    host = a.host or ip or "127.0.0.1"

    s = socket.create_connection((host, 47801), timeout=70)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    hello = {"v": 1, "deviceId": a.device_id, "deviceName": "Test Phone", "model": "ffmpeg testsrc", "transport": "wifi"}
    if a.pair_key:
        hello["pairKey"] = a.pair_key
    s.sendall(jmsg(HELLO, hello))
    t, p = read_msg(s)
    ack = json.loads(p)
    print("ack:", ack)
    if not ack.get("ok"):
        return 1
    s.settimeout(None)

    w, h = map(int, a.size.split("x"))
    s.sendall(jmsg(VCFG, {"codec": "h264", "width": w, "height": h, "fps": a.fps, "bitrate": 4000000}))
    s.sendall(jmsg(ACFG, {"sampleRate": 48000, "channels": 1, "format": "pcm_s16le"}))

    lock = threading.Lock()
    stop = threading.Event()
    stats = {}
    keyframe_requests = [0]

    def send(b):
        with lock:
            s.sendall(b)

    def reader():
        try:
            while not stop.is_set():
                t, p = read_msg(s)
                if t == STATS:
                    stats.update(json.loads(p))
                elif t == CONTROL:
                    c = json.loads(p)
                    if c.get("cmd") == "keyframe":
                        keyframe_requests[0] += 1
                    elif c.get("cmd") == "bye":
                        print("PC said bye")
                        stop.set()
                elif t == PONG:
                    pass
        except Exception as e:
            if not stop.is_set():
                print("reader:", e)
            stop.set()

    threading.Thread(target=reader, daemon=True).start()

    ff = subprocess.Popen([
        "ffmpeg", "-loglevel", "error", "-re", "-f", "lavfi", "-i", f"testsrc2=size={w}x{h}:rate={a.fps}",
        "-t", str(a.seconds), "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
        "-profile:v", "baseline", "-pix_fmt", "yuv420p", "-g", str(a.fps * 2), "-b:v", "4M",
        "-x264-params", "aud=1:repeat-headers=1", "-bsf:v", "h264_mp4toannexb", "-f", "h264", "-"],
        stdout=subprocess.PIPE)

    def audio():
        n = 0
        while not stop.is_set():
            samples = b"".join(struct.pack("<h", int(6000 * math.sin(2 * math.pi * 440 * (n + i) / 48000))) for i in range(960))
            n += 960
            send(msg(AFRAME, struct.pack(">Q", int(time.time() * 1e6)) + samples))
            time.sleep(0.02)

    def ticker():
        while not stop.is_set():
            send(msg(PING, struct.pack(">Q", int(time.time() * 1000))))
            send(jmsg(STATE, {"battery": 77, "charging": True, "tempC": 36.5, "wifiRssi": -50, "wifiBand": "5 GHz",
                              "muted": False, "paused": False, "sentFps": a.fps, "bitrateBps": 4000000, "rttMs": 3, "encodeMs": 8}))
            time.sleep(1)

    threading.Thread(target=audio, daemon=True).start()
    threading.Thread(target=ticker, daemon=True).start()

    start = time.time()
    frames = 0
    for au in access_units(ff.stdout):
        if stop.is_set():
            break
        types = nal_types(au)
        flags = 1 if 5 in types else 0
        send(msg(VFRAME, struct.pack(">QB", int((time.time() - start) * 1e6), flags) + au))
        frames += 1
        if frames % (a.fps * 2) == 0:
            print(f"sent {frames} frames; PC stats: {stats}")

    stop.set()
    try:
        send(jmsg(CONTROL, {"cmd": "bye"}))
    except Exception:
        pass
    s.close()
    print(f"done: sent {frames} frames; last PC stats: {stats}; keyframe requests: {keyframe_requests[0]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
