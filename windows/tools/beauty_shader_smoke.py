"""Compile and render the actual Android composite shader in headless Chromium WebGL 1.

Uses only Python's standard library. Requires Chrome/Edge. The test page stays in
windows/tests/BeautyShader/output; isolated browser profiles use the system temp folder.
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
OUTPUT = (ROOT / "windows/tests/BeautyShader/output").resolve()
assert OUTPUT.is_relative_to(ROOT)
OUTPUT.mkdir(parents=True, exist_ok=True)
source = SOURCE.read_text(encoding="utf-8-sig")


def shader(name):
    return re.search(r"private const val " + name + r' = (?:FS_HEAD \+ )?"""(.*?)"""', source, re.S)[1]


shaders = json.dumps({"vs": shader("VS"), "fs": shader("FS_HEAD") + shader("FS_COMP")})
page = OUTPUT / "shader-check.html"
page.write_text("""<!doctype html><meta charset="utf-8"><body><script>
const source = SOURCE_JSON;
function check(ok, message) { if (!ok) throw Error(message); }
try {
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = 96;
  const gl = canvas.getContext('webgl', {preserveDrawingBuffer:true, antialias:false});
  check(gl, 'WebGL 1 unavailable');
  function program(beauty) {
    function compile(type, code) {
      const s = gl.createShader(type); gl.shaderSource(s, code); gl.compileShader(s);
      check(gl.getShaderParameter(s, gl.COMPILE_STATUS), gl.getShaderInfoLog(s)); return s;
    }
    const p = gl.createProgram();
    gl.attachShader(p, compile(gl.VERTEX_SHADER, source.vs));
    gl.attachShader(p, compile(gl.FRAGMENT_SHADER, (beauty ? '#define BEAUTY\\n' : '') + source.fs));
    gl.bindAttribLocation(p, 0, 'aPos'); gl.linkProgram(p);
    check(gl.getProgramParameter(p, gl.LINK_STATUS), gl.getProgramInfoLog(p)); return p;
  }
  const fallback = program(false), beauty = program(true);
  const vbo = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1,1,-1,-1,1,1,1]), gl.STATIC_DRAW);
  gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0,2,gl.FLOAT,false,0,0);
  const pixels = new Uint8Array(96*96*4);
  for(let y=0;y<96;y++) for(let x=0;x<96;x++) {
    const i=(y*96+x)*4; pixels[i]=x*255/95; pixels[i+1]=y*255/95;
    pixels[i+2]=((x>>3)+(y>>3))%2 ? 160 : 80; pixels[i+3]=255;
  }
  const tex=gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D,tex);
  gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
  gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,96,96,0,gl.RGBA,gl.UNSIGNED_BYTE,pixels);
  function use(p) {
    gl.useProgram(p);
    const u=n=>gl.getUniformLocation(p,n);
    gl.uniform1i(u('sScene'),0); gl.uniform3f(u('uCrop'),.5,.5,1);
    gl.uniform2f(u('uTexel'),1/96,1/96); gl.uniform2f(u('uBgScale'),1,1);
    gl.uniform4f(u('uBeautyBasis'),1,0,1,0);
    gl.uniform4f(u('uBeautyFace'),.5,.5,.6,.8);
    gl.uniform4f(u('uBeautyEyes'),.4,.63,.6,.63);
    gl.uniform2f(u('uBeautyMouth'),.5,.35);
    return u;
  }
  function draw() {
    gl.viewport(0,0,96,96); gl.drawArrays(gl.TRIANGLE_STRIP,0,4);
    const out = new Uint8Array(pixels.length); gl.readPixels(0,0,96,96,gl.RGBA,gl.UNSIGNED_BYTE,out);
    check(gl.getError()===gl.NO_ERROR,'GL error'); return out;
  }
  use(fallback); const baseline=draw();
  const u=use(beauty); const disabled=draw();
  check(disabled.every((v,i)=>v===baseline[i]), 'Disabled beauty changed the image');
  gl.uniform4f(u('uBeautyBasis'),1,0,1,1);
  gl.uniform1i(u('uBeautyCount'),1);
  gl.uniform4fv(u('uBeautyRegions[0]'),new Float32Array([.4,.5,.2,.2]));
  gl.uniform2fv(u('uBeautyMoves[0]'),new Float32Array([.06,0]));
  const warped=draw();
  check(warped.some((v,i)=>v!==baseline[i]),'Enabled warp did not affect the image');
  for(const [x,y] of [[0,0],[95,95],[4,48],[90,48]]) {
    const i=(y*96+x)*4; check(warped.slice(i,i+4).every((v,k)=>v===baseline[i+k]),'Warp escaped its local region');
  }
  const regions=new Float32Array(20*4), moves=new Float32Array(20*2);
  for(let i=0;i<20;i++) { regions.set([.5,.5,.1,.1],i*4); moves.set([.001,0],i*2); }
  gl.uniform1i(u('uBeautyCount'),20); gl.uniform4fv(u('uBeautyRegions[0]'),regions);
  gl.uniform2fv(u('uBeautyMoves[0]'),moves); draw();
  gl.uniform1i(u('uBeautyCount'),0); gl.uniform3f(u('uBeautySkin'),0,1,0);
  const bright=draw(); const center=(48*96+48)*4;
  check(bright[center]>baseline[center], 'Skin brightness did not brighten the face');
  check(bright[0]===baseline[0], 'Skin brightness changed the background');
  // Nose contour: vertical bridge from (.5,.62) down to (.5,.38); face width .6 -> half-width .042.
  gl.uniform3f(u('uBeautySkin'),0,0,1); gl.uniform4f(u('uBeautyNose'),.5,.62,.5,.38);
  const contour=draw(); const side=(48*96+52)*4;
  check(contour[side]<baseline[side], 'Nose contour did not shade the bridge side');
  for(const [x,y] of [[0,0],[95,95],[10,48],[48,90]]) {
    const i=(y*96+x)*4; check(contour.slice(i,i+4).every((v,k)=>v===baseline[i+k]),'Nose contour escaped the nose');
  }
  gl.uniform3f(u('uBeautySkin'),0,0,0);
  document.body.textContent='PASS: beauty/fallback shaders compiled; bypass is exact; all 20 warps render; deformation is localized; skin brightness is face-limited; nose contour is nose-limited.';
} catch(e) { document.body.textContent='FAIL: '+e.stack; }
</script>""".replace("SOURCE_JSON", shaders), encoding="utf-8")
browsers = [Path(os.environ.get("PROGRAMFILES", "C:/Program Files")) / "Google/Chrome/Application/chrome.exe",
            Path(os.environ.get("PROGRAMFILES(X86)", "C:/Program Files (x86)")) / "Microsoft/Edge/Application/msedge.exe"]
browser = next((p for p in browsers if p.is_file()), None)
if not browser:
    raise SystemExit("Chrome/Edge is required for shader checks.")
result = subprocess.run([str(browser), "--headless=new", "--no-first-run", "--disable-extensions",
    "--disable-background-networking", "--use-angle=swiftshader", "--enable-unsafe-swiftshader",
    "--user-data-dir=" + tempfile.mkdtemp(prefix="camstudio-beauty-browser-"), "--dump-dom", page.as_uri()],
    capture_output=True, text=True, timeout=45, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
match = re.search(r"<body>(.*?)</body>", result.stdout, re.S)
message = html.unescape(match[1]) if match else f"Browser exited {result.returncode}; stdout={result.stdout[-1000:]}; stderr={result.stderr[-2000:]}"
print(message)
if not message.startswith("PASS:"):
    raise SystemExit(1)
