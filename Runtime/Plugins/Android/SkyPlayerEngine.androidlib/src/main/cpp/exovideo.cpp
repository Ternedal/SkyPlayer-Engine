// Native GL bridge for the ExoPlayer video path.
//
// Runs on Unity's GLES3 render thread (driven by GL.IssuePluginEvent):
//   1. create a GL_TEXTURE_EXTERNAL_OES texture
//   2. hand it to Java, which wraps it in a SurfaceTexture + Surface for ExoPlayer
//   3. each frame: SurfaceTexture.updateTexImage() pulls the decoded frame into the OES texture
//   4. blit OES -> a regular GL_TEXTURE_2D via an FBO + shader (samplerExternalOES)
//   5. Unity samples that 2D texture directly (Texture2D.CreateExternalTexture). No CPU copy.

#include <jni.h>
#include <android/log.h>
#include <GLES3/gl3.h>
#include <GLES2/gl2ext.h>   // GL_TEXTURE_EXTERNAL_OES

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  "ExoVideoNative", __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "ExoVideoNative", __VA_ARGS__)

static JavaVM*  g_vm        = nullptr;
static jobject  g_plugin    = nullptr;     // global ref to ExoVideoPlugin
static jmethodID m_initSurf = nullptr;     // boolean nativeInitSurface(int)
static jmethodID m_updateTex= nullptr;     // boolean nativeUpdateTex(float[])
static jmethodID m_getW     = nullptr;     // int getVideoWidth()
static jmethodID m_getH     = nullptr;     // int getVideoHeight()
static jfloatArray g_mtxArr = nullptr;     // global float[16] scratch

static GLuint g_oesTex = 0, g_outTex = 0, g_fbo = 0;
static GLuint g_prog = 0, g_vao = 0, g_vbo = 0;
static GLint  g_uMtx = -1, g_uTex = -1;
static int    g_vw = 0, g_vh = 0;
static bool   g_glInited = false, g_surfReq = false, g_texReady = false;

static const int MAX_W = 4096;   // cap target texture width (Quest 2 RAM safety)

extern "C" JNIEXPORT jint JNI_OnLoad(JavaVM* vm, void*) {
    g_vm = vm;
    LOGI("JNI_OnLoad: cached JavaVM");
    return JNI_VERSION_1_6;
}

static JNIEnv* getEnv() {
    if (!g_vm) return nullptr;
    JNIEnv* env = nullptr;
    jint r = g_vm->GetEnv((void**)&env, JNI_VERSION_1_6);
    if (r == JNI_EDETACHED) {
        if (g_vm->AttachCurrentThread(&env, nullptr) != 0) { LOGE("AttachCurrentThread failed"); return nullptr; }
    }
    return env;
}

static const char* VS =
    "#version 300 es\n"
    "in vec2 aPos; in vec2 aTex; uniform mat4 uSTMatrix; out vec2 vTex;\n"
    "void main(){ vTex = (uSTMatrix * vec4(aTex, 0.0, 1.0)).xy; gl_Position = vec4(aPos, 0.0, 1.0); }";
static const char* FS =
    "#version 300 es\n"
    "#extension GL_OES_EGL_image_external_essl3 : require\n"
    "precision mediump float; in vec2 vTex; uniform samplerExternalOES sTex; out vec4 o;\n"
    "void main(){ o = texture(sTex, vTex); }";

static GLuint compile(GLenum type, const char* src) {
    GLuint s = glCreateShader(type);
    glShaderSource(s, 1, &src, nullptr);
    glCompileShader(s);
    GLint ok = 0; glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (!ok) { char log[1024]; glGetShaderInfoLog(s, 1024, nullptr, log); LOGE("shader compile failed: %s", log); }
    return s;
}

static void initGL() {
    glGenTextures(1, &g_oesTex);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, g_oesTex);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, 0);

    GLuint vs = compile(GL_VERTEX_SHADER, VS), fs = compile(GL_FRAGMENT_SHADER, FS);
    g_prog = glCreateProgram();
    glAttachShader(g_prog, vs); glAttachShader(g_prog, fs);
    glBindAttribLocation(g_prog, 0, "aPos");
    glBindAttribLocation(g_prog, 1, "aTex");
    glLinkProgram(g_prog);
    GLint ok = 0; glGetProgramiv(g_prog, GL_LINK_STATUS, &ok);
    if (!ok) { char log[1024]; glGetProgramInfoLog(g_prog, 1024, nullptr, log); LOGE("program link failed: %s", log); }
    g_uMtx = glGetUniformLocation(g_prog, "uSTMatrix");
    g_uTex = glGetUniformLocation(g_prog, "sTex");
    glDeleteShader(vs); glDeleteShader(fs);

    const float verts[] = {
        -1.f,-1.f, 0.f,0.f,
         1.f,-1.f, 1.f,0.f,
        -1.f, 1.f, 0.f,1.f,
         1.f, 1.f, 1.f,1.f,
    };
    glGenVertexArrays(1, &g_vao);
    glBindVertexArray(g_vao);
    glGenBuffers(1, &g_vbo);
    glBindBuffer(GL_ARRAY_BUFFER, g_vbo);
    glBufferData(GL_ARRAY_BUFFER, sizeof(verts), verts, GL_STATIC_DRAW);
    glEnableVertexAttribArray(0);
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 16, (void*)0);
    glEnableVertexAttribArray(1);
    glVertexAttribPointer(1, 2, GL_FLOAT, GL_FALSE, 16, (void*)8);
    glBindVertexArray(0);
    glBindBuffer(GL_ARRAY_BUFFER, 0);

    g_glInited = true;
    LOGI("initGL done oesTex=%u prog=%u", g_oesTex, g_prog);
}

static void ensureOutTex(int w, int h) {
    if (w > MAX_W) { h = (int)((long)h * MAX_W / w); w = MAX_W; }
    glGenTextures(1, &g_outTex);
    glBindTexture(GL_TEXTURE_2D, g_outTex);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, nullptr);
    glBindTexture(GL_TEXTURE_2D, 0);

    glGenFramebuffers(1, &g_fbo);
    glBindFramebuffer(GL_FRAMEBUFFER, g_fbo);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, g_outTex, 0);
    GLenum st = glCheckFramebufferStatus(GL_FRAMEBUFFER);
    if (st != GL_FRAMEBUFFER_COMPLETE) LOGE("FBO incomplete 0x%x", st);
    glBindFramebuffer(GL_FRAMEBUFFER, 0);

    g_vw = w; g_vh = h; g_texReady = true;
    LOGI("ensureOutTex %dx%d outTex=%u fbo=%u", w, h, g_outTex, g_fbo);
}

static void render() {
    JNIEnv* env = getEnv();
    if (!env || !g_plugin) return;

    if (!g_glInited) initGL();

    if (!g_surfReq) {
        jboolean ok = env->CallBooleanMethod(g_plugin, m_initSurf, (jint)g_oesTex);
        g_surfReq = true;
        LOGI("nativeInitSurface(oes=%u) -> %d", g_oesTex, (int)ok);
        return;
    }

    jboolean got = env->CallBooleanMethod(g_plugin, m_updateTex, g_mtxArr);
    if (!got) return;

    if (!g_texReady) {
        int w = env->CallIntMethod(g_plugin, m_getW);
        int h = env->CallIntMethod(g_plugin, m_getH);
        if (w <= 0 || h <= 0) return;
        ensureOutTex(w, h);
    }

    float mtx[16];
    env->GetFloatArrayRegion(g_mtxArr, 0, 16, mtx);

    // Save the Unity GL state we are about to change.
    GLint pFBO;    glGetIntegerv(GL_FRAMEBUFFER_BINDING, &pFBO);
    GLint pVP[4];  glGetIntegerv(GL_VIEWPORT, pVP);
    GLint pProg;   glGetIntegerv(GL_CURRENT_PROGRAM, &pProg);
    GLint pActive; glGetIntegerv(GL_ACTIVE_TEXTURE, &pActive);
    GLboolean pDepth   = glIsEnabled(GL_DEPTH_TEST);
    GLboolean pBlend   = glIsEnabled(GL_BLEND);
    GLboolean pCull    = glIsEnabled(GL_CULL_FACE);
    GLboolean pScissor = glIsEnabled(GL_SCISSOR_TEST);

    glBindFramebuffer(GL_FRAMEBUFFER, g_fbo);
    glViewport(0, 0, g_vw, g_vh);
    glDisable(GL_DEPTH_TEST); glDisable(GL_BLEND);
    glDisable(GL_CULL_FACE);  glDisable(GL_SCISSOR_TEST);
    glUseProgram(g_prog);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, g_oesTex);
    glUniform1i(g_uTex, 0);
    glUniformMatrix4fv(g_uMtx, 1, GL_FALSE, mtx);
    glBindVertexArray(g_vao);
    glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
    glBindVertexArray(0);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, 0);

    // Restore Unity GL state.
    glBindFramebuffer(GL_FRAMEBUFFER, pFBO);
    glViewport(pVP[0], pVP[1], pVP[2], pVP[3]);
    glUseProgram(pProg);
    glActiveTexture(pActive);
    if (pDepth)   glEnable(GL_DEPTH_TEST);
    if (pBlend)   glEnable(GL_BLEND);
    if (pCull)    glEnable(GL_CULL_FACE);
    if (pScissor) glEnable(GL_SCISSOR_TEST);
}

extern "C" {

typedef void (*RenderEventFunc)(int);
static void OnRenderEvent(int eventId) { render(); }

__attribute__((visibility("default"))) __attribute__((used))
RenderEventFunc GetRenderEventFunc() { return OnRenderEvent; }

__attribute__((visibility("default"))) __attribute__((used))
int ExoNativePing() { LOGI("ExoNativePing called"); return 4242; }

// Receives the ExoVideoPlugin jobject (raw) from C#; resolves the method ids.
__attribute__((visibility("default"))) __attribute__((used))
int ExoNativeSetPlugin(void* pluginJObject) {
    JNIEnv* env = getEnv();
    if (!env) { LOGE("SetPlugin: no JNIEnv"); return -1; }
    jobject local = (jobject)pluginJObject;
    g_plugin = env->NewGlobalRef(local);
    jclass cls = env->GetObjectClass(g_plugin);
    m_initSurf  = env->GetMethodID(cls, "nativeInitSurface", "(I)Z");
    m_updateTex = env->GetMethodID(cls, "nativeUpdateTex",   "([F)Z");
    m_getW      = env->GetMethodID(cls, "getVideoWidth",     "()I");
    m_getH      = env->GetMethodID(cls, "getVideoHeight",    "()I");
    jfloatArray a = env->NewFloatArray(16);
    g_mtxArr = (jfloatArray)env->NewGlobalRef(a);
    int ok = (m_initSurf && m_updateTex && m_getW && m_getH) ? 1 : 0;
    LOGI("ExoNativeSetPlugin ok=%d", ok);
    return ok;
}

__attribute__((visibility("default"))) __attribute__((used))
int ExoNativeGetOutTex() { return (int)g_outTex; }
__attribute__((visibility("default"))) __attribute__((used))
int ExoNativeGetW() { return g_vw; }
__attribute__((visibility("default"))) __attribute__((used))
int ExoNativeGetH() { return g_vh; }

}
