package com.camstudio.pro.media

import android.content.Context
import android.graphics.Bitmap
import android.media.MediaPlayer
import android.net.Uri
import android.graphics.PointF
import android.graphics.RectF
import android.graphics.SurfaceTexture
import android.opengl.EGL14
import android.opengl.EGLConfig
import android.opengl.EGLContext
import android.opengl.EGLDisplay
import android.opengl.EGLExt
import android.opengl.EGLSurface
import android.opengl.GLES11Ext
import android.opengl.GLES20
import android.opengl.GLUtils
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.view.Surface
import com.camstudio.pro.data.BeautyState
import kotlinx.coroutines.flow.MutableStateFlow
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.FloatBuffer
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sin

/** Everything the renderer needs for one frame. Immutable; replace the whole object. */
data class RenderParams(
    val outW: Int = 1920,
    val outH: Int = 1080,
    val deviceRot: Int = 0,        // how the phone is held: 0, 90, 180, 270 (clockwise from natural)
    val manualRot: Int = 0,        // "Rotate stream" offset
    val mirror: Boolean = false,
    val frontCamera: Boolean = false,
    val brightness: Float = 0f,    // -1..1
    val contrast: Float = 0f,
    val saturation: Float = 0f,
    val warmth: Float = 0f,
    val sharpness: Float = 0f,     // 0..1
    val look: Int = 0,
    val smooth: Float = 0f,        // 0..1
    val lowLight: Boolean = false,
    val bgMode: Int = 0,           // 0 none, 1 blur, 2 image / video / captured room
    val blurStrength: Float = 0.7f,
    val edge: Float = 0.85f,
    val bgDepth: Float = 0.3f,     // 0..1 lens blur on a replaced background
    val bgMatch: Float = 0.6f,     // 0..1 match the background to the real room's light
    val bgGrain: Float = 0.5f,     // 0..1 camera grain on the background, 0.5 = natural
    val bgWrap: Float = 0.35f,     // 0..1 background light wrapping around the outline
    val sensorIso: Int? = null,    // current camera ISO, scales the grain
    val autoFrame: Boolean = false,
    val zebra: Boolean = false,
    val peaking: Boolean = false,
    val grid: Boolean = false,
    val compare: Boolean = false,
    val wantFace: Boolean = false,
    val wantHist: Boolean = false,
    val encodePaused: Boolean = false,
    val encodeFpsCap: Int = 0,     // 0 = every camera frame
    val beauty: BeautyState = BeautyState(),
    val beautyOriginal: Boolean = false,
    val thermalLimited: Boolean = false,
)

/**
 * Camera OES texture -> colour stage -> optional background stage -> crop ->
 * encoder surface + on-screen preview. All GL work happens on one HandlerThread.
 */
class GlPipeline {
    companion object {
        private const val TAG = "GlPipeline"
        private const val EGL_RECORDABLE_ANDROID = 0x3142
        /** Scene frames kept for mask synchronisation; the delay is at most RING - 1 frames. */
        private const val RING = 4
        /** A small confidence bias removes uncertain wall pixels without cutting away hair. */
        private const val PERSON_BIAS = 0.05f
        private const val VS = """
attribute vec2 aPos;
varying vec2 vUv;
void main() {
  vUv = aPos * 0.5 + 0.5;
  gl_Position = vec4(aPos, 0.0, 1.0);
}"""

        private const val FS_HEAD = """
#ifdef GL_FRAGMENT_PRECISION_HIGH
precision highp float;
#else
precision mediump float;
#endif
"""

        private const val VS_CAM = """
attribute vec2 aPos;
uniform mat4 uTexMatrix;
uniform vec2 uRot;
uniform vec2 uScale;
uniform float uMirror;
varying vec2 vTex;
void main() {
  vec2 p = aPos * 0.5;
  p.x *= uMirror;
  p *= uScale;
  p = vec2(p.x * uRot.x - p.y * uRot.y, p.x * uRot.y + p.y * uRot.x);
  vTex = (uTexMatrix * vec4(p + 0.5, 0.0, 1.0)).xy;
  gl_Position = vec4(aPos, 0.0, 1.0);
}"""

        private const val FS_CAM = """#extension GL_OES_EGL_image_external : require

#ifdef GL_FRAGMENT_PRECISION_HIGH
precision highp float;
#else
precision mediump float;
#endif

uniform samplerExternalOES sTex;
uniform float uBright;
uniform float uContrast;
uniform float uSat;
uniform float uWarm;
uniform float uLow;
uniform int uLook;
varying vec2 vTex;
const vec3 LUMA = vec3(0.2126, 0.7152, 0.0722);
void main() {
  vec3 c = texture2D(sTex, vTex).rgb;
  if (uLow > 0.5) {
    c = pow(c, vec3(0.72)) * 1.18;
  }
  c += uBright * 0.25;
  c = (c - 0.5) * (1.0 + uContrast * 0.6) + 0.5;
  float l = dot(c, LUMA);
  c = mix(vec3(l), c, 1.0 + uSat);
  c.r += uWarm * 0.08;
  c.b -= uWarm * 0.08;
  if (uLook == 1) {
    c *= vec3(1.07, 1.0, 0.92);
  } else if (uLook == 2) {
    c *= vec3(0.93, 1.0, 1.08);
  } else if (uLook == 3) {
    c = vec3(dot(c, LUMA));
  } else if (uLook == 4) {
    float lv = dot(c, LUMA);
    c = mix(vec3(lv), c, 1.35);
    c = (c - 0.5) * 1.1 + 0.5;
  } else if (uLook == 5) {
    float lf = dot(c, LUMA);
    c = mix(vec3(lf), c, 0.82);
    c = c * 0.88 + 0.06;
    c *= vec3(1.04, 1.0, 0.94);
  }
  gl_FragColor = vec4(clamp(c, 0.0, 1.0), 1.0);
}"""

        private const val FS_BLUR = FS_HEAD + """
uniform sampler2D sTex;
uniform vec2 uDir;
varying vec2 vUv;
void main() {
  vec3 c = texture2D(sTex, vUv).rgb * 0.227027;
  c += (texture2D(sTex, vUv + uDir * 1.3846) + texture2D(sTex, vUv - uDir * 1.3846)).rgb * 0.316216;
  c += (texture2D(sTex, vUv + uDir * 3.2308) + texture2D(sTex, vUv - uDir * 3.2308)).rgb * 0.070270;
  gl_FragColor = vec4(c, 1.0);
}"""

        /** Cover-fits a background image into scene space (y up). Bitmap rows run top-down, hence the flip. */
        private const val FS_BG = FS_HEAD + """
uniform sampler2D sTex;
uniform vec2 uScale;
varying vec2 vUv;
void main() {
  vec2 b = (vUv - 0.5) * uScale + 0.5;
  gl_FragColor = vec4(texture2D(sTex, vec2(b.x, 1.0 - b.y)).rgb, 1.0);
}"""

        /** Same for a decoded background video frame. */
        private const val FS_BG_OES = """#extension GL_OES_EGL_image_external : require
precision mediump float;
uniform samplerExternalOES sTex;
uniform mat4 uTexMatrix;
uniform vec2 uScale;
varying vec2 vUv;
void main() {
  vec2 b = (vUv - 0.5) * uScale + 0.5;
  gl_FragColor = vec4(texture2D(sTex, (uTexMatrix * vec4(b, 0.0, 1.0)).xy).rgb, 1.0);
}"""

        private const val FS_COMP = FS_HEAD + """
uniform sampler2D sScene;
uniform sampler2D sBg;
uniform sampler2D sMask;
uniform sampler2D sPreviousScene;
uniform float uMotionGuard;
uniform vec3 uCrop;      // centre.xy, scale
uniform vec2 uTexel;
uniform float uSmooth;
uniform float uSharp;
uniform float uDenoise;
uniform int uBgMode;     // 0 none, 1 blur, 2 image
uniform vec2 uEdge;      // edge softness (0..1), person confidence bias
uniform sampler2D sBgBlur;
uniform vec3 uBgGain;
uniform vec4 uBgFx;      // depth, grain, seed, light wrap (packed to save uniform space)
uniform vec2 uMaskTexel;
uniform vec3 uBeautySkin; // smoothing, brightness, nose contour
float hash(vec2 q) {
  return fract(sin(dot(q, vec2(12.9898, 78.233))) * 43758.5453);
}
#ifdef BEAUTY
uniform vec4 uBeautyFace; // center.xy, width/height in frame-height units
uniform vec4 uBeautyBasis; // cos, sin, aspect, strength
uniform vec4 uBeautyEyes;
uniform vec2 uBeautyMouth;
uniform vec4 uBeautyRegions[20]; // center.xy, local radii.xy
uniform vec2 uBeautyMoves[20];
uniform vec4 uBeautyNose;  // root.xy (between the eyes), base.xy
uniform int uBeautyCount;
vec2 faceLocal(vec2 delta) {
  delta.x *= uBeautyBasis.z;
  return vec2(dot(delta, uBeautyBasis.xy), dot(delta, vec2(-uBeautyBasis.y, uBeautyBasis.x)));
}
float region(vec2 uv, vec2 center, vec2 radius) {
  vec2 p = faceLocal(uv - center) / max(radius, vec2(0.0001));
  return 1.0 - smoothstep(0.45, 1.0, dot(p, p));
}
#endif
varying vec2 vUv;
void main() {
  vec2 uv = uCrop.xy + (vUv - 0.5) * uCrop.z;
  float skinMask = 0.0;
  float contourSide = 0.0;
  float contourRidge = 0.0;
#ifdef BEAUTY
  if (uBeautyBasis.w > 0.001) {
    vec2 shift = vec2(0.0);
    for (int i = 0; i < 20; i++) {
      if (i < uBeautyCount) {
        vec4 r = uBeautyRegions[i];
        vec2 delta = uBeautyMoves[i];
        vec2 local = faceLocal(uv - r.xy - delta) / max(r.zw, vec2(0.0001));
        float falloff = max(0.0, 1.0 - dot(local, local));
        shift += delta * falloff * falloff;
      }
    }
    uv = clamp(uv - shift * uBeautyBasis.w, uTexel * 0.5, vec2(1.0) - uTexel * 0.5);
    vec2 size = uBeautyFace.zw;
    skinMask = region(uv, uBeautyFace.xy, size * vec2(0.46, 0.48));
    skinMask *= 1.0 - region(uv, uBeautyEyes.xy, size * vec2(0.17, 0.08));
    skinMask *= 1.0 - region(uv, uBeautyEyes.zw, size * vec2(0.17, 0.08));
    skinMask *= 1.0 - region(uv, uBeautyMouth, size * vec2(0.24, 0.10));
    skinMask *= uBeautyBasis.w;
    if (uBeautySkin.z > 0.0) {
      // Distance from the bridge line (root -> base), in face units.
      vec2 axis = faceLocal(uBeautyNose.zw - uBeautyNose.xy);
      vec2 rel = faceLocal(uv - uBeautyNose.xy);
      float t = dot(rel, axis) / max(dot(axis, axis), 0.000001);
      float d = length(rel - axis * t);
      float hw = uBeautyFace.z * 0.07;
      float along = smoothstep(0.0, 0.2, t) * (1.0 - smoothstep(0.8, 1.05, t));
      float strength = uBeautySkin.z * uBeautyBasis.w * along;
      float sd = (d - hw) / (hw * 0.5);
      float rd = d / (hw * 0.35);
      contourSide = exp(-sd * sd) * strength;
      contourRidge = exp(-rd * rd) * strength;
    }
  }
#endif
  vec3 c = texture2D(sScene, uv).rgb;
  vec3 sceneC = c;
  if (uSmooth > 0.0 || uSharp > 0.0 || uDenoise > 0.0 || uBeautySkin.x * skinMask > 0.0) {
    vec2 d = uTexel * 1.5;
    vec3 n = texture2D(sScene, uv + vec2(d.x, 0.0)).rgb + texture2D(sScene, uv - vec2(d.x, 0.0)).rgb
           + texture2D(sScene, uv + vec2(0.0, d.y)).rgb + texture2D(sScene, uv - vec2(0.0, d.y)).rgb;
    n *= 0.25;
    vec3 orig = c;
    if (uDenoise > 0.0) {
      float diff = length(orig - n);
      c = mix(c, n, uDenoise * 0.6 * (1.0 - smoothstep(0.04, 0.16, diff)));
    }
    if (uSmooth > 0.0 || uBeautySkin.x * skinMask > 0.0) {
      vec2 w = uTexel * 4.0;
      vec3 f = texture2D(sScene, uv + w).rgb + texture2D(sScene, uv - w).rgb
             + texture2D(sScene, uv + vec2(w.x, -w.y)).rgb + texture2D(sScene, uv + vec2(-w.x, w.y)).rgb;
      f = (f * 0.25 + n) * 0.5;
      // Rough skin-tone gate so hair, eyes and background stay crisp.
      float skin = smoothstep(0.02, 0.12, orig.r - orig.g) * smoothstep(0.0, 0.08, orig.g - orig.b + 0.04)
                 * smoothstep(0.2, 0.4, orig.r);
      float flat_ = 1.0 - smoothstep(0.05, 0.2, length(orig - f));
      float smoothing = max(uSmooth * skin, uBeautySkin.x * skinMask);
      c = mix(c, f, smoothing * 0.85 * flat_);
    }
    if (uSharp > 0.0) {
      c += (orig - n) * uSharp * 1.4;
    }
  }
  c = mix(c, pow(max(c, vec3(0.0)), vec3(0.82)), uBeautySkin.y * skinMask * 0.65);
  // Contour: shade the sides of the bridge, lift the ridge, like makeup.
  c *= 1.0 - 0.18 * contourSide;
  c += (vec3(1.0) - c) * 0.08 * contourRidge;
  if (uBgMode != 0) {
    vec2 muv = vec2(uv.x, 1.0 - uv.y);
    // Edge-aware refinement (joint bilateral upsampling): the low-resolution person mask is
    // re-fitted to colour edges in the full-resolution frame, so hair and fingers stay crisp
    // on its matching camera frame, without pulling in another body position.
    float centreMask = texture2D(sMask, muv).r;
    float m = centreMask;
    float motion = 0.0;
    float best = centreMask;
    vec3 inner = sceneC;
    // Confident interior/background pixels need no 9-tap refinement. Most of a frame
    // lies here; spend the texture fetches and exponentials only on uncertain edges.
    if (centreMask > 0.015 && centreMask < 0.985) {
      float wsum = 0.0;
      float msum = 0.0;
      for (int j = -1; j <= 1; j++) {
        for (int i = -1; i <= 1; i++) {
          vec2 o = vec2(float(i), float(j)) * uMaskTexel * 0.75;
          float mk = texture2D(sMask, muv + o).r;
          vec3 col = texture2D(sScene, uv + vec2(o.x, -o.y)).rgb;
          if (uMotionGuard > 0.5) {
            vec3 delta = abs(col - texture2D(sPreviousScene, uv + vec2(o.x, -o.y)).rgb);
            motion = max(motion, max(delta.r, max(delta.g, delta.b)));
          }
          vec3 dc = col - sceneC;
          float w = exp(-dot(dc, dc) * 45.0);
          wsum += w;
          msum += w * mk;
          if (mk > best) { best = mk; inner = col; }
        }
      }
      m = msum / max(wsum, 0.0001);
    }
    // Pixels the model is unsure about (a band of wall along arms, parts of a chair) count as
    // background: the threshold sits a little above 50 % confidence.
    float w2 = mix(0.24, 0.10, uEdge.x);
    float centre = 0.5 + uEdge.y;
    m = smoothstep(centre - w2, centre + w2, m);
    // Soft moving edges (hands/held objects) must not lose baseline coverage solely
    // because spatial refinement favours surrounding room pixels. No new object model.
    if (uMotionGuard > 0.5 && motion > 0.07) {
      m = max(m, smoothstep(centre - w2, centre + w2, centreMask));
    }


    // Background, already in scene space: lens blur, the real room's light, camera grain.
    vec3 bgSoft = texture2D(sBgBlur, uv).rgb;
    vec3 bg;
    if (uBgMode == 1) {
      bg = bgSoft;
    } else {
      bgSoft *= uBgGain;
      bg = mix(texture2D(sBg, uv).rgb * uBgGain, bgSoft, uBgFx.x);
    }
    if (uBgFx.y > 0.0) {
      vec2 q = gl_FragCoord.xy + vec2(uBgFx.z * 61.0, uBgFx.z * 37.0);
      float n = hash(q) + hash(q + 17.31) - 1.0;           // triangular, sigma ~0.41
      vec3 chroma = vec3(hash(q + 3.7), hash(q + 9.1), hash(q + 5.3)) - 0.5;
      float lum = dot(bg, vec3(0.2126, 0.7152, 0.0722));
      float amp = uBgFx.y * 2.4 * (0.65 + 0.7 * (1.0 - lum)); // sensor noise shows most in shadows
      bg += vec3(n) * amp + chroma * amp * 0.35;
    }

    // Person: pull the old wall's colour out of semi-transparent edges, then let a little of
    // the new background's light wrap around the outline, as real light would.
    vec3 fg = mix(c, inner, (1.0 - m) * 0.35 * step(0.85, best) * step(0.05, m));
    float wrap = 4.0 * m * (1.0 - m) * uBgFx.w;
    fg = mix(fg, bgSoft, wrap * 0.08);
    c = mix(bg, fg, m);
  }
  gl_FragColor = vec4(clamp(c, 0.0, 1.0), 1.0);
}"""

        private const val VS_BLIT = """
attribute vec2 aPos;
uniform vec2 uRot;
uniform vec2 uScale;
varying vec2 vUv;
void main() {
  vec2 p = aPos * 0.5 * uScale;
  p = vec2(p.x * uRot.x - p.y * uRot.y, p.x * uRot.y + p.y * uRot.x);
  vUv = p + 0.5;
  gl_Position = vec4(aPos, 0.0, 1.0);
}"""

        private const val FS_BLIT = FS_HEAD + """
uniform sampler2D sTex;
uniform vec2 uTexel;
uniform float uZebra;
uniform float uPeak;
uniform float uGrid;
uniform float uTime;
varying vec2 vUv;
const vec3 LUMA = vec3(0.2126, 0.7152, 0.0722);
void main() {
  if (vUv.x < 0.0 || vUv.x > 1.0 || vUv.y < 0.0 || vUv.y > 1.0) {
    gl_FragColor = vec4(0.0, 0.0, 0.0, 1.0);
    return;
  }
  vec3 c = texture2D(sTex, vUv).rgb;
  float l = dot(c, LUMA);
  if (uPeak > 0.5) {
    float lx = dot(texture2D(sTex, vUv + vec2(uTexel.x, 0.0)).rgb - texture2D(sTex, vUv - vec2(uTexel.x, 0.0)).rgb, LUMA);
    float ly = dot(texture2D(sTex, vUv + vec2(0.0, uTexel.y)).rgb - texture2D(sTex, vUv - vec2(0.0, uTexel.y)).rgb, LUMA);
    float e = smoothstep(0.10, 0.22, length(vec2(lx, ly)));
    c = mix(c, vec3(0.29, 0.87, 0.5), e);
  }
  if (uZebra > 0.5 && l > 0.95) {
    float s = mod(gl_FragCoord.x + gl_FragCoord.y + uTime * 40.0, 14.0);
    if (s < 7.0) c = vec3(0.05);
  }
  if (uGrid > 0.5) {
    vec2 g = abs(fract(vUv * 3.0 + 0.5) - 0.5) / 3.0;
    float edgeGuard = step(0.02, vUv.x) * step(vUv.x, 0.98) * step(0.02, vUv.y) * step(vUv.y, 0.98);
    if ((g.x < uTexel.x * 1.5 || g.y < uTexel.y * 1.5) && edgeGuard > 0.5) c = mix(c, vec3(1.0), 0.28);
  }
  gl_FragColor = vec4(c, 1.0);
}"""

    }

    private class Fbo(val w: Int, val h: Int) {
        val tex: Int
        val fbo: Int
        init {
            val ids = IntArray(1)
            GLES20.glGenTextures(1, ids, 0)
            tex = ids[0]
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, tex)
            GLES20.glTexImage2D(GLES20.GL_TEXTURE_2D, 0, GLES20.GL_RGBA, w, h, 0, GLES20.GL_RGBA, GLES20.GL_UNSIGNED_BYTE, null)
            setTexParams(GLES20.GL_TEXTURE_2D)
            GLES20.glGenFramebuffers(1, ids, 0)
            fbo = ids[0]
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, fbo)
            GLES20.glFramebufferTexture2D(GLES20.GL_FRAMEBUFFER, GLES20.GL_COLOR_ATTACHMENT0, GLES20.GL_TEXTURE_2D, tex, 0)
            val status = GLES20.glCheckFramebufferStatus(GLES20.GL_FRAMEBUFFER)
            if (status != GLES20.GL_FRAMEBUFFER_COMPLETE) Log.e(TAG, "FBO ${w}x$h incomplete: 0x${status.toString(16)}")
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, 0)
        }
        fun release() {
            GLES20.glDeleteFramebuffers(1, intArrayOf(fbo), 0)
            GLES20.glDeleteTextures(1, intArrayOf(tex), 0)
        }
    }
    private class Program(vs: String, fs: String, name: String) {
        val id: Int
        val linked: Boolean
        private val locs = HashMap<String, Int>()
        init {
            val v = compile(GLES20.GL_VERTEX_SHADER, vs, name)
            val f = compile(GLES20.GL_FRAGMENT_SHADER, fs, name)
            val p = GLES20.glCreateProgram()
            GLES20.glAttachShader(p, v)
            GLES20.glAttachShader(p, f)
            GLES20.glBindAttribLocation(p, 0, "aPos")
            GLES20.glLinkProgram(p)
            val ok = IntArray(1)
            GLES20.glGetProgramiv(p, GLES20.GL_LINK_STATUS, ok, 0)
            linked = ok[0] != 0
            if (ok[0] == 0) Log.e(TAG, "link $name failed: ${GLES20.glGetProgramInfoLog(p)}")
            GLES20.glDeleteShader(v)
            GLES20.glDeleteShader(f)
            id = p
        }
        private fun compile(type: Int, src: String, name: String): Int {
            val s = GLES20.glCreateShader(type)
            GLES20.glShaderSource(s, src)
            GLES20.glCompileShader(s)
            val ok = IntArray(1)
            GLES20.glGetShaderiv(s, GLES20.GL_COMPILE_STATUS, ok, 0)
            if (ok[0] == 0) {
                Log.e(TAG, "compile $name (${if (type == GLES20.GL_VERTEX_SHADER) "vs" else "fs"}) failed: ${GLES20.glGetShaderInfoLog(s)}")
            }
            return s
        }
        fun u(name: String): Int = locs.getOrPut(name) { GLES20.glGetUniformLocation(id, name) }
        fun use() = GLES20.glUseProgram(id)
    }
    private class Camera(val tex: Int, val st: SurfaceTexture, val surface: Surface)

    /** A looping, muted background video decoded straight into a GL texture. */
    private class BgVideo(val tex: Int, val st: SurfaceTexture, val surface: Surface, val player: MediaPlayer, val aspect: Float) {
        @Volatile var pending = false
        var ready = false
        var frameNo = 0L
        val matrix = FloatArray(16)
    }

    /** "Capture my room": averages several frames of the empty room into a clean photo. */
    private class PlateJob(val cb: (Bitmap?, String?) -> Unit, val startNs: Long) {
        var fbo: Fbo? = null
        var sums: IntArray? = null
        var buf: ByteBuffer? = null
        var frames = 0
        var lastAt = 0L
    }

    private class WindowTarget(val surface: Surface, val egl: EGLSurface, var w: Int, var h: Int, val cover: Boolean)

    /** Geometry of the last drawn frame, for mapping touches and face boxes on the UI thread. */
    private class Geom(
        val d: Int, val theta: Int, val mirror: Float, val sx: Float, val sy: Float,
        val cx: Float, val cy: Float, val cs: Float, val outW: Int, val outH: Int, val texM: FloatArray,
    )

    @Volatile var params = RenderParams()

    /** Luma histogram (64 bins, normalized) while params.wantHist is set. */
    val histogram = MutableStateFlow<FloatArray?>(null)

    /** Largest face in output-frame coordinates (normalized, origin top-left), or null. */
    val face = MutableStateFlow<RectF?>(null)
    val beautyTracking = MutableStateFlow("Off")
    @Volatile var remoteBeautyOriginalUntil = 0L
    @Volatile private var captureActive = false
    val isBeautyOriginal: Boolean get() = params.beautyOriginal || System.nanoTime() < remoteBeautyOriginalUntil

    /** Frames drawn to the preview per second, for diagnostics. */
    @Volatile var cameraFps = 0f
        private set

    private val thread = HandlerThread("csp-gl").apply { start() }
    private val handler = Handler(thread.looper)

    private var display: EGLDisplay = EGL14.EGL_NO_DISPLAY
    private var context: EGLContext = EGL14.EGL_NO_CONTEXT
    private var config: EGLConfig? = null
    private var pbuffer: EGLSurface = EGL14.EGL_NO_SURFACE
    private var glReady = false

    private lateinit var progCam: Program
    private lateinit var progBlur: Program
    private lateinit var progComp: Program
    private lateinit var progBlit: Program
    private lateinit var progBg: Program
    private lateinit var progBgOes: Program
    private lateinit var quad: FloatBuffer

    private var camera: Camera? = null
    private var srcW = 1080
    private var srcH = 1920
    private val texM = FloatArray(16)

    private var scene: Fbo? = null
    private var raw: Fbo? = null
    private var blurA: Fbo? = null
    private var blurB: Fbo? = null
    private var outFbo: Fbo? = null
    private var small: Fbo? = null
    private var smallBuf: ByteBuffer? = null

    private var maskTex = 0
    private var maskReady = false
    private var bgTex = 0
    private var bgAspect = 16f / 9f
    private var bgReady = false
    private var bgVersion = 0L
    private var bgFbo: Fbo? = null
    private var bgPrepKey = ""
    private var bgMean = floatArrayOf(0.5f, 0.5f, 0.5f)
    private val bgGain = floatArrayOf(1f, 1f, 1f)
    private var roomMean: FloatArray? = null
    private var maskW = 256
    private var maskH = 144
    private var coverage = 1f
    private var coverageAt = 0L
    private var lastSegAt = 0L
    private var segFbo: Fbo? = null
    // Frame/mask synchronisation: recent scene frames are kept so the frame a mask was computed
    // from can be composited with that mask, instead of a newer frame (which caused a trailing
    // border of real wall around a moving person).
    private val ring = arrayOfNulls<Fbo>(RING)
    private val ringFrame = LongArray(RING) { -1L }
    private var maskLagFrames = 0f
    private var delayFrames = 0
    private var statMasks = 0
    private var statSince = 0L
    private var frameIntervalMs = 33f
    private var lastFrameNs = 0L

    /** Current video delay used to line frames up with the person mask; audio is held back by the same. */
    @Volatile var videoDelayMs = 0
        private set
    private var video: BgVideo? = null
    private var plate: PlateJob? = null

    private var preview: WindowTarget? = null
    private var encoder: VideoEncoder? = null
    private var encoderEgl: EGLSurface = EGL14.EGL_NO_SURFACE
    private var lastEncodeNs = 0L

    private var frameCount = 0L
    private var fpsWindowStart = 0L
    private var fpsWindowFrames = 0
    private val startNs = System.nanoTime()

    // Auto-framing state, in scene coordinates (y up).
    private var cropX = 0.5f
    private var cropY = 0.5f
    private var cropS = 1f
    @Volatile private var faceScene: RectF? = null   // top-left origin, scene space
    @Volatile private var faceSeenAt = 0L
    @Volatile private var geom: Geom? = null
    private var beautyGeometry: BeautyGeometry? = null
    private var beautySeenAt = 0L
    private var beautyTarget: BeautyGeometry? = null
    private var lastAnalysisAt = 0L
    private var lastRenderAt = 0L
    private var beautyGeneration = 0L
    private var beautyGeometryKey = ""
    private var beautyStrength = 0f
    private var beautySupported = true
    private val beautyRegions = FloatArray(BeautyGeometry.MAX_WARPS * 4)
    private val beautyMoves = FloatArray(BeautyGeometry.MAX_WARPS * 2)

    private var snapshotCb: ((Bitmap?) -> Unit)? = null

    private val analysisCallback = object : Analyzer.Callback {
        override fun onBeauty(landmarks: BeautyLandmarks?, generation: Long) {
            handler.post {
                if (generation != beautyGeneration || !params.beauty.enabled) return@post
                if (landmarks != null && System.nanoTime() - landmarks.capturedAt > 450_000_000L) return@post
                val geometry = landmarks?.let { BeautyGeometry.from(it, params.outW.toFloat() / params.outH) }
                if (geometry != null) {
                    beautyTarget = geometry
                    beautySeenAt = landmarks!!.capturedAt
                }
            }
        }
        override fun onMask(mask: ByteArray, w: Int, h: Int, stats: RoomStats, frameTag: Long) {
            handler.post {
                if (frameTag <= lastMaskTag) return@post
                // Never upload a result for a resized/rotated/overwritten source frame.
                val sync = params.bgMode != 0 && params.outW.toLong() * params.outH <= 1920L * 1088L
                if (sync && MaskFrameSync.slot(frameTag, ringFrame) < 0) return@post
                lastMaskTag = frameTag
                // ML Kit STREAM_MODE already stabilizes sequential frames. A second temporal
                // filter here would mix two different body positions and leave trailing edges.
                uploadMask(mask, w, h)
                noteMaskLag(frameTag)
                coverage = stats.coverage
                coverageAt = System.nanoTime()
                if (stats.valid) {
                    val m = roomMean
                    val now = floatArrayOf(stats.r, stats.g, stats.b)
                    roomMean = if (m == null) now else FloatArray(3) { i -> m[i] + (now[i] - m[i]) * 0.3f }
                }
            }
        }

        override fun onFace(face: RectF?) {
            if (face != null) {
                faceScene = face
                faceSeenAt = System.nanoTime()
            } else if (System.nanoTime() - faceSeenAt > 800_000_000L) {
                faceScene = null
            }
        }

        override fun onHistogram(bins: FloatArray) {
            histogram.value = bins
        }
    }

    /** Faces, beauty landmarks and the histogram. */
    private val analyzer = Analyzer(analysisCallback)
    /**
     * Person segmentation on its own thread and its own small readback, so slow face-contour
     * detection (beauty) never holds back the mask, and vice versa.
     */
    private val segAnalyzers = listOf(Analyzer(analysisCallback))
    private val segBufs = arrayOfNulls<ByteBuffer>(1)
    private var lastBackgroundActive = false
    private var lastMaskTag = -1L

    init {
        handler.post { initGl() }
    }


    fun createCameraSurface(bufW: Int, bufH: Int, uprightW: Int, uprightH: Int, cb: (Surface) -> Unit) {
        handler.post {
            if (!glReady) return@post
            makeCurrent(pbuffer)
            val ids = IntArray(1)
            GLES20.glGenTextures(1, ids, 0)
            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, ids[0])
            setTexParams(GLES11Ext.GL_TEXTURE_EXTERNAL_OES)
            val st = SurfaceTexture(ids[0])
            st.setDefaultBufferSize(bufW, bufH)
            val cam = Camera(ids[0], st, Surface(st))
            st.setOnFrameAvailableListener({ drawFrame(cam) }, handler)
            camera = cam
            resetBeautyTracking()
            srcW = uprightW
            srcH = uprightH
            cb(cam.surface)
        }
    }
    fun releaseCameraSurface(surface: Surface) {
        handler.post {
            val cam = camera
            if (cam != null && cam.surface === surface) {
                camera = null
                releaseCamera(cam)
            } else runCatching { surface.release() }
        }
    }
    private fun releaseCamera(cam: Camera) {
        runCatching { cam.st.setOnFrameAvailableListener(null) }
        runCatching { cam.surface.release() }
        runCatching { cam.st.release() }
        if (glReady) {
            makeCurrent(pbuffer)
            GLES20.glDeleteTextures(1, intArrayOf(cam.tex), 0)
        }
    }
    fun setPreviewSurface(surface: Surface?, w: Int, h: Int, cover: Boolean) {
        val latch = CountDownLatch(1)
        handler.post {
            try {
                if (!glReady) return@post
                val cur = preview
                if (surface == null) {
                    if (cur != null) destroyWindow(cur.egl)
                    preview = null
                } else if (cur != null && cur.surface === surface) {
                    cur.w = w
                    cur.h = h
                } else {
                    if (cur != null) destroyWindow(cur.egl)
                    preview = null
                    val egl = createWindow(surface)
                    if (egl != EGL14.EGL_NO_SURFACE) {
                        preview = WindowTarget(surface, egl, w, h, cover)
                        makeCurrent(egl)
                        EGL14.eglSwapInterval(display, 0)
                    }
                }
            } finally { latch.countDown() }
        }
        if (surface == null && Thread.currentThread() !== thread) latch.await(1, TimeUnit.SECONDS)
    }
    fun clearPreviewSurface(surface: Surface) {
        val latch = CountDownLatch(1)
        handler.post {
            try {
                val cur = preview
                if (glReady && cur != null && cur.surface === surface) {
                    destroyWindow(cur.egl)
                    preview = null
                }
            } finally { latch.countDown() }
        }
        latch.await(1, TimeUnit.SECONDS)
    }
    fun setEncoder(enc: VideoEncoder?) {
        val latch = CountDownLatch(1)
        handler.post {
            try {
                if (!glReady) return@post
                if (encoderEgl != EGL14.EGL_NO_SURFACE) destroyWindow(encoderEgl)
                encoderEgl = EGL14.EGL_NO_SURFACE
                encoder = null
                val s = enc?.inputSurface
                if (enc != null && s != null) {
                    val egl = createWindow(s)
                    if (egl != EGL14.EGL_NO_SURFACE) { encoderEgl = egl; encoder = enc }
                }
            } finally { latch.countDown() }
        }
        latch.await(2, TimeUnit.SECONDS)
    }
    fun setCaptureActive(active: Boolean) {
        if (captureActive == active) return
        captureActive = active
        if (!active) handler.post {
            if (glReady) {
                makeCurrent(pbuffer)
                releaseVideo()
            }
        }
    }
    fun setBackground(bmp: Bitmap?) {
        val mean = bmp?.let { meanColor(it) }
        handler.post {
            if (!glReady) return@post
            makeCurrent(pbuffer)
            bgVersion++
            bgPrepKey = ""
            if (bmp == null) { bgReady = false; return@post }
            releaseVideo()
            bgMean = mean ?: bgMean
            if (bgTex == 0) {
                val ids = IntArray(1)
                GLES20.glGenTextures(1, ids, 0)
                bgTex = ids[0]
            }
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, bgTex)
            setTexParams(GLES20.GL_TEXTURE_2D)
            GLUtils.texImage2D(GLES20.GL_TEXTURE_2D, 0, bmp, 0)
            bgAspect = bmp.width.toFloat() / bmp.height
            bgReady = true
        }
    }
    /**
     * Looping, muted background video, or null to stop it. [aspect] is the display aspect
     * (rotation applied); [mean] the average colour of a frame, for lighting matching.
     */
    fun setBackgroundVideo(ctx: Context, uri: Uri?, aspect: Float, mean: FloatArray?) {
        val app = ctx.applicationContext
        handler.post {
            if (!glReady) return@post
            makeCurrent(pbuffer)
            releaseVideo()
            bgPrepKey = ""
            if (uri == null) return@post
            if (!captureActive) return@post
            try {
                val ids = IntArray(1)
                GLES20.glGenTextures(1, ids, 0)
                GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, ids[0])
                setTexParams(GLES11Ext.GL_TEXTURE_EXTERNAL_OES)
                val st = SurfaceTexture(ids[0])
                val surface = Surface(st)
                val player = MediaPlayer()
                val v = BgVideo(ids[0], st, surface, player, aspect)
                st.setOnFrameAvailableListener({ v.pending = true }, handler)
                player.setDataSource(app, uri)
                player.setSurface(surface)
                player.isLooping = true
                player.setVolume(0f, 0f)
                player.setOnPreparedListener { it.start() }
                player.setOnErrorListener { _, what, extra -> Log.w(TAG, "background video error $what/$extra"); true }
                player.prepareAsync()
                video = v
                bgMean = mean ?: bgMean
            } catch (e: Exception) {
                Log.w(TAG, "background video failed", e)
                releaseVideo()
            }
        }
    }

    private fun releaseVideo() {
        val v = video ?: return
        video = null
        runCatching { v.player.release() }
        runCatching { v.st.setOnFrameAvailableListener(null) }
        runCatching { v.surface.release() }
        runCatching { v.st.release() }
        GLES20.glDeleteTextures(1, intArrayOf(v.tex), 0)
    }

    /**
     * Photographs the empty room for "Capture my room": waits until nobody is in view, then
     * averages 8 frames (removing sensor noise, which is added back live so it never freezes).
     * [cb] runs on the GL thread with the photo, or null and a message.
     */
    fun captureRoom(cb: (Bitmap?, String?) -> Unit) {
        handler.post {
            plate?.let { finishPlate(it, null, "Cancelled") }
            plate = PlateJob(cb, System.nanoTime())
        }
    }

    fun cancelRoomCapture() {
        handler.post { plate?.let { finishPlate(it, null, null) } }
    }

    private fun finishPlate(job: PlateJob, bmp: Bitmap?, error: String?) {
        if (plate === job) plate = null
        job.fbo?.release()
        job.fbo = null
        job.cb(bmp, error)
    }

    private fun stepPlate(job: PlateJob, sc: Fbo, now: Long) {
        if (now - job.startNs > 20_000_000_000L) {
            finishPlate(job, null, "Someone is still in view. Step completely out of the picture and try again.")
            return
        }
        // Only frames analysed after the request, with (almost) no person in them.
        if (coverageAt < job.startNs || coverage > 0.03f || now - job.lastAt < 100_000_000L) return
        job.lastAt = now
        val scale = min(1f, 1920f / max(sc.w, sc.h))
        val w = max(2, (sc.w * scale).roundToInt())
        val h = max(2, (sc.h * scale).roundToInt())
        val fbo = job.fbo ?: Fbo(w, h).also { job.fbo = it }
        val buf = job.buf ?: ByteBuffer.allocateDirect(w * h * 4).order(ByteOrder.nativeOrder()).also { job.buf = it }
        val sums = job.sums ?: IntArray(w * h * 3).also { job.sums = it }
        bind(fbo)
        blit(sc.tex, sc.w, sc.h, 0, 1f, 1f, null, 0f)
        buf.position(0)
        GLES20.glReadPixels(0, 0, w, h, GLES20.GL_RGBA, GLES20.GL_UNSIGNED_BYTE, buf)
        for (i in 0 until w * h) {
            sums[i * 3] += buf.get(i * 4).toInt() and 0xff
            sums[i * 3 + 1] += buf.get(i * 4 + 1).toInt() and 0xff
            sums[i * 3 + 2] += buf.get(i * 4 + 2).toInt() and 0xff
        }
        job.frames++
        if (job.frames < 8) return
        val n = job.frames
        val colors = IntArray(w * h)
        for (y in 0 until h) {
            val src = (h - 1 - y) * w   // GL rows run bottom-up
            for (x in 0 until w) {
                val i = (src + x) * 3
                colors[y * w + x] = (0xff shl 24) or ((sums[i] / n) shl 16) or ((sums[i + 1] / n) shl 8) or (sums[i + 2] / n)
            }
        }
        finishPlate(job, Bitmap.createBitmap(colors, w, h, Bitmap.Config.ARGB_8888), null)
    }

    fun snapshot(cb: (Bitmap?) -> Unit) { handler.post { snapshotCb = cb } }

    fun viewToBuffer(x: Float, y: Float, vw: Int, vh: Int, cover: Boolean): PointF? {
        val g = geom ?: return null
        val s = viewScale(g, vw, vh, cover)
        val r = rot((x / vw - .5f) * s.x, (.5f - y / vh) * s.y, -g.d)
        val ox = r.x + .5f
        val oy = r.y + .5f
        if (ox < 0f || ox > 1f || oy < 0f || oy > 1f) return null
        return outToBuffer(ox, 1f - oy)
    }
    fun outToBuffer(u: Float, v: Float): PointF? {
        val g = geom ?: return null
        val sx = g.cx + (u - .5f) * g.cs
        val sy = g.cy + (1f - v - .5f) * g.cs
        val r = rot((sx - .5f) * g.mirror * g.sx, (sy - .5f) * g.sy, g.theta)
        val out = FloatArray(4)
        android.opengl.Matrix.multiplyMV(out, 0, g.texM, 0, floatArrayOf(r.x + .5f, r.y + .5f, 0f, 1f), 0)
        return PointF(out[0].coerceIn(0f, 1f), out[1].coerceIn(0f, 1f))
    }
    fun outToView(u: Float, v: Float, vw: Int, vh: Int, cover: Boolean): PointF? {
        val g = geom ?: return null
        val s = viewScale(g, vw, vh, cover)
        val r = rot(u - .5f, .5f - v, g.d)
        return PointF((r.x / s.x + .5f) * vw, (.5f - r.y / s.y) * vh)
    }
    fun release() {
        handler.post {
            camera?.let { releaseCamera(it) }
            camera = null
            analyzer.release()
            segAnalyzers.forEach { it.release() }
            if (glReady) { makeCurrent(pbuffer); releaseVideo() }
            if (glReady) {
                preview?.let { destroyWindow(it.egl) }
                if (encoderEgl != EGL14.EGL_NO_SURFACE) destroyWindow(encoderEgl)
                EGL14.eglMakeCurrent(display, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_CONTEXT)
                EGL14.eglDestroySurface(display, pbuffer)
                EGL14.eglDestroyContext(display, context)
                EGL14.eglTerminate(display)
                glReady = false
            }
            thread.quitSafely()
        }
    }
    private fun rot(x: Float, y: Float, deg: Int): PointF {
        val a = Math.toRadians(deg.toDouble())
        val c = cos(a).toFloat()
        val s = sin(a).toFloat()
        return PointF(x * c - y * s, x * s + y * c)
    }
    private fun viewScale(g: Geom, vw: Int, vh: Int, cover: Boolean) = viewScale(g.d, g.outW, g.outH, vw, vh, cover)
    private fun viewScale(d: Int, outW: Int, outH: Int, vw: Int, vh: Int, cover: Boolean): PointF {
        val odd = d % 180 != 0
        val rw = if (odd) outH else outW
        val rh = if (odd) outW else outH
        val fx = vw.toFloat() / rw
        val fy = vh.toFloat() / rh
        val s = if (cover) max(fx, fy) else min(fx, fy)
        return PointF(vw / (rw * s), vh / (rh * s))
    }
    private fun initGl() {
        try {
            display = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY)
            val ver = IntArray(2)
            if (!EGL14.eglInitialize(display, ver, 0, ver, 1)) throw RuntimeException("eglInitialize failed")
            val attr = intArrayOf(EGL14.EGL_RED_SIZE, 8, EGL14.EGL_GREEN_SIZE, 8, EGL14.EGL_BLUE_SIZE, 8,
                EGL14.EGL_ALPHA_SIZE, 8, EGL14.EGL_RENDERABLE_TYPE, EGL14.EGL_OPENGL_ES2_BIT,
                EGL14.EGL_SURFACE_TYPE, EGL14.EGL_WINDOW_BIT or EGL14.EGL_PBUFFER_BIT,
                EGL_RECORDABLE_ANDROID, 1, EGL14.EGL_NONE)
            val configs = arrayOfNulls<EGLConfig>(1)
            val num = IntArray(1)
            if (!EGL14.eglChooseConfig(display, attr, 0, configs, 0, 1, num, 0) || num[0] == 0) throw RuntimeException("no recordable EGL config")
            config = configs[0]
            context = EGL14.eglCreateContext(display, config, EGL14.EGL_NO_CONTEXT, intArrayOf(EGL14.EGL_CONTEXT_CLIENT_VERSION, 2, EGL14.EGL_NONE), 0)
            if (context == EGL14.EGL_NO_CONTEXT) throw RuntimeException("eglCreateContext failed")
            pbuffer = EGL14.eglCreatePbufferSurface(display, config, intArrayOf(EGL14.EGL_WIDTH, 1, EGL14.EGL_HEIGHT, 1, EGL14.EGL_NONE), 0)
            if (!EGL14.eglMakeCurrent(display, pbuffer, pbuffer, context)) throw RuntimeException("eglMakeCurrent failed")
            progCam = Program(VS_CAM, FS_CAM, "cam")
            progBlur = Program(VS, FS_BLUR, "blur")
            val uniformLimit = IntArray(1)
            GLES20.glGetIntegerv(GLES20.GL_MAX_FRAGMENT_UNIFORM_VECTORS, uniformLimit, 0)
            beautySupported = uniformLimit[0] >= 64
            progComp = Program(VS, (if (beautySupported) "#define BEAUTY\n" else "") + FS_COMP, "comp")
            if (beautySupported && !progComp.linked) {
                GLES20.glDeleteProgram(progComp.id)
                beautySupported = false
                progComp = Program(VS, FS_COMP, "comp-without-beauty")
            }
            progBlit = Program(VS_BLIT, FS_BLIT, "blit")
            progBg = Program(VS, FS_BG, "bg")
            progBgOes = Program(VS, FS_BG_OES, "bg-video")
            quad = ByteBuffer.allocateDirect(32).order(ByteOrder.nativeOrder()).asFloatBuffer()
            quad.put(floatArrayOf(-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f)).position(0)
            val ids = IntArray(1)
            GLES20.glGenTextures(1, ids, 0)
            maskTex = ids[0]
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, maskTex)
            setTexParams(GLES20.GL_TEXTURE_2D)
            GLES20.glDisable(GLES20.GL_DEPTH_TEST)
            GLES20.glDisable(GLES20.GL_BLEND)
            GLES20.glPixelStorei(GLES20.GL_UNPACK_ALIGNMENT, 1)
            GLES20.glPixelStorei(GLES20.GL_PACK_ALIGNMENT, 1)
            glReady = true
        } catch (e: Throwable) { Log.e(TAG, "initGl failed", e) }
    }
    private fun createWindow(surface: Surface): EGLSurface {
        if (!surface.isValid) return EGL14.EGL_NO_SURFACE
        return try {
            val s = EGL14.eglCreateWindowSurface(display, config, surface, intArrayOf(EGL14.EGL_NONE), 0)
            if (s == null || s == EGL14.EGL_NO_SURFACE) {
                Log.e(TAG, "eglCreateWindowSurface failed: 0x${EGL14.eglGetError().toString(16)}")
                EGL14.EGL_NO_SURFACE
            } else s
        } catch (e: Throwable) { Log.e(TAG, "eglCreateWindowSurface threw", e); EGL14.EGL_NO_SURFACE }
    }
    private fun destroyWindow(s: EGLSurface) { makeCurrent(pbuffer); EGL14.eglDestroySurface(display, s) }
    private fun makeCurrent(s: EGLSurface): Boolean = EGL14.eglMakeCurrent(display, s, s, context)
    private fun ensureTargets(p: RenderParams) {
        val sc = scene
        val analysisSize = if (p.beauty.enabled && beautySupported) 640 else 320
        if (sc == null || sc.w != p.outW || sc.h != p.outH || max(small?.w ?: 0, small?.h ?: 0) != analysisSize) {
            listOf(scene, raw, blurA, blurB, outFbo, small, bgFbo, segFbo).forEach { it?.release() }
            for (i in 1 until RING) { ring[i]?.release(); ring[i] = null }
            ringFrame.fill(-1L)
            bgFbo = null
            segFbo = null
            segBufs.fill(null)
            bgPrepKey = ""
            scene = Fbo(p.outW, p.outH)
            raw = null
            outFbo = Fbo(p.outW, p.outH)
            val qw = max(2, p.outW / 4)
            val qh = max(2, p.outH / 4)
            blurA = Fbo(qw, qh)
            blurB = Fbo(qw, qh)
            val sw: Int
            val sh: Int
            if (p.outW >= p.outH) {
                sw = analysisSize
                sh = max(2, (analysisSize.toFloat() * p.outH / p.outW).roundToInt())
            } else {
                sh = analysisSize
                sw = max(2, (analysisSize.toFloat() * p.outW / p.outH).roundToInt())
            }
            small = Fbo(sw, sh)
            smallBuf = ByteBuffer.allocateDirect(sw * sh * 4).order(ByteOrder.nativeOrder())
            maskReady = false
            lastMaskTag = -1L
        }
    }
    private fun bind(target: Fbo) {
        GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, target.fbo)
        GLES20.glViewport(0, 0, target.w, target.h)
    }
    private fun drawQuad() {
        GLES20.glEnableVertexAttribArray(0)
        GLES20.glVertexAttribPointer(0, 2, GLES20.GL_FLOAT, false, 0, quad)
        GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4)
    }
    private fun bindTex(unit: Int, target: Int, tex: Int) { GLES20.glActiveTexture(GLES20.GL_TEXTURE0 + unit); GLES20.glBindTexture(target, tex) }
    private fun uploadMask(mask: ByteArray, w: Int, h: Int) {
        if (!glReady) return
        makeCurrent(pbuffer)
        GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, maskTex)
        GLES20.glTexImage2D(GLES20.GL_TEXTURE_2D, 0, GLES20.GL_LUMINANCE, w, h, 0, GLES20.GL_LUMINANCE, GLES20.GL_UNSIGNED_BYTE, ByteBuffer.wrap(mask))
        maskW = w
        maskH = h
        maskReady = true
    }
    private fun camPass(cam: Camera, target: Fbo, p: RenderParams, neutral: Boolean, theta: Int, mirror: Float, sx: Float, sy: Float) {
        bind(target)
        progCam.use()
        bindTex(0, GLES11Ext.GL_TEXTURE_EXTERNAL_OES, cam.tex)
        GLES20.glUniform1i(progCam.u("sTex"), 0)
        GLES20.glUniformMatrix4fv(progCam.u("uTexMatrix"), 1, false, texM, 0)
        val a = Math.toRadians(theta.toDouble())
        GLES20.glUniform2f(progCam.u("uRot"), cos(a).toFloat(), sin(a).toFloat())
        GLES20.glUniform2f(progCam.u("uScale"), sx, sy)
        GLES20.glUniform1f(progCam.u("uMirror"), mirror)
        GLES20.glUniform1f(progCam.u("uBright"), if (neutral) 0f else p.brightness)
        GLES20.glUniform1f(progCam.u("uContrast"), if (neutral) 0f else p.contrast)
        GLES20.glUniform1f(progCam.u("uSat"), if (neutral) 0f else p.saturation)
        GLES20.glUniform1f(progCam.u("uWarm"), if (neutral) 0f else p.warmth)
        GLES20.glUniform1f(progCam.u("uLow"), if (!neutral && p.lowLight) 1f else 0f)
        GLES20.glUniform1i(progCam.u("uLook"), if (neutral) 0 else p.look)
        drawQuad()
    }
    private fun blit(tex: Int, texW: Int, texH: Int, rotDeg: Int, sx: Float, sy: Float, p: RenderParams?, timeSec: Float) {
        progBlit.use()
        bindTex(0, GLES20.GL_TEXTURE_2D, tex)
        GLES20.glUniform1i(progBlit.u("sTex"), 0)
        val a = Math.toRadians(rotDeg.toDouble())
        GLES20.glUniform2f(progBlit.u("uRot"), cos(a).toFloat(), sin(a).toFloat())
        GLES20.glUniform2f(progBlit.u("uScale"), sx, sy)
        GLES20.glUniform2f(progBlit.u("uTexel"), 1f / texW, 1f / texH)
        GLES20.glUniform1f(progBlit.u("uZebra"), if (p?.zebra == true) 1f else 0f)
        GLES20.glUniform1f(progBlit.u("uPeak"), if (p?.peaking == true) 1f else 0f)
        GLES20.glUniform1f(progBlit.u("uGrid"), if (p?.grid == true) 1f else 0f)
        GLES20.glUniform1f(progBlit.u("uTime"), timeSec)
        drawQuad()
    }
    /** A mask computed from frame [frameTag] just arrived: track how many frames late masks are. */
    private fun noteMaskLag(frameTag: Long) {
        val lag = (frameCount - frameTag).coerceIn(0L, 30L).toFloat()
        maskLagFrames += (lag - maskLagFrames) * 0.2f
        // Rendering computes the actual age of the selected frame, not an average.
        statMasks++
        val now = System.nanoTime()
        if (statSince == 0L) statSince = now
        if (now - statSince >= 5_000_000_000L) {
            Log.i(TAG, "segmentation: %.1f masks/s, lag %.1f frames, video delay %d frames (%d ms), frame %.1f ms, mask %dx%d"
                .format(statMasks * 1e9f / (now - statSince), maskLagFrames, delayFrames,
                    (delayFrames * frameIntervalMs).roundToInt(), frameIntervalMs, maskW, maskH))
            statMasks = 0
            statSince = now
        }
    }

    /** Two separable Gaussian passes at quarter resolution: [src] -> blurA -> blurB. */
    private fun blurInto(src: Int, r: Float) {
        val a = blurA ?: return
        val b = blurB ?: return
        progBlur.use()
        GLES20.glUniform1i(progBlur.u("sTex"), 0)
        var from = src
        for (i in 0 until 2) {
            bind(a)
            bindTex(0, GLES20.GL_TEXTURE_2D, from)
            GLES20.glUniform2f(progBlur.u("uDir"), r / a.w, 0f)
            drawQuad()
            bind(b)
            bindTex(0, GLES20.GL_TEXTURE_2D, a.tex)
            GLES20.glUniform2f(progBlur.u("uDir"), 0f, r / a.h)
            drawQuad()
            from = b.tex
        }
    }

    /**
     * Renders the chosen image or video frame into scene space (bgFbo) and its lens-blurred
     * copy (blurB). A still image is prepared once and cached; a video every new frame.
     */
    private fun prepareBackground(p: RenderParams, ao: Float): Boolean {
        val v = video?.takeIf { it.ready }
        if (v == null && (video != null || !bgReady)) return false
        val bf = bgFbo?.takeIf { it.w == p.outW && it.h == p.outH }
            ?: Fbo(p.outW, p.outH).also { bgFbo?.release(); bgFbo = it; bgPrepKey = "" }
        val r = 0.6f + 3.0f * p.bgDepth
        val key = (if (v != null) "v${v.frameNo}" else "i$bgVersion") + ":${p.outW}x${p.outH}:$r"
        if (key == bgPrepKey) return true
        bgPrepKey = key
        val aspect = v?.aspect ?: bgAspect
        val sx = if (aspect > ao) ao / aspect else 1f
        val sy = if (aspect > ao) 1f else aspect / ao
        bind(bf)
        if (v != null) {
            progBgOes.use()
            bindTex(0, GLES11Ext.GL_TEXTURE_EXTERNAL_OES, v.tex)
            GLES20.glUniform1i(progBgOes.u("sTex"), 0)
            GLES20.glUniformMatrix4fv(progBgOes.u("uTexMatrix"), 1, false, v.matrix, 0)
            GLES20.glUniform2f(progBgOes.u("uScale"), sx, sy)
        } else {
            progBg.use()
            bindTex(0, GLES20.GL_TEXTURE_2D, bgTex)
            GLES20.glUniform1i(progBg.u("sTex"), 0)
            GLES20.glUniform2f(progBg.u("uScale"), sx, sy)
        }
        drawQuad()
        blurInto(bf.tex, r)
        return true
    }

    private fun resetBeautyTracking() {
        beautyGeneration++
        beautyGeometry = null
        beautyTarget = null
        beautySeenAt = 0L
        beautyStrength = 0f
    }

    private fun uploadBeauty(p: RenderParams, now: Long) {
        beautyGeometry = beautyTarget?.smooth(beautyGeometry)
        val shape = beautyGeometry
        val original = isBeautyOriginal
        val ageMs = (now - beautySeenAt) / 1_000_000f
        val freshness = AnalysisPolicy.freshness(ageMs)
        val target = if (p.beauty.enabled && !original && shape != null) freshness * shape.confidence else 0f
        beautyStrength += (target - beautyStrength) * .35f
        // Original comparison and tracking resets must bypass immediately.
        if (original || !p.beauty.enabled || shape == null || freshness == 0f || shape.confidence <= .05f) beautyStrength = 0f
        beautyTracking.value = when {
            !p.beauty.enabled -> "Off"
            !beautySupported -> "Unavailable on this device's GPU"
            original -> "Original · beauty temporarily bypassed"
            shape == null || freshness == 0f -> "Looking for a face · effects bypassed"
            shape.confidence < .1f -> "Face forward to apply beauty effects"
            else -> "Face tracked · beauty applied on phone"
        }
        GLES20.glUniform3f(progComp.u("uBeautySkin"),
            p.beauty.skinSmooth / 100f, p.beauty.skinBrightness / 100f, p.beauty.noseContour / 100f)
        if (!beautySupported) return
        GLES20.glUniform4f(progComp.u("uBeautyBasis"), shape?.cos ?: 1f, shape?.sin ?: 0f,
            p.outW.toFloat() / p.outH, beautyStrength)
        GLES20.glUniform1i(progComp.u("uBeautyCount"), 0)
        if (shape == null || beautyStrength < .001f) return
        GLES20.glUniform4f(progComp.u("uBeautyFace"), shape.center.x, shape.center.y, shape.width, shape.height)
        GLES20.glUniform4f(progComp.u("uBeautyEyes"), shape.eyeA.x, shape.eyeA.y, shape.eyeB.x, shape.eyeB.y)
        GLES20.glUniform2f(progComp.u("uBeautyMouth"), shape.mouth.x, shape.mouth.y)
        GLES20.glUniform4f(progComp.u("uBeautyNose"), shape.root.x, shape.root.y, shape.nose.x, shape.nose.y)
        val warps = shape.warps(p.beauty)
        for ((i, warp) in warps.withIndex()) {
            beautyRegions[i * 4] = warp.x
            beautyRegions[i * 4 + 1] = warp.y
            beautyRegions[i * 4 + 2] = warp.rx
            beautyRegions[i * 4 + 3] = warp.ry
            beautyMoves[i * 2] = warp.dx
            beautyMoves[i * 2 + 1] = warp.dy
        }
        GLES20.glUniform1i(progComp.u("uBeautyCount"), warps.size)
        if (warps.isNotEmpty()) {
            GLES20.glUniform4fv(progComp.u("uBeautyRegions[0]"), warps.size, beautyRegions, 0)
            GLES20.glUniform2fv(progComp.u("uBeautyMoves[0]"), warps.size, beautyMoves, 0)
        }
    }

    private fun drawFrame(cam: Camera) {
        if (!captureActive || !glReady || camera !== cam) return
        try {
            makeCurrent(pbuffer)
            cam.st.updateTexImage()
            val renderNow = System.nanoTime()
            val renderLimit = if (params.thermalLimited) 15 else if (params.beauty.enabled || params.bgMode != 0) 30 else 0
            if (renderLimit > 0 && renderNow - lastRenderAt < 900_000_000L / renderLimit) return
            lastRenderAt = renderNow
            cam.st.getTransformMatrix(texM)
            val p = params
            ensureTargets(p)
            val baseScene = scene ?: return
            val out = outFbo ?: return
            val now = System.nanoTime()
            frameCount++

            // Geometry: cover-fit the upright source into the output frame.
            val theta = ((p.deviceRot + p.manualRot) % 360 + 360) % 360
            val odd = theta % 180 != 0
            val ew = if (odd) srcH else srcW
            val eh = if (odd) srcW else srcH
            val ae = ew.toFloat() / eh
            val ao = p.outW.toFloat() / p.outH
            val sx = if (ae > ao) ao / ae else 1f
            val sy = if (ae > ao) 1f else ae / ao
            // The camera service mirrors the front camera for display; undo that so the
            // stream is true-to-life unless the user asks for a mirrored image.
            val mirror = if (p.mirror != p.frontCamera) -1f else 1f
            val geometryKey = "$theta:$mirror:${p.outW}:${p.outH}:$sx:$sy:${p.beauty.enabled}"
            if (geometryKey != beautyGeometryKey) {
                beautyGeometryKey = geometryKey
                resetBeautyTracking()
                ringFrame.fill(-1L)
                maskReady = false
                lastMaskTag = -1L
            }
            if (lastBackgroundActive != (p.bgMode != 0)) {
                lastBackgroundActive = p.bgMode != 0
                ringFrame.fill(-1L)
                maskReady = false
                lastMaskTag = -1L
            }

            // With a replaced background, keep a few recent frames (up to 1080p) for mask sync.
            val sync = p.bgMode != 0 && p.outW.toLong() * p.outH <= 1920L * 1088L
            val slot = if (sync) (frameCount % RING).toInt() else 0
            val sceneFbo = if (slot == 0) baseScene else ring[slot] ?: Fbo(p.outW, p.outH).also { ring[slot] = it }
            camPass(cam, sceneFbo, p, false, theta, mirror, sx, sy)
            ringFrame[slot] = frameCount
            if (lastFrameNs != 0L) frameIntervalMs += ((now - lastFrameNs) / 1e6f - frameIntervalMs) * 0.05f
            lastFrameNs = now
            // Match the exact model input, not an estimated delay. Reusing a mask on the
            // newest camera frame produces the moving halo and broken shoulder edges.
            var compScene = sceneFbo
            val maskSlot = if (sync && maskReady) MaskFrameSync.slot(lastMaskTag, ringFrame) else -1
            if (maskSlot >= 0) compScene = if (maskSlot == 0) baseScene else ring[maskSlot] ?: sceneFbo
            val previousSlot = if (sync && maskSlot >= 0)
                MaskFrameSync.previousSlot(lastMaskTag, ringFrame) else -1
            val previousScene = if (previousSlot == 0) baseScene else ring.getOrNull(previousSlot)
            delayFrames = if (maskSlot >= 0) (frameCount - lastMaskTag).toInt() else 0
            videoDelayMs = if (maskSlot >= 0) MaskFrameSync.delayMs(frameCount, lastMaskTag, frameIntervalMs) else 0

            // Small readback for segmentation / faces / histogram.
            val wantSeg = p.bgMode != 0 || plate != null
            val wantFace = p.wantFace || p.autoFrame || (p.beauty.enabled && beautySupported)
            // Faces and the histogram stay at 10 Hz; segmentation runs faster so edges keep up.
            val slowInterval = if (wantSeg && p.thermalLimited) 400_000_000L else AnalysisPolicy.intervalNs(p.thermalLimited)
            val slowDue = now - lastAnalysisAt >= slowInterval
            // One ordered stream preserves the model's temporal state and avoids competing inference models.
            val segIdx = if (wantSeg && now - lastSegAt >= AnalysisPolicy.segmentIntervalNs(p.thermalLimited))
                segAnalyzers.indexOfFirst { it.busy.compareAndSet(false, true) } else -1
            if (segIdx >= 0) {
                lastSegAt = now
                // A modest 320px input preserves fine detail; the model returns its native mask.
                val sw = if (p.outW >= p.outH) 320 else max(2, (320f * p.outW / p.outH).roundToInt())
                val sh = if (p.outW >= p.outH) max(2, (320f * p.outH / p.outW).roundToInt()) else 320
                val sf = segFbo?.takeIf { it.w == sw && it.h == sh } ?: Fbo(sw, sh).also { segFbo?.release(); segFbo = it; segBufs.fill(null) }
                val sb = segBufs[segIdx]?.takeIf { it.capacity() == sw * sh * 4 }
                    ?: ByteBuffer.allocateDirect(sw * sh * 4).order(ByteOrder.nativeOrder()).also { segBufs[segIdx] = it }
                bind(sf)
                blit(sceneFbo.tex, sceneFbo.w, sceneFbo.h, 0, 1f, -1f, null, 0f)
                sb.position(0)
                GLES20.glReadPixels(0, 0, sw, sh, GLES20.GL_RGBA, GLES20.GL_UNSIGNED_BYTE, sb)
                segAnalyzers[segIdx].submit(sb, sw, sh, true, false, false, beautyGeneration, false, frameCount)
            }
            if (slowDue && (wantFace || p.wantHist) && analyzer.busy.compareAndSet(false, true)) {
                val doFace = wantFace
                val doHist = p.wantHist
                lastAnalysisAt = now
                val segDue = false
                val sm = small
                val buf = smallBuf
                if (sm != null && buf != null) {
                    bind(sm)
                    blit(sceneFbo.tex, sceneFbo.w, sceneFbo.h, 0, 1f, -1f, null, 0f)
                    buf.position(0)
                    GLES20.glReadPixels(0, 0, sm.w, sm.h, GLES20.GL_RGBA, GLES20.GL_UNSIGNED_BYTE, buf)
                    analyzer.submit(buf, sm.w, sm.h, segDue, doFace, doHist, beautyGeneration, p.beauty.enabled)
                } else {
                    analyzer.busy.set(false)
                }
            }
            if (sync && maskReady && maskSlot < 0) {
                // Keep the previous composite while a fresh matching pair is in flight.
                // Do not apply a stale mask to moving video or reveal the real room.
                return
            }
            if (!wantSeg) maskReady = false
            if (!wantFace) faceScene = null

            // Auto-framing: ease the crop toward the face.
            var tx = 0.5f
            var ty = 0.5f
            var ts = 1f
            val f = faceScene
            if (p.autoFrame && f != null) {
                ts = (f.height() * 3.2f).coerceIn(0.45f, 1f)
                tx = f.centerX()
                ty = (1f - f.centerY()) - 0.08f * ts
                tx = tx.coerceIn(ts / 2f, 1f - ts / 2f)
                ty = ty.coerceIn(ts / 2f, 1f - ts / 2f)
            }
            val k = 0.07f
            cropX += (tx - cropX) * k
            cropY += (ty - cropY) * k
            cropS += (ts - cropS) * k
            if (!p.autoFrame && cropS > 0.995f) {
                cropX = 0.5f
                cropY = 0.5f
                cropS = 1f
            }

            plate?.let { stepPlate(it, sceneFbo, now) }

            // Background stage. Both modes leave a soft (blurred) background in blurB.
            video?.let { v ->
                if (v.pending) {
                    v.pending = false
                    runCatching {
                        v.st.updateTexImage()
                        v.st.getTransformMatrix(v.matrix)
                        v.frameNo++
                        v.ready = true
                    }
                }
            }
            var bgMode = 0
            var bgTexId = 0
            val blurOut = blurB
            if (p.bgMode == 1 && maskReady && blurOut != null) {
                blurInto(compScene.tex, 0.6f + 2.4f * p.blurStrength)
                bgPrepKey = ""   // blurB now holds the scene, not the prepared background
                bgMode = 1
                bgTexId = compScene.tex
            } else if (p.bgMode == 2 && maskReady && blurOut != null && prepareBackground(p, ao)) {
                bgMode = 2
                bgTexId = bgFbo!!.tex
                val target = roomMean?.let { MaskFilter.matchGain(it, bgMean, p.bgMatch) }
                for (i in 0 until 3) bgGain[i] += ((target?.get(i) ?: 1f) - bgGain[i]) * 0.06f
            }

            // Composite into the output frame.
            bind(out)
            progComp.use()
            bindTex(0, GLES20.GL_TEXTURE_2D, compScene.tex)
            bindTex(1, GLES20.GL_TEXTURE_2D, if (bgMode != 0) bgTexId else compScene.tex)
            bindTex(2, GLES20.GL_TEXTURE_2D, maskTex)
            bindTex(3, GLES20.GL_TEXTURE_2D, if (bgMode != 0 && blurOut != null) blurOut.tex else compScene.tex)
            bindTex(4, GLES20.GL_TEXTURE_2D, previousScene?.tex ?: compScene.tex)
            GLES20.glUniform1i(progComp.u("sScene"), 0)
            GLES20.glUniform1i(progComp.u("sBg"), 1)
            GLES20.glUniform1i(progComp.u("sMask"), 2)
            GLES20.glUniform1i(progComp.u("sBgBlur"), 3)
            GLES20.glUniform1i(progComp.u("sPreviousScene"), 4)
            GLES20.glUniform1f(progComp.u("uMotionGuard"), if (bgMode != 0 && previousScene != null) 1f else 0f)
            GLES20.glUniform2f(progComp.u("uMaskTexel"), 1f / maskW, 1f / maskH)
            if (bgMode == 2) GLES20.glUniform3f(progComp.u("uBgGain"), bgGain[0], bgGain[1], bgGain[2])
            else GLES20.glUniform3f(progComp.u("uBgGain"), 1f, 1f, 1f)
            GLES20.glUniform4f(progComp.u("uBgFx"), (p.bgDepth * 1.5f).coerceIn(0f, 1f),
                if (bgMode != 0) MaskFilter.grainSigma(p.sensorIso, p.bgGrain) else 0f,
                (frameCount * 0.618034f) % 1f, p.bgWrap)
            GLES20.glUniform3f(progComp.u("uCrop"), cropX, cropY, cropS)
            GLES20.glUniform2f(progComp.u("uTexel"), 1f / out.w, 1f / out.h)
            GLES20.glUniform1f(progComp.u("uSmooth"), p.smooth)
            uploadBeauty(p, now)
            GLES20.glUniform1f(progComp.u("uSharp"), p.sharpness)
            GLES20.glUniform1f(progComp.u("uDenoise"), if (p.lowLight) 1f else 0f)
            GLES20.glUniform1i(progComp.u("uBgMode"), bgMode)
            GLES20.glUniform2f(progComp.u("uEdge"), p.edge, PERSON_BIAS)
            drawQuad()

            // Snapshot of the processed frame.
            snapshotCb?.let { cb ->
                snapshotCb = null
                val bmp = runCatching {
                    val buf = ByteBuffer.allocateDirect(out.w * out.h * 4).order(ByteOrder.nativeOrder())
                    GLES20.glReadPixels(0, 0, out.w, out.h, GLES20.GL_RGBA, GLES20.GL_UNSIGNED_BYTE, buf)
                    buf.position(0)
                    val b = Bitmap.createBitmap(out.w, out.h, Bitmap.Config.ARGB_8888)
                    b.copyPixelsFromBuffer(buf)
                    val m = android.graphics.Matrix().apply { preScale(1f, -1f) }
                    Bitmap.createBitmap(b, 0, 0, out.w, out.h, m, false).also { if (it !== b) b.recycle() }
                }.getOrNull()
                cb(bmp)
            }

            // "Compare": the preview shows the unprocessed image while held.
            var previewTex = out.tex
            if (p.compare) {
                val r = raw ?: Fbo(p.outW, p.outH).also { raw = it }
                camPass(cam, r, p, true, theta, mirror, sx, sy)
                previewTex = r.tex
            }
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, 0)

            // Encoder.
            val enc = encoder
            if (enc != null && encoderEgl != EGL14.EGL_NO_SURFACE && !p.encodePaused) {
                val minGap = if (p.encodeFpsCap > 0) (1_000_000_000L / p.encodeFpsCap) * 9 / 10 else 0L
                if (now - lastEncodeNs >= minGap) {
                    lastEncodeNs = now
                    if (makeCurrent(encoderEgl)) {
                        GLES20.glViewport(0, 0, enc.width, enc.height)
                        blit(out.tex, out.w, out.h, 0, 1f, 1f, null, 0f)
                        EGLExt.eglPresentationTimeANDROID(display, encoderEgl, now)
                        enc.noteFrame(now)
                        if (!EGL14.eglSwapBuffers(display, encoderEgl)) {
                            Log.w(TAG, "encoder swap failed: 0x${EGL14.eglGetError().toString(16)}")
                        }
                    }
                }
            }

            // Preview.
            val pv = preview
            if (pv != null && pv.w > 0 && pv.h > 0) {
                if (makeCurrent(pv.egl)) {
                    GLES20.glViewport(0, 0, pv.w, pv.h)
                    val s = viewScale(p.deviceRot, p.outW, p.outH, pv.w, pv.h, pv.cover)
                    blit(previewTex, out.w, out.h, -p.deviceRot, s.x, s.y, p, (now - startNs) / 1e9f)
                    if (!EGL14.eglSwapBuffers(display, pv.egl)) {
                        Log.w(TAG, "preview swap failed: 0x${EGL14.eglGetError().toString(16)}")
                    }
                }
            }

            // Publish geometry + face box for the UI.
            geom = Geom(p.deviceRot, theta, mirror, sx, sy, cropX, cropY, cropS, p.outW, p.outH, texM.copyOf())
            val fs = faceScene
            face.value = if (fs != null && p.wantFace) {
                // scene (top-left origin) -> output frame (top-left origin), undoing the crop
                fun mx(x: Float) = (x - cropX) / cropS + 0.5f
                fun my(yTop: Float) = 1f - (((1f - yTop) - cropY) / cropS + 0.5f)
                RectF(mx(fs.left), my(fs.top), mx(fs.right), my(fs.bottom))
            } else null

            fpsWindowFrames++
            if (now - fpsWindowStart >= 1_000_000_000L) {
                cameraFps = fpsWindowFrames * 1e9f / (now - fpsWindowStart)
                fpsWindowStart = now
                fpsWindowFrames = 0
            }
        } catch (e: Throwable) {
            Log.e(TAG, "drawFrame failed", e)
        }
    }

}
private fun setTexParams(target: Int) {
    GLES20.glTexParameteri(target, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR)
    GLES20.glTexParameteri(target, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR)
    GLES20.glTexParameteri(target, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_CLAMP_TO_EDGE)
    GLES20.glTexParameteri(target, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE)
}

/** Average colour (0..1) of a bitmap, for matching a background to the room's light. */
private fun meanColor(bmp: Bitmap): FloatArray {
    val small = Bitmap.createScaledBitmap(bmp, 16, 16, true)
    val px = IntArray(256)
    small.getPixels(px, 0, 16, 0, 0, 16, 16)
    if (small !== bmp) small.recycle()
    var r = 0L
    var g = 0L
    var b = 0L
    for (c in px) {
        r += (c shr 16) and 0xff
        g += (c shr 8) and 0xff
        b += c and 0xff
    }
    return floatArrayOf(r / (256f * 255f), g / (256f * 255f), b / (256f * 255f))
}
