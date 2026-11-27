// Accès OpenGL sans bibliothèque : repris de FacePhoto (launcher\Gl.cs, projet de l'utilisateur, 17/11), pour le
// moteur 3D du Builder natif (plan work\diag\natif\PLAN.md, étape 2). Code d'origine gardé tel quel.
#nullable disable
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GenerateFollower.Native;

// Gl.cs (17/11) - our own OpenGL access: no library, only opengl32.dll (Windows) and the graphics driver.
// GL 1.1 functions are exported by opengl32.dll; everything newer (buffers, shaders, framebuffers) is asked to the
// driver with wglGetProcAddress once a context exists (GL.Load). Shaders are GLSL 1.20 so that any driver since
// ~2008 runs them; framebuffers (anti-aliasing) are optional.

static class GL
{
    const string L = "opengl32.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct PFD
    {
        public ushort nSize, nVersion;
        public uint dwFlags;
        public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift,
                    cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
                    cDepthBits, cStencilBits, cAuxBuffers;
        public sbyte iLayerType;
        public byte bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    // ---- Windows side ----
    [DllImport(L)] public static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport(L)] public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr ctx);
    [DllImport(L)] public static extern bool wglDeleteContext(IntPtr ctx);
    [DllImport(L, CharSet = CharSet.Ansi)] static extern IntPtr wglGetProcAddress(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetModuleHandle(string n);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string n);
    [DllImport("kernel32")] static extern IntPtr LoadLibrary(string n);
    [DllImport("gdi32")] public static extern int ChoosePixelFormat(IntPtr hdc, ref PFD p);
    [DllImport("gdi32")] public static extern bool SetPixelFormat(IntPtr hdc, int f, ref PFD p);
    [DllImport("gdi32")] public static extern bool SwapBuffers(IntPtr hdc);
    [DllImport("user32")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);

    // ---- GL 1.1 ----
    [DllImport(L)] public static extern void glClearColor(float r, float g, float b, float a);
    [DllImport(L)] public static extern void glClear(uint m);
    [DllImport(L)] public static extern void glViewport(int x, int y, int w, int h);
    [DllImport(L)] public static extern void glEnable(uint c);
    [DllImport(L)] public static extern void glDisable(uint c);
    [DllImport(L)] public static extern void glDepthMask(bool f);
    [DllImport(L)] public static extern void glDepthFunc(uint f);
    [DllImport(L)] public static extern void glBlendFunc(uint s, uint d);
    [DllImport(L)] public static extern void glCullFace(uint m);
    [DllImport(L)] public static extern void glPolygonMode(uint face, uint mode);
    [DllImport(L)] public static extern void glDrawElements(uint mode, int count, uint type, IntPtr idx);
    [DllImport(L)] public static extern void glGenTextures(int n, uint[] t);
    [DllImport(L)] public static extern void glDeleteTextures(int n, uint[] t);
    [DllImport(L)] public static extern void glBindTexture(uint target, uint t);
    [DllImport(L)] public static extern void glTexImage2D(uint target, int level, int internalformat, int w, int h,
                                                         int border, uint format, uint type, IntPtr data);
    [DllImport(L)] public static extern void glTexParameteri(uint target, uint p, int v);
    [DllImport(L)] public static extern void glTexParameterf(uint target, uint p, float v);
    [DllImport(L)] public static extern void glPixelStorei(uint p, int v);
    [DllImport(L)] public static extern void glReadPixels(int x, int y, int w, int h, uint format, uint type, IntPtr data);
    [DllImport(L)] static extern IntPtr glGetString(uint n);
    [DllImport(L)] public static extern uint glGetError();
    [DllImport(L)] public static extern void glLineWidth(float w);
    [DllImport(L)] public static extern void glFinish();

    public static string GetString(uint n)
    {
        var p = glGetString(n);
        return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p);
    }

    // ---- constants ----
    public const uint COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x100, DEPTH_TEST = 0x0B71, BLEND = 0x0BE2,
        CULL_FACE = 0x0B44, BACK = 0x0405, FRONT = 0x0404, FRONT_AND_BACK = 0x0408, LINE = 0x1B01, FILL = 0x1B02,
        TRIANGLES = 4, UNSIGNED_INT = 0x1405, FLOAT = 0x1406, UNSIGNED_BYTE = 0x1401, SRC_ALPHA = 0x0302,
        ONE_MINUS_SRC_ALPHA = 0x0303, LEQUAL = 0x0203, TEXTURE_2D = 0x0DE1, RGBA = 0x1908, RGBA8 = 0x8058,
        BGRA = 0x80E1, TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800, TEXTURE_WRAP_S = 0x2802,
        TEXTURE_WRAP_T = 0x2803, LINEAR = 0x2601, LINEAR_MIPMAP_LINEAR = 0x2703, CLAMP_TO_EDGE = 0x812F,
        REPEAT = 0x2901, UNPACK_ALIGNMENT = 0x0CF5, PACK_ALIGNMENT = 0x0D05, TEXTURE0 = 0x84C0,
        ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, STATIC_DRAW = 0x88E4,
        VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82,
        FRAMEBUFFER = 0x8D40, READ_FRAMEBUFFER = 0x8CA8, DRAW_FRAMEBUFFER = 0x8CA9, RENDERBUFFER = 0x8D41,
        COLOR_ATTACHMENT0 = 0x8CE0, DEPTH_ATTACHMENT = 0x8D00, DEPTH_COMPONENT24 = 0x81A6,
        FRAMEBUFFER_COMPLETE = 0x8CD5, NEAREST = 0x2600, MAX_SAMPLES = 0x8D57, MAX_TEXTURE_SIZE = 0x0D33, VENDOR = 0x1F00, RENDERER = 0x1F01,
        VERSION = 0x1F02, TEXTURE_MAX_ANISOTROPY = 0x84FE, MULTISAMPLE = 0x809D;

    // ---- GL 2.0+ (driver entry points) ----
    public delegate void GenDel(int n, uint[] ids);
    public delegate void BindDel(uint target, uint id);
    public delegate void BufferDataDel(uint target, IntPtr size, IntPtr data, uint usage);
    public delegate void EnableAttrDel(uint index);
    public delegate void AttrPtrDel(uint index, int size, uint type, bool norm, int stride, IntPtr ptr);
    public delegate uint CreateShaderDel(uint type);
    public delegate void ShaderSourceDel(uint sh, int count, string[] src, IntPtr len);
    public delegate void UIntDel(uint id);
    public delegate void GetIvDel(uint id, uint pname, out int v);
    public delegate void InfoLogDel(uint id, int size, out int len, StringBuilder log);
    public delegate uint CreateProgramDel();
    public delegate void AttachDel(uint prog, uint sh);
    public delegate void BindAttrDel(uint prog, uint index, string name);
    public delegate int UniformLocDel(uint prog, string name);
    public delegate void UniformMatDel(int loc, int count, bool transpose, float[] v);
    public delegate void Uniform3fDel(int loc, float a, float b, float c);
    public delegate void Uniform1fDel(int loc, float a);
    public delegate void Uniform1iDel(int loc, int a);
    public delegate void RbStorageMsDel(uint target, int samples, uint fmt, int w, int h);
    public delegate void FbRbDel(uint target, uint attach, uint rbTarget, uint rb);
    public delegate void BlitDel(int sx0, int sy0, int sx1, int sy1, int dx0, int dy0, int dx1, int dy1, uint mask, uint filter);
    public delegate uint CheckFbDel(uint target);
    public delegate void GenMipDel(uint target);
    public delegate void GetIntDel(uint pname, out int v);

    public static GenDel glGenBuffers, glDeleteBuffers, glGenFramebuffers, glDeleteFramebuffers, glGenRenderbuffers,
                         glDeleteRenderbuffers;
    public static BindDel glBindBuffer, glBindFramebuffer, glBindRenderbuffer;
    public static BufferDataDel glBufferData;
    public static EnableAttrDel glEnableVertexAttribArray, glDisableVertexAttribArray;
    public static AttrPtrDel glVertexAttribPointer;
    public static CreateShaderDel glCreateShader;
    public static ShaderSourceDel glShaderSource;
    public static UIntDel glCompileShader, glLinkProgram, glUseProgram, glDeleteShader, glActiveTexture;
    public static GetIvDel glGetShaderiv, glGetProgramiv;
    public static InfoLogDel glGetShaderInfoLog, glGetProgramInfoLog;
    public static CreateProgramDel glCreateProgram;
    public static AttachDel glAttachShader;
    public static BindAttrDel glBindAttribLocation;
    public static UniformLocDel glGetUniformLocation;
    public static UniformMatDel glUniformMatrix4fv;
    public static Uniform3fDel glUniform3f;
    public static Uniform1fDel glUniform1f;
    public static Uniform1iDel glUniform1i;
    public static RbStorageMsDel glRenderbufferStorageMultisample;
    public static FbRbDel glFramebufferRenderbuffer;
    public static BlitDel glBlitFramebuffer;
    public static CheckFbDel glCheckFramebufferStatus;
    public static GenMipDel glGenerateMipmap;
    [DllImport(L)] public static extern void glGetIntegerv(uint pname, out int v);

    public static bool HasFramebuffers;

    static IntPtr Proc(string n)
    {
        var p = wglGetProcAddress(n);
        long v = p.ToInt64();
        if (v >= -1 && v <= 3) p = GetProcAddress(GetModuleHandle("opengl32.dll"), n);
        return p;
    }

    static T Get<T>(string n, bool required) where T : class
    {
        var p = Proc(n);
        if (p == IntPtr.Zero)
        {
            if (required) throw new Exception(GenerateFollower.L.T("la carte graphique ne propose pas {0} (OpenGL 2.0 nécessaire)", n));
            return null;
        }
        return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
    }

    public static void Load()
    {
        glGenBuffers = Get<GenDel>("glGenBuffers", true);
        glDeleteBuffers = Get<GenDel>("glDeleteBuffers", true);
        glBindBuffer = Get<BindDel>("glBindBuffer", true);
        glBufferData = Get<BufferDataDel>("glBufferData", true);
        glEnableVertexAttribArray = Get<EnableAttrDel>("glEnableVertexAttribArray", true);
        glDisableVertexAttribArray = Get<EnableAttrDel>("glDisableVertexAttribArray", true);
        glVertexAttribPointer = Get<AttrPtrDel>("glVertexAttribPointer", true);
        glCreateShader = Get<CreateShaderDel>("glCreateShader", true);
        glShaderSource = Get<ShaderSourceDel>("glShaderSource", true);
        glCompileShader = Get<UIntDel>("glCompileShader", true);
        glDeleteShader = Get<UIntDel>("glDeleteShader", true);
        glGetShaderiv = Get<GetIvDel>("glGetShaderiv", true);
        glGetShaderInfoLog = Get<InfoLogDel>("glGetShaderInfoLog", true);
        glCreateProgram = Get<CreateProgramDel>("glCreateProgram", true);
        glAttachShader = Get<AttachDel>("glAttachShader", true);
        glBindAttribLocation = Get<BindAttrDel>("glBindAttribLocation", true);
        glLinkProgram = Get<UIntDel>("glLinkProgram", true);
        glGetProgramiv = Get<GetIvDel>("glGetProgramiv", true);
        glGetProgramInfoLog = Get<InfoLogDel>("glGetProgramInfoLog", true);
        glUseProgram = Get<UIntDel>("glUseProgram", true);
        glGetUniformLocation = Get<UniformLocDel>("glGetUniformLocation", true);
        glUniformMatrix4fv = Get<UniformMatDel>("glUniformMatrix4fv", true);
        glUniform3f = Get<Uniform3fDel>("glUniform3f", true);
        glUniform1f = Get<Uniform1fDel>("glUniform1f", true);
        glUniform1i = Get<Uniform1iDel>("glUniform1i", true);
        glActiveTexture = Get<UIntDel>("glActiveTexture", true);
        // optional: anti-aliasing through a multisampled framebuffer, mipmaps
        glGenerateMipmap = Get<GenMipDel>("glGenerateMipmap", false);
        glGenFramebuffers = Get<GenDel>("glGenFramebuffers", false);
        glDeleteFramebuffers = Get<GenDel>("glDeleteFramebuffers", false);
        glBindFramebuffer = Get<BindDel>("glBindFramebuffer", false);
        glGenRenderbuffers = Get<GenDel>("glGenRenderbuffers", false);
        glDeleteRenderbuffers = Get<GenDel>("glDeleteRenderbuffers", false);
        glBindRenderbuffer = Get<BindDel>("glBindRenderbuffer", false);
        glRenderbufferStorageMultisample = Get<RbStorageMsDel>("glRenderbufferStorageMultisample", false);
        glFramebufferRenderbuffer = Get<FbRbDel>("glFramebufferRenderbuffer", false);
        glBlitFramebuffer = Get<BlitDel>("glBlitFramebuffer", false);
        glCheckFramebufferStatus = Get<CheckFbDel>("glCheckFramebufferStatus", false);
        HasFramebuffers = glGenFramebuffers != null && glBindFramebuffer != null && glGenRenderbuffers != null &&
                          glRenderbufferStorageMultisample != null && glFramebufferRenderbuffer != null &&
                          glBlitFramebuffer != null && glCheckFramebufferStatus != null
                          // essai : comme une carte sans tampon multi-échantillons
                          && Environment.GetEnvironmentVariable("GF_GL_SANSFBO") != "1";
    }

    // ---- small matrix helpers (column-major, as OpenGL) ----
    public static float[] Perspective(double fovDeg, double aspect, double near, double far)
    {
        double f = 1.0 / Math.Tan(fovDeg * Math.PI / 360.0);
        var m = new float[16];
        m[0] = (float)(f / aspect); m[5] = (float)f;
        m[10] = (float)((far + near) / (near - far)); m[11] = -1;
        m[14] = (float)(2 * far * near / (near - far));
        return m;
    }

    public static float[] LookAt(double[] eye, double[] at, double[] up)
    {
        double[] f = Norm(Sub(at, eye)), s = Norm(Cross(f, up)), u = Cross(s, f);
        var m = new float[16];
        m[0] = (float)s[0]; m[4] = (float)s[1]; m[8] = (float)s[2];
        m[1] = (float)u[0]; m[5] = (float)u[1]; m[9] = (float)u[2];
        m[2] = (float)-f[0]; m[6] = (float)-f[1]; m[10] = (float)-f[2];
        m[12] = (float)-(s[0] * eye[0] + s[1] * eye[1] + s[2] * eye[2]);
        m[13] = (float)-(u[0] * eye[0] + u[1] * eye[1] + u[2] * eye[2]);
        m[14] = (float)(f[0] * eye[0] + f[1] * eye[1] + f[2] * eye[2]);
        m[15] = 1;
        return m;
    }

    public static float[] Mul(float[] a, float[] b)
    {
        var r = new float[16];
        for (int c = 0; c < 4; c++)
            for (int rr = 0; rr < 4; rr++)
            {
                float v = 0;
                for (int k = 0; k < 4; k++) v += a[k * 4 + rr] * b[c * 4 + k];
                r[c * 4 + rr] = v;
            }
        return r;
    }

    public static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    public static double[] Cross(double[] a, double[] b)
    {
        return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    }
    public static double[] Norm(double[] a)
    {
        double l = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        return l < 1e-12 ? new[] { 0.0, 0.0, 0.0 } : new[] { a[0] / l, a[1] / l, a[2] / l };
    }
}
