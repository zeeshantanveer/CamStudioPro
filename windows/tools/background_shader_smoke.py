"""Compile and render the Android background-replacement shaders in headless Chromium WebGL 1.

Checks the person stays untouched, the background is replaced, the low-resolution mask is
re-fitted to real colour edges (edge-aware refinement), lighting gain and grain behave, and
the background preparation shader compiles. Uses only Python's standard library; needs
Chrome or Edge. Output page goes to windows/tests/BackgroundShader/output.
"""
import html
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "android/app/src/main/java/com/camstudio/pro/media/GlPipeline.kt"
OUTPUT = (ROOT / "windows/tests/BackgroundShader/output").resolve()
assert OUTPUT.is_relative_to(ROOT)
OUTPUT.mkdir(parents=True, exist_ok=True)
source = SOURCE.read_text(encoding="utf-8-sig")


def shader(name):
    return re.search(r"private const val " + name + r' = (?:FS_HEAD \+ )?"""(.*?)"""', source, re.S)[1]


head = shader("FS_HEAD")
shaders = json.dumps({"vs": shader("VS"), "fs": head + shader("FS_COMP"), "bg": head + shader("FS_BG")})
page = OUTPUT / "background-check.html"
page.write_text("""<!doctype html><meta charset="utf-8"><body><script>
const source = SOURCE_JSON;
const N = 96, M = 12;
function check(ok, message) { if (!ok) throw Error(message); }
try {
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = N;
  const gl = canvas.getContext('webgl', {preserveDrawingBuffer:true, antialias:false});
  check(gl, 'WebGL 1 unavailable');
  function program(fs, defs) {
    function compile(type, code) {
      const s = gl.createShader(type); gl.shaderSource(s, code); gl.compileShader(s);
      check(gl.getShaderParameter(s, gl.COMPILE_STATUS), gl.getShaderInfoLog(s)); return s;
    }
    const p = gl.createProgram();
    gl.attachShader(p, compile(gl.VERTEX_SHADER, source.vs));
    gl.attachShader(p, compile(gl.FRAGMENT_SHADER, defs + fs));
    gl.bindAttribLocation(p, 0, 'aPos'); gl.linkProgram(p);
    check(gl.getProgramParameter(p, gl.LINK_STATUS), gl.getProgramInfoLog(p)); return p;
  }
  program(source.bg, '');                       // background preparation pass compiles
  const comp = program(source.fs, '#define BEAUTY\\n');
  const vbo = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1,1,-1,-1,1,1,1]), gl.STATIC_DRAW);
  gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0,2,gl.FLOAT,false,0,0);

  function texture(unit, w, h, format, data) {
    const t = gl.createTexture(); gl.activeTexture(gl.TEXTURE0 + unit); gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
    gl.texImage2D(gl.TEXTURE_2D,0,format,w,h,0,format,gl.UNSIGNED_BYTE,data); return t;
  }
  // Scene: person (blue) on the left half, real wall (beige) on the right half.
  const PERSON=[40,60,200], WALL=[200,180,150], GREEN=[30,200,60];
  const scene = new Uint8Array(N*N*4);
  for (let y=0;y<N;y++) for (let x=0;x<N;x++) { const c = x < N/2 ? PERSON : WALL; scene.set([...c,255],(y*N+x)*4); }
  // Low-res mask whose person edge is one mask texel too far right (typical segmentation error).
  const mask = new Uint8Array(M*M);
  for (let y=0;y<M;y++) for (let x=0;x<M;x++) mask[y*M+x] = x < M/2 + 1 ? 255 : 0;
  const bg = new Uint8Array(N*N*4);
  for (let i=0;i<N*N;i++) bg.set([...GREEN,255], i*4);
  texture(0, N, N, gl.RGBA, scene);
  texture(1, N, N, gl.RGBA, bg);
  texture(2, M, M, gl.LUMINANCE, mask);
  texture(3, N, N, gl.RGBA, bg);
  const previousTexture = texture(4, N, N, gl.RGBA, scene);

  gl.useProgram(comp);
  const u = n => gl.getUniformLocation(comp, n);
  gl.uniform1i(u('sScene'),0); gl.uniform1i(u('sBg'),1); gl.uniform1i(u('sMask'),2); gl.uniform1i(u('sBgBlur'),3);
  gl.uniform1i(u('sPreviousScene'),4); gl.uniform1f(u('uMotionGuard'),0);
  gl.uniform3f(u('uCrop'),.5,.5,1); gl.uniform2f(u('uTexel'),1/N,1/N); gl.uniform2f(u('uMaskTexel'),1/M,1/M);
  gl.uniform1i(u('uBgMode'),2); gl.uniform2f(u('uEdge'),.85,.12); gl.uniform3f(u('uBgGain'),1,1,1);
  gl.uniform4f(u('uBeautyBasis'),1,0,1,0);
  function draw() {
    gl.viewport(0,0,N,N); gl.drawArrays(gl.TRIANGLE_STRIP,0,4);
    const out = new Uint8Array(N*N*4); gl.readPixels(0,0,N,N,gl.RGBA,gl.UNSIGNED_BYTE,out);
    check(gl.getError()===gl.NO_ERROR,'GL error'); return out;
  }
  const px = (img, x, y) => Array.from(img.slice((y*N+x)*4, (y*N+x)*4+3));
  const near = (a, b, tol) => a.every((v,i) => Math.abs(v-b[i]) <= tol);

  const plain = draw();
  check(near(px(plain,8,48), PERSON, 2), 'Person interior changed: ' + px(plain,8,48));
  check(near(px(plain,90,48), GREEN, 2), 'Background not replaced: ' + px(plain,90,48));
  // Wall pixels inside the mis-aligned mask band must mostly show the new background.
  const band = px(plain, 52, 48);
  check(band[0] < 150, 'Edge refinement did not snap the mask to the real edge: ' + band);

  gl.uniform3f(u('uBgGain'),.5,.5,.5);
  const dim = draw();
  check(near(px(dim,90,48), GREEN.map(v=>v*.5), 2), 'Lighting gain not applied: ' + px(dim,90,48));
  check(near(px(dim,8,48), PERSON, 2), 'Lighting gain leaked onto the person');
  gl.uniform3f(u('uBgGain'),1,1,1);

  gl.uniform4f(u('uBgFx'),0,.02,.37,0);
  const grain = draw();
  let sum=0, sq=0, n=0;
  for (let y=10;y<86;y++) for (let x=70;x<92;x++) { const v=px(grain,x,y)[1]; sum+=v; sq+=v*v; n++; }
  const mean=sum/n, sd=Math.sqrt(sq/n-mean*mean);
  check(Math.abs(mean-GREEN[1]) < 4, 'Grain shifted the background brightness: ' + mean);
  check(sd > 1.5 && sd < 12, 'Grain amplitude out of range: ' + sd);
  check(near(px(grain,8,48), PERSON, 2), 'Grain leaked onto the person');
  gl.uniform4f(u('uBgFx'),0,.02,.81,0);
  const grain2 = draw();
  check(px(grain2,80,40).some((v,i)=>v!==px(grain,80,40)[i]) || px(grain2,81,40).some((v,i)=>v!==px(grain,81,40)[i]),
        'Grain is frozen between frames');
  // An uncertain region (55 % confidence, e.g. wall along an arm or part of a chair) counts as background.
  gl.uniform4f(u('uBgFx'),0,0,0,0);
  gl.activeTexture(gl.TEXTURE2);
  gl.texImage2D(gl.TEXTURE_2D,0,gl.LUMINANCE,M,M,0,gl.LUMINANCE,gl.UNSIGNED_BYTE,new Uint8Array(M*M).fill(140));
  const unsure = draw();
  check(px(unsure,90,48)[0] < 90, 'Uncertain mask region was kept as person: ' + px(unsure,90,48));
  // A narrow uncertain feature can be erased by spatial smoothing. Motion history
  // must preserve its original matte, while static and missing-history cases stay identical.
  const uniformPerson = new Uint8Array(N*N*4);
  for(let i=0;i<N*N;i++) uniformPerson.set([...PERSON,255],i*4);
  gl.activeTexture(gl.TEXTURE0); gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,N,N,0,gl.RGBA,gl.UNSIGNED_BYTE,uniformPerson);
  const stripe = new Uint8Array(M*M);
  for(let y=0;y<M;y++) stripe[y*M+7]=200;
  gl.activeTexture(gl.TEXTURE2); gl.texImage2D(gl.TEXTURE_2D,0,gl.LUMINANCE,M,M,0,gl.LUMINANCE,gl.UNSIGNED_BYTE,stripe);
  gl.activeTexture(gl.TEXTURE4); gl.bindTexture(gl.TEXTURE_2D,previousTexture);
  gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,N,N,0,gl.RGBA,gl.UNSIGNED_BYTE,uniformPerson);
  const noHistory=draw();
  gl.uniform1f(u('uMotionGuard'),1); const staticHistory=draw();
  check(staticHistory.every((v,i)=>v===noHistory[i]),'Static history altered the baseline');
  gl.activeTexture(gl.TEXTURE4); gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,N,N,0,gl.RGBA,gl.UNSIGNED_BYTE,bg);
  const movingFeature=draw();
  check(px(movingFeature,60,48)[2]>px(noHistory,60,48)[2]+50,'Motion did not protect the narrow foreground feature');
  check(near(px(movingFeature,5,48),GREEN,2),'Motion kept confident background as foreground');
  gl.uniform1f(u('uMotionGuard'),0);const disabled=draw();
  check(disabled.every((v,i)=>v===noHistory[i]),'Missing/disabled history altered the baseline');
  document.body.textContent='PASS: background shaders compiled; person untouched; background replaced; edge refined; lighting and grain behave; motion preserves narrow foreground; static/disabled history matches baseline.';
} catch(e) { document.body.textContent='FAIL: '+e.stack; }
</script>""".replace("SOURCE_JSON", shaders), encoding="utf-8")
browsers = [Path(os.environ.get("PROGRAMFILES", "C:/Program Files")) / "Google/Chrome/Application/chrome.exe",
            Path(os.environ.get("PROGRAMFILES(X86)", "C:/Program Files (x86)")) / "Microsoft/Edge/Application/msedge.exe"]
browser = next((p for p in browsers if p.is_file()), None)
if not browser:
    raise SystemExit("Chrome/Edge is required for shader checks.")
result = subprocess.run([str(browser), "--headless=new", "--no-first-run", "--disable-extensions",
    "--disable-background-networking", "--use-angle=swiftshader", "--enable-unsafe-swiftshader",
    "--user-data-dir=" + tempfile.mkdtemp(prefix="camstudio-background-browser-"), "--dump-dom", page.as_uri()],
    capture_output=True, text=True, timeout=45, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
match = re.search(r"<body>(.*?)</body>", result.stdout, re.S)
message = html.unescape(match[1]) if match else f"Browser exited {result.returncode}; stdout={result.stdout[-1000:]}; stderr={result.stderr[-2000:]}"
print(message)
if not message.startswith("PASS:"):
    raise SystemExit(1)
