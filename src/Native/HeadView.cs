#nullable disable
#pragma warning disable WFO1000   // contrôle dessiné à la main : pas de concepteur WinForms
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace GenerateFollower.Native;

/// <summary>
/// Moteur 3D du Builder natif (plan work\diag\natif\PLAN.md, étape 2) : la tête du follower, sans navigateur. Repris du
/// moteur de FacePhoto (Viewer3D.cs, 17/11 : OpenGL 2.1 / GLSL 1.20, éclairage hémisphère + clé + contre-jour, Lambert + GGX,
/// MSAA 4x, orbite amortie) et adapté aux têtes du Builder :
/// - formes et textures données par l'API locale, comme pour la page : /api/mesh/&lt;id&gt; (tête affichée : export RaceMenu,
///   tête d'un autre preset ou tête générée), /api/texture?path= (diffuse), /api/tint/&lt;id&gt;?path= (teinte du visage) ;
/// - mêmes règles que la page : formes d'aide cachées (sans diffuse, 0alfa.dds), teinte FaceGen sur la plus grande forme de
///   visage (b² + 2·t·b·(1−b) en sRGB, comme le jeu), cheveux teintés par la couleur du preset (shader Hair Tint), transparence
///   du NIF (mélange avec ses facteurs, test alpha), toutes les formes double face, texture introuvable en violet ;
/// - repère Skyrim (Z en haut, visage vers +Y) → (−x, z, y) comme FacePhoto.
/// Finitions (18/11, « Finir la tête ») : relief (normal maps : espace objet « _msn » comme la page, .xzy de Skyrim ; sinon
/// espace tangent, vert tel quel), skin choisi sur le visage (/api/skins, rôle head_d), maquillages et tatouages du visage
/// (/api/faceoverlays : texture × couleur, opacité = alpha, sur la forme du visage), référence côte à côte (export RaceMenu
/// associé, décalé d'une largeur et demie de tête à droite) et écart du visage (/api/compare).
/// Corps entier : étape 3.
/// </summary>
sealed class HeadView : Control
{
    sealed class Part
    {
        public string Name, Diffuse, Tint, Normal;
        public bool Msn, Ref;
        public float Opacity = 1;
        public Part OverlayOf;          // maquillage / tatouage : dessiné sur la géométrie de cette forme
        public int OverlayIndex;
        public float[] Vtx;
        public uint[] Idx;
        public float[] Color = { 1, 1, 1 };
        public float AlphaTest;
        public bool Blend, Face, Eye, Missing, Body, Hair;
        public float[] Tone;            // peau du corps (shader Skin Tint) : couleur de peau du PNJ (QNAM), même calcul que la teinte du visage
        public uint Src = GL.SRC_ALPHA, Dst = GL.ONE_MINUS_SRC_ALPHA;
        public double[] Mid = new double[3], Lo, Hi;
    }

    sealed class Img { public byte[] Px; public int W, H; }

    sealed class Mesh
    {
        public Part P;
        public uint Vbo, Ibo, Tex, TintTex, NmapTex;
        public int Count;
        public bool OwnsBuffers = true;
    }

    static readonly float[] Bg = { 42 / 255f, 44 / 255f, 49 / 255f };
    // facteurs de mélange de NiAlphaProperty (0 à 10) → OpenGL
    string shownId;
    int shownLoad, retintId;

    /// <summary>Couleur de peau changée (poste Corps et peau) : seules la teinte du visage (/api/tint avec ?peau=) et la peau
    /// du corps (Skin Tint) sont refaites, sans recharger formes et textures. Faux si la vue ne montre pas ce preset tel
    /// quel (chargement en cours) : il faut alors un Load.</summary>
    public bool Retint(HttpClient http, string id, string presetSkin)
    {
        if (meshes.Count == 0 || shownId != id || shownLoad != loadId) return false;
        int myLoad = loadId, myRetint = ++retintId;
        var faces = meshes.Where(m => m.TintTex != 0 && m.P.Tint is { } t && t.StartsWith("api/tint/")).ToList();
        var bodies = meshes.Where(m => m.P.Tone != null).ToList();
        Task.Run(async () =>
        {
            JsonNode opt = null, skins = null;
            try { opt = await GetJson(http, "api/options/" + id); } catch (Exception) { }
            try { skins = await GetJson(http, "api/skins/" + id); } catch (Exception) { }
            bool erin = skins?["erin"] is JsonValue ev && ev.TryGetValue<bool>(out var eb) && eb;
            var chosen = erin ? null : opt?["peauChoix"] is JsonValue pc && pc.TryGetValue<string>(out var pcs) && pcs.Length == 7 ? pcs[1..] : null;
            var urls = faces.ToDictionary(m => m, m => Regex.Replace(m.P.Tint, "&peau=[0-9A-Fa-f]*", "") + (chosen != null ? "&peau=" + chosen : ""));
            var imgs = await Fetch(http, urls.Values);
            var tone = erin ? "FFFFFF" : chosen ?? (presetSkin is { Length: 7 } ? presetSkin[1..] : null);
            float[] rgb = tone != null && int.TryParse(tone, System.Globalization.NumberStyles.HexNumber, null, out var t)
                ? new[] { ((t >> 16) & 255) / 255f, ((t >> 8) & 255) / 255f, (t & 255) / 255f } : null;
            if (IsDisposed) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (myLoad != loadId || myRetint != retintId || ctx == IntPtr.Zero) return;
                    GL.wglMakeCurrent(hdc, ctx);
                    foreach (var (m, u) in urls)
                        if (imgs.GetValueOrDefault(u) is { } img) { Fill(m.TintTex, img); m.P.Tint = u; }
                    if (rgb != null) foreach (var m in bodies) m.P.Tone = rgb;
                    dirty = true; Invalidate();
                });
            }
            catch (InvalidOperationException) { }
        });
        return true;
    }

    static readonly uint[] BlendGl ={ 1, 0, 0x0300, 0x0301, 0x0306, 0x0307, 0x0302, 0x0303, 0x0304, 0x0305, 0x0308 };

    IntPtr hdc, ctx;
    uint prog;
    int uMvp, uTex, uHasTex, uTint, uHasTint, uColor, uAlphaTest, uRough, uKey, uKeyDir, uRimDir, uCam, uNmap, uNmapMode, uOpacity, uOffset, uTone;
    [DllImport("opengl32.dll")] static extern void glPolygonOffset(float factor, float units);
    const uint POLYGON_OFFSET_FILL = 0x8037;
    double[] refOffset = { 0, 0, 0 };
    bool keepViewNext;
    readonly List<Mesh> meshes = new();
    readonly System.Windows.Forms.Timer tick = new() { Interval = 15 };
    int loadId;
    public string GlInfo = "", GlError, Status = "";
    public event Action<string> StatusChanged;

    uint fbo, rbColor, rbDepth;
    int fbW, fbH, samples;

    double[] target = { 0, 0, 0 };
    double theta, phi = Math.PI / 2, dist = 50, radius = 12;
    double dTheta, dPhi;
    double[] panPending = { 0, 0, 0 };
    double[] center = { 0, 0, 0 };
    Point last;
    MouseButtons dragging = MouseButtons.None;
    bool dirty = true, hairOn = true, reliefOn = true, overlaysOn = true, hideHair;   // hideHair : casque ou capuche de la tenue

    public HeadView()
    {
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        BackColor = Color.FromArgb(42, 44, 49);
        tick.Tick += (s, e) => Step();
        tick.Start();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20; return cp; }   // CS_OWNDC
    }

    /// <summary>Carte graphique et OpenGL de la vue 3D (app.log, rapport de diagnostic), null tant que la fenêtre n'est pas ouverte.</summary>
    public static string Report;

    /// <summary>Réglage « Aperçu 3D ». false : pas de contexte OpenGL, aucune géométrie ni texture demandée, un résumé en texte à la
    /// place. Changé à chaud : la fenêtre de la vue est recréée (un contexte détruit laisse sinon une surface où GDI ne dessine plus).</summary>
    public bool Use3D
    {
        get => use3D;
        set
        {
            if (use3D == value) return;
            use3D = value;
            loadId++;            // un chargement en cours est abandonné
            summary = "";
            shownId = null;
            if (IsHandleCreated) RecreateHandle();
            Invalidate();
        }
    }
    bool use3D = true;
    /// <summary>La vue peut dessiner (contexte OpenGL créé).</summary>
    public bool Active => ctx != IntPtr.Zero;
    /// <summary>Nom, race et sexe du follower, pour le résumé sans 3D.</summary>
    public string Caption = "";
    string summary = "";

    public bool HairOn { get => hairOn; set { hairOn = value; dirty = true; Invalidate(); } }
    public bool ReliefOn { get => reliefOn; set { reliefOn = value; dirty = true; Invalidate(); } }
    public bool OverlaysOn { get => overlaysOn; set { overlaysOn = value; dirty = true; Invalidate(); } }
    public bool HasHead => meshes.Count > 0;
    /// <summary>Noms (minuscules) des formes de la coiffure du preset, retirées quand une coiffure ajoutée est choisie ;
    /// donnés par la fiche (headparts de type Hair et leurs extras) avant chaque chargement.</summary>
    public static HashSet<string> AddedHairReplaces = [];

    void Say(string s) { Status = s; StatusChanged?.Invoke(s); }

    // ---------------- contexte ----------------
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        tick.Start();
        GlError = null;
        if (!use3D) { GlInfo = ""; Report = "off (setting)"; Console.WriteLine("OpenGL : " + Report); return; }
        try
        {
            hdc = GL.GetDC(Handle);
            if (Environment.GetEnvironmentVariable("GF_GL_ECHEC") == "1") throw new Exception(L.T("contexte OpenGL refusé"));   // essai : carte sans OpenGL
            var pfd = new GL.PFD
            {
                nSize = (ushort)Marshal.SizeOf(typeof(GL.PFD)), nVersion = 1, dwFlags = 0x4 | 0x20 | 0x1,
                iPixelType = 0, cColorBits = 32, cAlphaBits = 8, cDepthBits = 24, cStencilBits = 8,
            };
            int f = GL.ChoosePixelFormat(hdc, ref pfd);
            if (f == 0 || !GL.SetPixelFormat(hdc, f, ref pfd)) throw new Exception(L.T("format de pixel refusé"));
            ctx = GL.wglCreateContext(hdc);
            if (ctx == IntPtr.Zero || !GL.wglMakeCurrent(hdc, ctx)) throw new Exception(L.T("contexte OpenGL refusé"));
            GL.Load();
            GlInfo = GL.GetString(GL.RENDERER) + " | OpenGL " + GL.GetString(GL.VERSION);
            BuildProgram();
            if (GL.HasFramebuffers) { GL.glGetIntegerv(GL.MAX_SAMPLES, out int max); samples = Math.Min(4, Math.Max(0, max)); }
            GL.glGetIntegerv(GL.MAX_TEXTURE_SIZE, out int maxTex);
            GL.glGetError();
            Report = $"{GL.GetString(GL.VENDOR)} | {GlInfo} | texture max {maxTex} | MSAA x{samples}";
        }
        catch (Exception ex) { GlError = ex.Message; ctx = IntPtr.Zero; Report = "ERROR " + ex.Message; }
        // dans app.log et le rapport de diagnostic : essais sur d'autres cartes (Intel, AMD)
        Console.WriteLine("OpenGL : " + Report);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (ctx != IntPtr.Zero)
        {
            GL.wglMakeCurrent(hdc, ctx);
            Clear(); FreeFbo();
            GL.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            GL.wglDeleteContext(ctx);
            ctx = IntPtr.Zero;
        }
        if (hdc != IntPtr.Zero) { GL.ReleaseDC(Handle, hdc); hdc = IntPtr.Zero; }
        tick.Stop();
        base.OnHandleDestroyed(e);
    }

    const string VS = @"#version 120
attribute vec3 aPos; attribute vec3 aNor; attribute vec2 aUv; attribute vec4 aTan;
uniform mat4 uMvp; uniform vec3 uOffset;
varying vec3 vN; varying vec2 vUv; varying vec3 vW; varying vec4 vT;
void main() { vN = aNor; vUv = aUv; vT = aTan; vW = aPos + uOffset; gl_Position = uMvp * vec4(aPos + uOffset, 1.0); }";

    // teinte FaceGen comme le jeu (et la page) : en sRGB, b² + 2·t·b·(1−b)
    const string FS = @"#version 120
uniform sampler2D uTex; uniform int uHasTex; uniform sampler2D uTint; uniform int uHasTint;
uniform vec3 uColor; uniform float uAlphaTest; uniform float uRough; uniform float uOpacity;
uniform sampler2D uNmap; uniform int uNmapMode; uniform vec3 uTone;
uniform float uKey; uniform vec3 uKeyDir; uniform vec3 uRimDir; uniform vec3 uCam;
varying vec3 vN; varying vec2 vUv; varying vec3 vW; varying vec4 vT;
const float PI = 3.14159265;
vec3 lin(vec3 c) { return pow(max(c, vec3(0.0)), vec3(2.2)); }
vec3 ggx(vec3 n, vec3 v, vec3 l, float a) {
  vec3 h = normalize(l + v);
  float nl = max(dot(n, l), 0.0), nv = max(dot(n, v), 1e-4), nh = max(dot(n, h), 0.0), vh = max(dot(v, h), 0.0);
  float a2 = a * a;
  float d = a2 / (PI * pow(nh * nh * (a2 - 1.0) + 1.0, 2.0));
  float g = 0.5 / max(nl * sqrt(nv * nv * (1.0 - a2) + a2) + nv * sqrt(nl * nl * (1.0 - a2) + a2), 1e-5);
  float f = 0.04 + 0.96 * pow(1.0 - vh, 5.0);
  return vec3(f * g * d);
}
void main() {
  vec4 t = uHasTex == 1 ? texture2D(uTex, vUv) : vec4(1.0);
  if (t.a < uAlphaTest) discard;
  vec3 b = t.rgb;
  if (uHasTint == 1) { vec3 tt = texture2D(uTint, vUv).rgb; b = b * b + 2.0 * tt * b * (1.0 - b); }
  else if (uHasTint == 2) b = b * b + 2.0 * uTone * b * (1.0 - b);
  vec3 alb = lin(b) * lin(uColor);
  vec3 n = normalize(vN);
  if (uNmapMode == 2) {
    // espace objet de Skyrim (« _msn ») : normale de la géométrie = texture .xzy, tournée par la pose du sommet
    // (quaternion dans aTan, identité sans pose), puis (x, y, z) -> (-x, z, y)
    vec3 t2 = texture2D(uNmap, vUv).xyz * 2.0 - 1.0;
    vec3 ns = t2.xzy;
    ns += 2.0 * cross(vT.xyz, cross(vT.xyz, ns) + vT.w * ns);
    n = normalize(vec3(-ns.x, ns.z, ns.y));
    if (!gl_FrontFacing) n = -n;
  } else if (uNmapMode == 1) {
    // espace tangent, repère tangent par pixel d'après les dérivées de l'écran (comme three.js, getTangentFrame) : les
    // tangentes par sommet étaient fausses sur les îlots d'UV en miroir (taches claires sur une cotte de mailles, 18/11)
    if (!gl_FrontFacing) n = -n;
    vec3 tn = texture2D(uNmap, vUv).xyz * 2.0 - 1.0;
    vec3 q0 = dFdx(vW), q1 = dFdy(vW);
    vec2 st0 = dFdx(vUv), st1 = dFdy(vUv);
    vec3 q1p = cross(q1, n), q0p = cross(n, q0);
    vec3 T = q1p * st0.x + q0p * st1.x, Bt = q1p * st0.y + q0p * st1.y;
    float det = max(dot(T, T), dot(Bt, Bt));
    float sc = det == 0.0 ? 0.0 : inversesqrt(det);
    n = normalize(T * (tn.x * sc) + Bt * (tn.y * sc) + n * tn.z);
  } else if (!gl_FrontFacing) n = -n;
  vec3 v = normalize(uCam - vW);
  float a = uRough * uRough;
  vec3 hemi = mix(lin(vec3(0.251, 0.251, 0.282)), vec3(1.0), 0.5 * n.y + 0.5) * 0.7;
  vec3 l1 = normalize(uKeyDir), l2 = normalize(uRimDir);
  vec3 rim = lin(vec3(0.749, 0.831, 1.0)) * 0.5;
  float d1 = max(dot(n, l1), 0.0), d2 = max(dot(n, l2), 0.0);
  vec3 col = alb / PI * (hemi + vec3(uKey) * d1 + rim * d2) + ggx(n, v, l1, a) * uKey * d1 + ggx(n, v, l2, a) * rim * d2;
  gl_FragColor = vec4(pow(col, vec3(1.0 / 2.2)), t.a * uOpacity);
}";

    uint Shader(uint type, string src)
    {
        uint s = GL.glCreateShader(type);
        GL.glShaderSource(s, 1, new[] { src }, IntPtr.Zero);
        GL.glCompileShader(s);
        GL.glGetShaderiv(s, GL.COMPILE_STATUS, out int ok);
        if (ok == 0) { var sb = new System.Text.StringBuilder(4096); GL.glGetShaderInfoLog(s, sb.Capacity, out _, sb); throw new Exception("shader : " + sb); }
        return s;
    }

    void BuildProgram()
    {
        uint vs = Shader(GL.VERTEX_SHADER, VS), fs = Shader(GL.FRAGMENT_SHADER, FS);
        prog = GL.glCreateProgram();
        GL.glAttachShader(prog, vs); GL.glAttachShader(prog, fs);
        GL.glBindAttribLocation(prog, 0, "aPos"); GL.glBindAttribLocation(prog, 1, "aNor"); GL.glBindAttribLocation(prog, 2, "aUv"); GL.glBindAttribLocation(prog, 3, "aTan");
        GL.glLinkProgram(prog);
        GL.glGetProgramiv(prog, GL.LINK_STATUS, out int ok);
        if (ok == 0) { var sb = new System.Text.StringBuilder(4096); GL.glGetProgramInfoLog(prog, sb.Capacity, out _, sb); throw new Exception("programme : " + sb); }
        GL.glDeleteShader(vs); GL.glDeleteShader(fs);
        int U(string n) => GL.glGetUniformLocation(prog, n);
        uMvp = U("uMvp"); uTex = U("uTex"); uHasTex = U("uHasTex"); uTint = U("uTint"); uHasTint = U("uHasTint"); uColor = U("uColor");
        uAlphaTest = U("uAlphaTest"); uRough = U("uRough"); uKey = U("uKey"); uKeyDir = U("uKeyDir"); uRimDir = U("uRimDir"); uCam = U("uCam");
        uNmap = U("uNmap"); uNmapMode = U("uNmapMode"); uOpacity = U("uOpacity"); uOffset = U("uOffset"); uTone = U("uTone");
    }

    // ---------------- chargement ----------------
    static readonly Regex Helper = new(@"(^|\\)0alfa\.dds$", RegexOptions.IgnoreCase);

    /// <summary>Corps entier (étape 3) : réponse de /api/body, overlays du corps, taille en jeu, couleur de peau, options.</summary>
    sealed class BodyInfo
    {
        public JsonObject D;
        public JsonArray Ovl = [];
        public double Scale = 1;
        public float[] Tone;
        public bool Pose;
    }

    /// <summary>Tête du preset id (formes et textures lues sur l'API locale hors du fil de l'interface). presetSkin : couleur de
    /// peau du preset (« #RRGGBB » de la fiche) ; body : corps entier (avec tenue et pose naturelle si demandées).</summary>
    public void Load(HttpClient http, string id, int? hairColor, string presetSkin, string refPath = null, bool body = false, bool outfit = true, bool pose = true, bool keepView = false)
    {
        int myId = ++loadId;
        if (!use3D || GlError != null) { LoadSummary(http, id, presetSkin, myId); return; }
        // keepView : même preset rechargé après un choix (tenue…) : la caméra reste où l'utilisateur l'a mise
        keepViewNext = keepView && meshes.Count > 0;
        Say(body ? L.T("Chargement du corps…") : L.T("Chargement de la tête…"));
        Task.Run(async () =>
        {
            List<Part> parts = null; Dictionary<string, Img> imgs = null; string error = null;
            string compare = null, bodyText = null;
            bool hide = false;
            try
            {
                // choix de la fiche : couleur de peau choisie, taille ; skins : skin choisi, race Erin (peau blanche)
                JsonNode opt = null, skins = null;
                try { opt = await GetJson(http, "api/options/" + id); } catch (Exception) { }
                try { skins = await GetJson(http, "api/skins/" + id); } catch (Exception) { }
                bool erin = skins?["erin"] is JsonValue ev && ev.TryGetValue<bool>(out var eb) && eb;
                var chosen = erin ? null : opt?["peauChoix"] is JsonValue pc && pc.TryGetValue<string>(out var pcs) && pcs.Length == 7 ? pcs[1..] : null;
                BodyInfo bi = null;
                if (body)
                {
                    bi = new BodyInfo { Pose = pose };
                    string preset = null;
                    try
                    {
                        var bp = await GetJson(http, "api/bodypresets/" + id);
                        if (bp?["choix"] is JsonValue cv && cv.TryGetValue<string>(out var cs) && cs.Length > 0 && !(bp["choixInconnu"] is JsonValue ci && (bool)ci)) preset = cs;
                    }
                    catch (Exception) { }
                    var q = new List<string>();
                    if (preset != null) q.Add("preset=" + Uri.EscapeDataString(preset));
                    if (outfit) q.Add("tenue=1");
                    bi.D = (await GetJson(http, "api/body/" + id + (q.Count > 0 ? "?" + string.Join("&", q) : ""))).AsObject();
                    try { bi.Ovl = (await GetJson(http, "api/bodyoverlays/" + id)).AsArray(); } catch (Exception) { }
                    // taille en jeu = taille de la fiche × hauteur de la race pour ce sexe (comme la page)
                    double taille = opt?["choix"]?["taille"] is JsonValue tv && tv.TryGetValue<double>(out var td) ? td : 1;
                    double race = bi.D["hauteurRace"] is JsonValue hv && hv.TryGetValue<double>(out var hd) ? hd : 1;
                    bi.Scale = taille * race;
                    // couleur de peau du PNJ comme au build : blanche pour la race Erin, sinon la couleur choisie, sinon celle du preset
                    var tone = erin ? "FFFFFF" : chosen ?? (presetSkin is { Length: 7 } ? presetSkin[1..] : null);
                    if (tone != null && int.TryParse(tone, System.Globalization.NumberStyles.HexNumber, null, out var t))
                        bi.Tone = new[] { ((t >> 16) & 255) / 255f, ((t >> 8) & 255) / 255f, (t & 255) / 255f };
                    hide = outfit && bi.D["cacheCheveux"] is JsonValue hc && (bool)hc;
                    bodyText = BodyText(bi.D, taille, race);
                }
                (parts, imgs) = await Read(http, id, hairColor, chosen, skins, bi);
                if (bi != null)
                {
                    var (bp2, bimg) = await ReadBody(http, bi, skins);
                    parts.AddRange(bp2);
                    foreach (var (k, v) in bimg) imgs.TryAdd(k, v);
                }
                else if (refPath != null)
                {
                    // référence illisible (tête du PNJ absente, export déplacé) : la tête du follower reste, l'erreur est dite
                    try
                    {
                        var (rp, ri) = await ReadRef(http, refPath);
                        parts.AddRange(rp);
                        foreach (var (k, v) in ri) imgs.TryAdd(k, v);
                        compare = await Compare(http, id, refPath);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        compare = L.T("Référence (à droite) : {0}", string.Join(@"\", refPath.Split('\\').TakeLast(2))) + " · " + ex.Message;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.Message; }
            if (myId != loadId || IsDisposed) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (myId != loadId) return;
                    if (parts != null)
                    {
                        hideHair = hide;
                        // diagnostic : GF_NATIF_DUMP=<fichier> -> formes chargées (nom, rôle, transparence, boîte)
                        if (Environment.GetEnvironmentVariable("GF_NATIF_DUMP") is { Length: > 0 } dump)
                            File.WriteAllLines(dump, parts.Select(p => string.Join(" | ", p.Name, p.Body ? "corps" : p.Ref ? "ref" : "tete", p.OverlayOf != null ? "overlay" : "",
                                p.Blend ? "blend" : "", "test " + p.AlphaTest, p.Missing ? "MANQUE" : "", p.Diffuse, p.Normal, p.Msn ? "msn" : "",
                                p.Tone != null ? "teinte" : "", p.Lo == null ? "" : string.Join(";", p.Lo.Select(x => Math.Round(x))) + " / " + string.Join(";", p.Hi.Select(x => Math.Round(x))))));
                        Upload(parts, imgs);
                        shownId = id; shownLoad = myId;
                        int miss = parts.Count(p => p.Missing && !p.Ref);
                        Say((bodyText ?? compare ?? L.T("{0} formes · glisser pour tourner, molette pour zoomer", parts.Count(p => !p.Ref && p.OverlayOf == null)))
                            + (miss > 0 ? " · " + L.T("{0} texture(s) introuvable(s), en violet (comme en jeu)", miss) : ""));
                    }
                    else { Clear(); Say(error == null ? L.T("Pas de tête à afficher.") : body ? L.T("Corps indisponible : {0}", error) : error); }
                    Invalidate();
                });
            }
            catch (InvalidOperationException) { }
        });
    }

    /// <summary>Sans vue 3D : résumé du follower (skin, couleur de peau, taille, coiffure ajoutée), lu dans deux petites réponses de
    /// l'API ; aucune géométrie, aucune texture.</summary>
    void LoadSummary(HttpClient http, string id, string presetSkin, int myId)
    {
        Say(use3D ? L.T("Vue 3D indisponible : {0}", GlError) : L.T("Aperçu 3D désactivé (Réglages)."));
        Task.Run(async () =>
        {
            JsonNode opt = null, skins = null;
            try { opt = await GetJson(http, "api/options/" + id); } catch (Exception) { }
            try { skins = await GetJson(http, "api/skins/" + id); } catch (Exception) { }
            var lines = new List<string>();
            var choice = skins?["choix"]?.ToString();
            var skin = (skins?["skins"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => x["id"]?.ToString() == choice);
            lines.Add(L.T("Peau (skin)") + " : " + (skin != null ? S(skin, "nom") : L.T("Peau du jeu (par défaut)")));
            var tone = opt?["peauChoix"]?.ToString() is { Length: 7 } t ? t : presetSkin;
            if (tone is { Length: > 0 }) lines.Add(L.T("Couleur de peau") + " : " + tone.ToUpperInvariant());
            if (opt?["choix"]?["taille"] is JsonValue tv && tv.TryGetValue<double>(out var size)) lines.Add(L.T("Taille : {0}", Math.Round(size, 2).ToString(System.Globalization.CultureInfo.CurrentUICulture)));
            if (opt?["choix"]?["coiffure"] != null) lines.Add(L.T("Coiffure ajoutée"));
            if (myId != loadId || IsDisposed) return;
            try { BeginInvoke(() => { if (myId != loadId) return; summary = string.Join("\n", lines); shownId = id; Invalidate(); }); }
            catch (InvalidOperationException) { }
        });
    }

    /// <summary>Résumé en texte au centre de la vue (aperçu désactivé ou OpenGL indisponible).</summary>
    void PaintSummary(Graphics g)
    {
        g.Clear(BackColor);
        int w = Math.Max(60, ClientSize.Width - 60);
        var parts = new List<(string Text, Font Font, Color Color, int Gap)>();
        if (Caption.Length > 0)
        {
            var c = Caption.Split('\n');
            parts.Add((c[0], Theme.Title, Theme.Text, 4));
            if (c.Length > 1) parts.Add((c[1], Theme.Small, Theme.Muted, 18));
        }
        if (summary.Length > 0) parts.Add((summary, Theme.Body, Theme.Text, 24));
        if (GlError != null)
        {
            parts.Add((L.T("Vue 3D indisponible : {0}", GlError), Theme.Body, Theme.Err, 6));
            parts.Add((L.T("Tu peux désactiver l'aperçu 3D dans les Réglages ; le reste du Builder marche pareil."), Theme.Small, Theme.Muted, 0));
        }
        else parts.Add((L.T("Aperçu 3D désactivé (Réglages)."), Theme.Small, Theme.Muted, 0));
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        int H((string Text, Font Font, Color Color, int Gap) p) => TextRenderer.MeasureText(g, p.Text, p.Font, new Size(w, int.MaxValue), flags).Height;
        int y = Math.Max(10, (ClientSize.Height - parts.Sum(p => H(p) + p.Gap)) / 2);
        foreach (var p in parts)
        {
            int h = H(p);
            TextRenderer.DrawText(g, p.Text, p.Font, new Rectangle((ClientSize.Width - w) / 2, y, w, h), p.Color, flags);
            y += h + p.Gap;
        }
    }

    /// <summary>Ligne d'état du corps entier (mêmes textes que la page).</summary>
    static string BodyText(JsonObject d, double taille, double race)
    {
        string Str(string k) => d[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        double Num(string k) => d[k] is JsonValue v && v.TryGetValue<double>(out var x) ? x : 0;
        var pieces = (d["pieces"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var missingRace = pieces.Where(p => !(p["formes"] is JsonValue f && f.TryGetValue<int>(out var n) && n > 0)).Select(p => S(p, "nom")).ToList();
        var ci = System.Globalization.CultureInfo.CurrentUICulture;
        return L.T("Corps : {0} · poids {1}", Str("corps"), Math.Round(Num("poids"))) + " · "
            + (Str("preset") is { } pr ? L.T("preset « {0} » ({1} curseurs sur {2} trouvés dans le corps)", pr, Num("curseursAppliques"), Num("curseurs"))
                : L.T("sans preset (OBody au hasard en jeu)"))
            + (Str("tenue") is { } te ? " · " + L.T("tenue : {0}, {1} pièce(s)", te, pieces.Count) : "")
            + (missingRace.Count > 0 ? " · " + L.T("sans modèle pour la race (invisibles en jeu) : {0}", string.Join(", ", missingRace)) : "")
            + " · " + L.T("taille en jeu {0} (fiche {1} × race {2})", Math.Round(taille * race, 2).ToString(ci), Math.Round(taille, 2).ToString(ci), Math.Round(race, 2).ToString(ci));
    }

    static async Task<JsonNode> GetJson(HttpClient http, string url)
    {
        var res = await http.GetAsync(url);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new GfException(JsonNode.Parse(text)?["erreur"]?.GetValue<string>() ?? $"HTTP {(int)res.StatusCode}");
        return JsonNode.Parse(text);
    }

    /// <summary>Transparence, mélange et yeux d'une forme (NiAlphaProperty du NIF), comme la page.</summary>
    static void Material(Part p, JsonObject s)
    {
        p.Eye = (int?)s["shader"] == 16 || (Regex.IsMatch(p.Name, "eye", RegexOptions.IgnoreCase) && !Regex.IsMatch(p.Name, "lash|brow", RegexOptions.IgnoreCase));
        bool alpha = B(s, "alpha");
        p.Blend = s["alphaBlend"] is JsonValue ab ? (bool)ab : alpha;
        p.AlphaTest = s["alphaTest"] is JsonValue at && at.TryGetValue<double>(out var atv) ? (float)atv : alpha ? 0.05f : 0f;
        if (p.Blend && s["blendSrc"] is JsonValue bs && s["blendDst"] is JsonValue bd)
        {
            int a = (int)bs, b = (int)bd;
            if (a >= 0 && a < BlendGl.Length) p.Src = BlendGl[a];
            if (b >= 0 && b < BlendGl.Length) p.Dst = BlendGl[b];
        }
    }

    /// <summary>Rôles du skin choisi (/api/skins) : id et rôles (head_d, body_d, body_n, hands_d…), ou null.</summary>
    static (string Id, HashSet<string> Roles)? ChosenSkin(JsonNode sk)
    {
        var choix = sk?["choix"]?.ToString();
        if (choix is not { Length: > 0 } || (sk["skins"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => x["id"]?.ToString() == choix) is not { } chosen) return null;
        return (choix, (chosen["roles"] as JsonArray ?? []).Select(r => r?.ToString()).Where(r => r != null).ToHashSet());
    }

    static string SkinUrl(string skin, string role) => $"api/skin-texture?skin={Uri.EscapeDataString(skin)}&role={role}";

    static async Task<Dictionary<string, Img>> Fetch(HttpClient http, IEnumerable<string> urls)
    {
        var imgs = new Dictionary<string, Img>();
        foreach (var (u, img) in await Task.WhenAll(urls.Distinct().Select(async u =>
        {
            try { var r = await http.GetAsync(u); return (u, r.IsSuccessStatusCode ? Decode(await r.Content.ReadAsByteArrayAsync()) : null); }
            catch (Exception) { return (u, (Img)null); }
        }))) imgs[u] = img;
        return imgs;
    }

    static async Task<(List<Part>, Dictionary<string, Img>)> Read(HttpClient http, string id, int? hairColor, string skinTone, JsonNode skins, BodyInfo body)
    {
        var shapes = (await GetJson(http, "api/mesh/" + id))!["shapes"]!.AsArray().OfType<JsonObject>().ToList();
        // coiffure ajoutée par le Builder (poste Coiffure) : remplace les formes de la coiffure du preset, comme la page
        try
        {
            var added = await GetJson(http, "api/coiffure-mesh/" + id);
            if (added?["choix"] != null && added["shapes"] is JsonArray ash && ash.Count > 0)
            {
                var hair = AddedHairReplaces;
                shapes = shapes.Where(s => !hair.Contains(S(s, "name").ToLowerInvariant()))
                    .Concat(ash.OfType<JsonObject>().Select(x => x.DeepClone().AsObject())).ToList();
            }
        }
        catch (Exception) { }
        // skin choisi : diffuse du visage du skin sur la tête (comme le FaceGen livré)
        string skinFace = ChosenSkin(skins) is { } cs && cs.Roles.Contains("head_d") ? SkinUrl(cs.Id, "head_d") : null;
        JsonArray overlays = [];
        try { overlays = (await GetJson(http, "api/faceoverlays/" + id)).AsArray(); } catch (Exception) { }
        // forme qui reçoit la teinte FaceGen : la plus grande forme de visage (shader 4) qui a une teinte (pas la bouche)
        var tintTarget = shapes.Where(s => (int?)s["shader"] == 4 && S(s, "tint").Length > 0).OrderByDescending(s => s["verts"]!.AsArray().Count).FirstOrDefault();
        var drawn = shapes.Where(s => S(s, "diffuse") is { Length: > 0 } d && !Helper.IsMatch(d)).ToList();
        // corps entier : la tête est posée sur l'os « NPC Head » du squelette de la race (le jeu l'y attache) ; une tête déjà
        // dans le repère du squelette (centre au-dessus de 50 unités) reste où elle est. Puis la taille en jeu.
        double[] xf = null;
        if (body?.D["osTete"] is JsonArray ot && ot.Count == 16 && drawn.Count > 0)
        {
            double lo = 1e30, hi = -1e30;
            foreach (var s in drawn) { var v = Nums(s["verts"]); for (int i = 2; i < v.Length; i += 3) { lo = Math.Min(lo, v[i]); hi = Math.Max(hi, v[i]); } }
            if ((lo + hi) / 2 < 50) xf = Nums(ot);
        }
        double scale = body?.Scale ?? 1;
        var parts = new List<Part>();
        var urls = new HashSet<string>();
        foreach (var s in drawn)
        {
            var p = new Part { Name = S(s, "name"), Face = (int?)s["shader"] == 4 };
            Material(p, s);
            p.Diffuse = s == tintTarget && skinFace != null ? skinFace : "api/texture?path=" + Uri.EscapeDataString(S(s, "diffuse"));
            urls.Add(p.Diffuse);
            if (S(s, "normal") is { Length: > 0 } nm) { p.Normal = "api/texture?path=" + Uri.EscapeDataString(nm); p.Msn = B(s, "msn"); urls.Add(p.Normal); }
            if (s == tintTarget)
            {
                p.Tint = $"api/tint/{id}?path={Uri.EscapeDataString(S(s, "tint"))}" + (skinTone is { Length: > 0 } ? "&peau=" + skinTone : "");
                urls.Add(p.Tint);
            }
            // cheveux (shader Hair Tint) : couleur de cheveux du preset, en sRGB comme les textures. Le jeu la double : le .jslot (et le
            // PNJ) garde la moitié, le shader d'un export RaceMenu porte la couleur entière (5C5850 → 0,72 0,69 0,63 ; 23/11)
            if (B(s, "hairTint") && hairColor is int hc)
                p.Color = new[] { ((hc >> 16) & 255) * 2 / 255f, ((hc >> 8) & 255) * 2 / 255f, (hc & 255) * 2 / 255f };
            p.Hair = !p.Face && !p.Eye;
            Geometry(p, s, null, null, xf, scale);
            parts.Add(p);
        }
        // maquillages et tatouages du visage : sur la forme qui reçoit la teinte (sinon la plus grande forme de visage)
        var faceBase = parts.FirstOrDefault(p => p.Tint != null) ?? parts.Where(p => p.Face).OrderByDescending(p => p.Vtx.Length).FirstOrDefault();
        int oi = 0;
        foreach (var l in overlays.OfType<JsonObject>())
        {
            if (faceBase == null || S(l, "tex").Length == 0) break;
            var o = Overlay(l, faceBase, oi++);
            o.Face = true;
            urls.Add(o.Diffuse);
            parts.Add(o);
        }
        // textures en parallèle (PNG convertis par le serveur ; cache côté serveur)
        var imgs = await Fetch(http, urls);
        foreach (var p in parts) if (imgs.GetValueOrDefault(p.Diffuse) == null) { p.Missing = true; p.Color = new[] { 1f, 0f, 1f }; }
        return (parts, imgs);
    }

    /// <summary>Maquillage, tatouage ou peinture RaceMenu (texture × couleur, opacité = alpha du preset) sur la géométrie de baseP.</summary>
    static Part Overlay(JsonObject l, Part baseP, int index)
    {
        var o = new Part { Name = S(l, "node"), OverlayOf = baseP, OverlayIndex = index, Blend = true, Body = baseP.Body,
            Diffuse = "api/texture?path=" + Uri.EscapeDataString(S(l, "tex")),
            Opacity = l["alpha"] is JsonValue av && av.TryGetValue<double>(out var a) ? (float)a : 1f };
        if (l["color"] is JsonValue cv && cv.TryGetValue<int>(out var c)) o.Color = new[] { ((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f };
        o.Mid = baseP.Mid; o.Lo = baseP.Lo; o.Hi = baseP.Hi;
        return o;
    }

    /// <summary>Corps entier : corps, mains, pieds et tenue (/api/body), skin choisi sur la peau nue et la peau visible des
    /// armures, couleur de peau du PNJ sur les formes Skin Tint, overlays du corps, pose naturelle et taille en jeu.</summary>
    static async Task<(List<Part>, Dictionary<string, Img>)> ReadBody(HttpClient http, BodyInfo b, JsonNode skins)
    {
        var shapes = (b.D["shapes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var skin = ChosenSkin(skins);
        // skin choisi pour une forme du corps nu (skinPart) ou la peau visible d'une armure (skinZone)
        string SkinTex(JsonObject s, string suffix)
        {
            var part = S(s, "skinPart") is { Length: > 0 } sp ? sp : S(s, "skinZone");
            return skin is { } k && part.Length > 0 && k.Roles.Contains(part + suffix) ? SkinUrl(k.Id, part + suffix) : null;
        }
        var bones = (b.D["os"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var arms = b.Pose ? Pose.Arms(bones) : [];
        var known = bones.Select(x => S(x, "nom")).ToHashSet();
        var parts = new List<Part>();
        var byShape = new Dictionary<JsonObject, Part>();
        var urls = new HashSet<string>();
        foreach (var s in shapes)
        {
            var diffuse = S(s, "diffuse");
            if (diffuse.Length == 0 || Helper.IsMatch(diffuse)) continue;
            // diagnostic : GF_NATIF_SKIP=<nom>;<nom> -> formes du corps écartées
            if ((Environment.GetEnvironmentVariable("GF_NATIF_SKIP") ?? "").Split(';').Contains(S(s, "name"))) continue;
            var p = new Part { Name = S(s, "name"), Body = true };
            Material(p, s);
            p.Diffuse = SkinTex(s, "_d") ?? "api/texture?path=" + Uri.EscapeDataString(diffuse);
            urls.Add(p.Diffuse);
            var normal = SkinTex(s, "_n") ?? (S(s, "normal") is { Length: > 0 } nm ? "api/texture?path=" + Uri.EscapeDataString(nm) : null);
            if (normal != null) { p.Normal = normal; p.Msn = B(s, "msn"); urls.Add(normal); }
            if (B(s, "skinTint")) p.Tone = b.Tone;
            var (raw, quat) = arms.Count > 0 ? Pose.Apply(s, arms, known) : (null, null);
            Geometry(p, s, raw, quat, null, b.Scale);
            parts.Add(p); byShape[s] = p;
        }
        // overlays du corps, des mains et des pieds : sur la peau nue de la zone (corps : la forme principale), et sur la peau
        // visible d'une armure de la même zone, comme SKEE en jeu
        int i = 0;
        foreach (var l in b.Ovl.OfType<JsonObject>())
        {
            var zone = S(l, "zone");
            foreach (var (s, p) in byShape)
            {
                if (!B(s, "skinTint")) continue;
                bool on = S(s, "part") == "armor" ? S(s, "skinZone") == zone : S(s, "part") == zone && (zone != "body" || S(s, "skinPart") == "body");
                if (!on) continue;
                var o = Overlay(l, p, i);
                urls.Add(o.Diffuse);
                parts.Add(o);
            }
            i++;
        }
        var imgs = await Fetch(http, urls);
        foreach (var p in parts) if (imgs.GetValueOrDefault(p.Diffuse) == null) { p.Missing = true; p.Color = new[] { 1f, 0f, 1f }; }
        return (parts, imgs);
    }

    /// <summary>Référence : un export RaceMenu (chemin relatif à Data), ses textures et sa propre teinte (/api/mesh-file).</summary>
    static async Task<(List<Part>, Dictionary<string, Img>)> ReadRef(HttpClient http, string path)
    {
        var shapes = (await GetJson(http, "api/mesh-file?path=" + Uri.EscapeDataString(path)))!["shapes"]!.AsArray().OfType<JsonObject>().ToList();
        var tintTarget = shapes.Where(s => (int?)s["shader"] == 4 && S(s, "tint").Length > 0).OrderByDescending(s => s["verts"]!.AsArray().Count).FirstOrDefault();
        var parts = new List<Part>();
        var urls = new HashSet<string>();
        foreach (var s in shapes)
        {
            var diffuse = S(s, "diffuse");
            if (diffuse.Length == 0 || Helper.IsMatch(diffuse)) continue;
            var p = new Part { Name = S(s, "name"), Face = (int?)s["shader"] == 4, Ref = true };
            p.Eye = (int?)s["shader"] == 16 || (Regex.IsMatch(p.Name, "eye", RegexOptions.IgnoreCase) && !Regex.IsMatch(p.Name, "lash|brow", RegexOptions.IgnoreCase));
            bool alpha = B(s, "alpha");
            p.Blend = s["alphaBlend"] is JsonValue ab ? (bool)ab : alpha;
            p.AlphaTest = s["alphaTest"] is JsonValue at && at.TryGetValue<double>(out var atv) ? (float)atv : alpha ? 0.05f : 0f;
            p.Diffuse = "api/texture?path=" + Uri.EscapeDataString(diffuse);
            urls.Add(p.Diffuse);
            if (s == tintTarget) { p.Tint = "api/texture?path=" + Uri.EscapeDataString(S(s, "tint")); urls.Add(p.Tint); }
            if (B(s, "hairTint")) p.Color = new[] { 0.35f, 0.3f, 0.27f };
            Geometry(p, s);
            parts.Add(p);
        }
        var imgs = new Dictionary<string, Img>();
        foreach (var (u, img) in await Task.WhenAll(urls.Select(async u =>
        {
            try { var r = await http.GetAsync(u); return (u, r.IsSuccessStatusCode ? Decode(await r.Content.ReadAsByteArrayAsync()) : null); }
            catch (Exception) { return (u, (Img)null); }
        }))) imgs[u] = img;
        return (parts, imgs);
    }

    /// <summary>Écart de forme du visage avec la référence (comme la ligne d'état de la page).</summary>
    static async Task<string> Compare(HttpClient http, string id, string path)
    {
        var name = string.Join(@"\", path.Split('\\').TakeLast(2));
        // PNJ remplacé (poste Remplacer un PNJ) : un autre visage, pas d'écart à mesurer (comme la page)
        if (path.StartsWith(@"meshes\actors\character\facegendata\", StringComparison.OrdinalIgnoreCase))
            return L.T("Référence (à droite) : {0}", name);
        try
        {
            var d = await GetJson(http, $"api/compare/{id}?path={Uri.EscapeDataString(path)}");
            return d?["erreur"] is { } err ? L.T("Référence (à droite) : {0}", name) + " · " + err
                : L.T("Référence (à droite) : {0}", name) + " · " + L.T("écart du visage : moyen {0} · 95 % sous {1} · max {2} (< 0,1 ≈ identique)", d?["moyen"], d?["p95"], d?["max"]);
        }
        catch (Exception) { return L.T("Référence (à droite) : {0}", name) + " · " + L.T("pas d'écart mesuré (pas de tête générée)"); }
    }

    static double[] Nums(JsonNode n) => (n as JsonArray ?? []).Select(x => (double)x!).ToArray();
    static string S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    static bool B(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    const int Stride = 12;   // position 3, normale 3, UV 2, tangente 4

    /// <summary>Tampons d'une forme. raw : sommets déjà posés (repère Skyrim) au lieu de ceux du JSON ; quat : rotation de pose
    /// de chaque sommet (4 par sommet, normal maps « _msn ») ; xf : matrice 4×4 par lignes (os de la tête en corps entier) ;
    /// scale : taille en jeu, depuis les pieds (origine du squelette).</summary>
    static void Geometry(Part p, JsonObject s, double[] raw = null, float[] quat = null, double[] xf = null, double scale = 1)
    {
        var v = raw ?? Nums(s["verts"]);
        if (xf != null || scale != 1)
        {
            v = (double[])v.Clone();
            for (int i = 0; i + 2 < v.Length; i += 3)
            {
                double x = v[i], y = v[i + 1], z = v[i + 2];
                if (xf != null)
                {
                    v[i] = xf[0] * x + xf[1] * y + xf[2] * z + xf[3];
                    v[i + 1] = xf[4] * x + xf[5] * y + xf[6] * z + xf[7];
                    v[i + 2] = xf[8] * x + xf[9] * y + xf[10] * z + xf[11];
                }
                v[i] *= scale; v[i + 1] *= scale; v[i + 2] *= scale;
            }
        }
        var uv = (s["uvs"] as JsonArray ?? []).Select(x => (double)x!).ToArray();
        var tr = s["tris"]!.AsArray().Select(x => (uint)(int)x!).ToArray();
        int nv = v.Length / 3;
        var pos = new double[nv * 3];
        double[] lo = { 1e30, 1e30, 1e30 }, hi = { -1e30, -1e30, -1e30 };
        for (int i = 0; i < nv; i++)
        {
            pos[i * 3] = -v[i * 3]; pos[i * 3 + 1] = v[i * 3 + 2]; pos[i * 3 + 2] = v[i * 3 + 1];
            for (int k = 0; k < 3; k++) { double c = pos[i * 3 + k]; lo[k] = Math.Min(lo[k], c); hi[k] = Math.Max(hi[k], c); }
        }
        for (int k = 0; k < 3; k++) p.Mid[k] = (lo[k] + hi[k]) / 2;
        p.Lo = lo; p.Hi = hi;
        // normales pondérées par l'aire (computeVertexNormals de three.js)
        var nor = new double[nv * 3];
        for (int t = 0; t + 2 < tr.Length; t += 3)
        {
            uint a = tr[t], b = tr[t + 1], c = tr[t + 2];
            if (a >= nv || b >= nv || c >= nv) continue;
            double ux = pos[b * 3] - pos[a * 3], uy = pos[b * 3 + 1] - pos[a * 3 + 1], uz = pos[b * 3 + 2] - pos[a * 3 + 2];
            double wx = pos[c * 3] - pos[a * 3], wy = pos[c * 3 + 1] - pos[a * 3 + 1], wz = pos[c * 3 + 2] - pos[a * 3 + 2];
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            foreach (var q in new[] { a, b, c }) { nor[q * 3] += nx; nor[q * 3 + 1] += ny; nor[q * 3 + 2] += nz; }
        }
        // tangentes (relief en espace tangent) : par triangle d'après les UV, cumulées par sommet ; w = sens du bitangent
        var tan = new double[nv * 3]; var bit = new double[nv * 3];
        if (uv.Length >= nv * 2)
            for (int t = 0; t + 2 < tr.Length; t += 3)
            {
                uint a = tr[t], b = tr[t + 1], c = tr[t + 2];
                if (a >= nv || b >= nv || c >= nv) continue;
                double e1x = pos[b * 3] - pos[a * 3], e1y = pos[b * 3 + 1] - pos[a * 3 + 1], e1z = pos[b * 3 + 2] - pos[a * 3 + 2];
                double e2x = pos[c * 3] - pos[a * 3], e2y = pos[c * 3 + 1] - pos[a * 3 + 1], e2z = pos[c * 3 + 2] - pos[a * 3 + 2];
                double du1 = uv[b * 2] - uv[a * 2], dv1 = uv[b * 2 + 1] - uv[a * 2 + 1], du2 = uv[c * 2] - uv[a * 2], dv2 = uv[c * 2 + 1] - uv[a * 2 + 1];
                double det = du1 * dv2 - du2 * dv1;
                if (Math.Abs(det) < 1e-12) continue;
                double r = 1 / det;
                double tx = (e1x * dv2 - e2x * dv1) * r, ty = (e1y * dv2 - e2y * dv1) * r, tz = (e1z * dv2 - e2z * dv1) * r;
                double bx = (e2x * du1 - e1x * du2) * r, by = (e2y * du1 - e1y * du2) * r, bz = (e2z * du1 - e1z * du2) * r;
                foreach (var q in new[] { a, b, c }) { tan[q * 3] += tx; tan[q * 3 + 1] += ty; tan[q * 3 + 2] += tz; bit[q * 3] += bx; bit[q * 3 + 1] += by; bit[q * 3 + 2] += bz; }
            }
        var vtx = new float[nv * Stride];
        for (int i = 0; i < nv; i++)
        {
            double nx = nor[i * 3], ny = nor[i * 3 + 1], nz = nor[i * 3 + 2], l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l < 1e-20) l = 1;
            nx /= l; ny /= l; nz /= l;
            int o = i * Stride;
            vtx[o] = (float)pos[i * 3]; vtx[o + 1] = (float)pos[i * 3 + 1]; vtx[o + 2] = (float)pos[i * 3 + 2];
            vtx[o + 3] = (float)nx; vtx[o + 4] = (float)ny; vtx[o + 5] = (float)nz;
            if (uv.Length >= (i + 1) * 2) { vtx[o + 6] = (float)uv[i * 2]; vtx[o + 7] = (float)uv[i * 2 + 1]; }
            double tx = tan[i * 3], ty = tan[i * 3 + 1], tz = tan[i * 3 + 2], tl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            // normal map en espace objet : pas de tangente, la place sert à la rotation de pose du sommet (identité sans pose)
            if (p.Msn)
            {
                if (quat != null && quat.Length >= (i + 1) * 4) { vtx[o + 8] = quat[i * 4]; vtx[o + 9] = quat[i * 4 + 1]; vtx[o + 10] = quat[i * 4 + 2]; vtx[o + 11] = quat[i * 4 + 3]; }
                else vtx[o + 11] = 1;
            }
            else if (tl > 1e-20)
            {
                tx /= tl; ty /= tl; tz /= tl;
                // sens : bitangent du même côté que n × t ?
                double cx = ny * tz - nz * ty, cy = nz * tx - nx * tz, cz = nx * ty - ny * tx;
                double w = cx * bit[i * 3] + cy * bit[i * 3 + 1] + cz * bit[i * 3 + 2] < 0 ? -1 : 1;
                vtx[o + 8] = (float)tx; vtx[o + 9] = (float)ty; vtx[o + 10] = (float)tz; vtx[o + 11] = (float)w;
            }
        }
        p.Vtx = vtx; p.Idx = tr;
    }

    static Img Decode(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            // pixels lus tels quels (LockBits convertit au besoin sans prémultiplier) : redessiner l'image avec GDI+ effaçait la
            // couleur des pixels d'alpha 0, et les normal maps dont l'alpha est un masque de brillance (souvent 0) perdaient leurs
            // normales (taches claires sur l'armure de loup, 18/11)
            using var b = new Bitmap(ms);
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[b.Width * b.Height * 4];
            for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * b.Width * 4, b.Width * 4);
            b.UnlockBits(d);
            return new Img { Px = px, W = b.Width, H = b.Height };
        }
        catch (Exception) { return null; }
    }

    void Clear()
    {
        if (ctx == IntPtr.Zero) { meshes.Clear(); return; }
        GL.wglMakeCurrent(hdc, ctx);
        var texs = new HashSet<uint>();
        foreach (var m in meshes)
        {
            if (m.OwnsBuffers) GL.glDeleteBuffers(2, new[] { m.Vbo, m.Ibo });
            if (m.Tex != 0) texs.Add(m.Tex);
            if (m.TintTex != 0) texs.Add(m.TintTex);
            if (m.NmapTex != 0) texs.Add(m.NmapTex);
        }
        if (texs.Count > 0) GL.glDeleteTextures(texs.Count, texs.ToArray());
        meshes.Clear();
    }

    void Upload(List<Part> parts, Dictionary<string, Img> imgs)
    {
        if (ctx == IntPtr.Zero) return;
        Clear();
        GL.wglMakeCurrent(hdc, ctx);
        var texIds = new Dictionary<string, uint>();
        foreach (var (key, img) in imgs)
        {
            if (img == null) continue;
            var ids = new uint[1]; GL.glGenTextures(1, ids);
            Fill(ids[0], img);
            texIds[key] = ids[0];
        }
        UploadMeshes(parts, texIds);
    }

    /// <summary>Remplit une texture OpenGL (contexte courant) avec une image, mipmaps compris.</summary>
    static void Fill(uint tex, Img img)
    {
            GL.glBindTexture(GL.TEXTURE_2D, tex);
            GL.glPixelStorei(GL.UNPACK_ALIGNMENT, 1);
            var h = GCHandle.Alloc(img.Px, GCHandleType.Pinned);
            GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA8, img.W, img.H, 0, GL.BGRA, GL.UNSIGNED_BYTE, h.AddrOfPinnedObject());
            h.Free();
            // 0x505 = mémoire vidéo insuffisante, 0x501 = texture plus grande que la carte ne l'accepte : la forme serait noire sans rien dire
            if (GL.glGetError() is var err and not 0) { Report += $" | texture {img.W}x{img.H} error 0x{err:X}"; Console.WriteLine($"OpenGL : texture {img.W}x{img.H}, error 0x{err:X}"); }
            bool mip = GL.glGenerateMipmap != null;
            if (mip) GL.glGenerateMipmap(GL.TEXTURE_2D);
            GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)(mip ? GL.LINEAR_MIPMAP_LINEAR : GL.LINEAR));
            GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
            GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.REPEAT);
            GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.REPEAT);
            GL.glTexParameterf(GL.TEXTURE_2D, GL.TEXTURE_MAX_ANISOTROPY, 8f);
            GL.glGetError();
    }

    void UploadMeshes(List<Part> parts, Dictionary<string, uint> texIds)
    {
        var byPart = new Dictionary<Part, Mesh>();
        foreach (var p in parts.Where(p => p.OverlayOf == null))
        {
            var b = new uint[2]; GL.glGenBuffers(2, b);
            Buffer(GL.ARRAY_BUFFER, b[0], p.Vtx, p.Vtx.Length * 4);
            Buffer(GL.ELEMENT_ARRAY_BUFFER, b[1], p.Idx, p.Idx.Length * 4);
            var m = new Mesh { P = p, Vbo = b[0], Ibo = b[1], Count = p.Idx.Length,
                Tex = texIds.GetValueOrDefault(p.Diffuse ?? ""), TintTex = p.Tint != null ? texIds.GetValueOrDefault(p.Tint) : 0,
                NmapTex = p.Normal != null ? texIds.GetValueOrDefault(p.Normal) : 0 };
            meshes.Add(m); byPart[p] = m;
        }
        // overlays : mêmes tampons que leur forme de base
        foreach (var p in parts.Where(p => p.OverlayOf != null && byPart.ContainsKey(p.OverlayOf)))
        {
            var bm = byPart[p.OverlayOf];
            meshes.Add(new Mesh { P = p, Vbo = bm.Vbo, Ibo = bm.Ibo, Count = bm.Count, OwnsBuffers = false, Tex = texIds.GetValueOrDefault(p.Diffuse ?? "") });
        }
        foreach (var p in parts) { p.Vtx = null; p.Idx = null; }
        Frame();
        PlaceRef();
        if (keepViewNext) { keepViewNext = false; dirty = true; Invalidate(); }
        else SetView("front");
    }

    /// <summary>Cadrage sur les formes du visage (comme la page) : centre de la boîte, un peu relevé.</summary>
    void Frame()
    {
        // corps entier : de la tête aux pieds, vu de face
        var all = meshes.Where(m => m.P.Body && m.P.OverlayOf == null).ToList();
        if (all.Count > 0)
        {
            all.AddRange(meshes.Where(m => !m.P.Body && !m.P.Ref && m.P.OverlayOf == null));
            double[] bl = { 1e30, 1e30, 1e30 }, bh = { -1e30, -1e30, -1e30 };
            foreach (var m in all) for (int k = 0; k < 3; k++) { bl[k] = Math.Min(bl[k], m.P.Lo[k]); bh[k] = Math.Max(bh[k], m.P.Hi[k]); }
            center = new[] { (bl[0] + bh[0]) / 2, (bl[1] + bh[1]) / 2, (bl[2] + bh[2]) / 2 };
            radius = (bh[1] - bl[1]) * 0.66;   // hauteur entière dans la vue (champ de 22°, distance 4,2 × rayon), avec une marge
            return;
        }
        var face = meshes.Where(m => m.P.Face && !m.P.Ref && m.P.OverlayOf == null).ToList();
        if (face.Count == 0) face = meshes.Where(m => !m.P.Ref).ToList();
        if (face.Count == 0) return;
        double[] lo = { 1e30, 1e30, 1e30 }, hi = { -1e30, -1e30, -1e30 };
        foreach (var m in face) for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], m.P.Lo[k]); hi[k] = Math.Max(hi[k], m.P.Hi[k]); }
        center = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2 + (hi[1] - lo[1]) * 0.05, (lo[2] + hi[2]) / 2 };
        double dx = hi[0] - lo[0], dy = hi[1] - lo[1], dz = hi[2] - lo[2];
        radius = Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2 * 1.35;   // tête entière, cheveux compris
    }

    /// <summary>Référence côte à côte : décalée d'une largeur et demie de tête vers la droite de l'écran, et à la hauteur de
    /// notre tête si elle est dans un autre repère (tête de PNJ du jeu : visage vers 120 unités de haut).</summary>
    void PlaceRef()
    {
        refOffset = new double[3];
        var head = meshes.Where(m => m.P.Face && !m.P.Ref && m.P.OverlayOf == null).ToList();
        var refs = meshes.Where(m => m.P.Face && m.P.Ref).ToList();
        if (refs.Count == 0) return;
        double Lo(List<Mesh> ms, int k) => ms.Min(m => m.P.Lo[k]); double Hi(List<Mesh> ms, int k) => ms.Max(m => m.P.Hi[k]);
        double w = head.Count > 0 ? Hi(head, 0) - Lo(head, 0) : 20;
        refOffset[0] = w * 1.5;
        if (head.Count > 0)
        {
            double dy = (Lo(head, 1) + Hi(head, 1)) / 2 - (Lo(refs, 1) + Hi(refs, 1)) / 2;
            if (Math.Abs(dy) > 50) refOffset[1] = dy;
            // cadrage sur les deux têtes
            center[0] += refOffset[0] / 2;
            radius *= 1.6;
        }
    }

    static void Buffer(uint target, uint id, Array data, int bytes)
    {
        GL.glBindBuffer(target, id);
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        GL.glBufferData(target, new IntPtr(bytes), h.AddrOfPinnedObject(), GL.STATIC_DRAW);
        h.Free();
    }

    // ---------------- caméra ----------------
    public void SetView(string v)
    {
        double a = v == "three" ? Math.PI / 5 : v == "side" ? Math.PI / 2 : v == "back" ? Math.PI : 0;   // back : de dos (captures)
        target = (double[])center.Clone();
        theta = a; phi = Math.PI / 2; dist = radius * 4.2;
        dTheta = dPhi = 0; panPending = new double[3];
        dirty = true; Invalidate();
    }

    double[] Eye() => new[] { target[0] + dist * Math.Sin(phi) * Math.Sin(theta), target[1] + dist * Math.Cos(phi), target[2] + dist * Math.Sin(phi) * Math.Cos(theta) };

    void Step()
    {
        const double damp = 0.05;
        bool moving = Math.Abs(dTheta) > 1e-6 || Math.Abs(dPhi) > 1e-6 || Math.Abs(panPending[0]) + Math.Abs(panPending[1]) + Math.Abs(panPending[2]) > 1e-6;
        if (moving)
        {
            theta += dTheta * damp; phi = Math.Max(1e-3, Math.Min(Math.PI - 1e-3, phi + dPhi * damp));
            for (int k = 0; k < 3; k++) { target[k] += panPending[k] * damp; panPending[k] *= 1 - damp; }
            dTheta *= 1 - damp; dPhi *= 1 - damp;
            dirty = true;
        }
        if (dirty) Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); dragging = e.Button; last = e.Location; Capture = true; }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = MouseButtons.None; Capture = false; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging == MouseButtons.None) return;
        double dx = e.X - last.X, dy = e.Y - last.Y; last = e.Location;
        double h = Math.Max(1, ClientSize.Height);
        if (dragging == MouseButtons.Left) { dTheta -= 2 * Math.PI * dx / h; dPhi -= 2 * Math.PI * dy / h; }
        else
        {
            var eye = Eye();
            double[] f = GL.Norm(GL.Sub(target, eye)), right = GL.Norm(GL.Cross(f, new[] { 0.0, 1, 0 })), up = GL.Cross(right, f);
            double td = dist * Math.Tan(22 * Math.PI / 360), px = -2 * dx * td / h, py = 2 * dy * td / h;
            for (int k = 0; k < 3; k++) panPending[k] += right[k] * px + up[k] * py;
        }
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        double s = Math.Pow(0.95, Math.Abs(e.Delta) / 120.0);
        dist = e.Delta > 0 ? dist * s : dist / s;
        dist = Math.Max(radius * 0.6, Math.Min(radius * 30, dist));
        dirty = true;
    }

    // ---------------- dessin ----------------
    void FreeFbo()
    {
        if (fbo != 0) { GL.glDeleteFramebuffers(1, new[] { fbo }); fbo = 0; }
        if (rbColor != 0) { GL.glDeleteRenderbuffers(2, new[] { rbColor, rbDepth }); rbColor = rbDepth = 0; }
        fbW = fbH = 0;
    }

    bool EnsureFbo(int w, int h)
    {
        if (samples < 2) return false;
        if (fbo != 0 && fbW == w && fbH == h) return true;
        FreeFbo();
        var a = new uint[1]; GL.glGenFramebuffers(1, a); fbo = a[0];
        var r = new uint[2]; GL.glGenRenderbuffers(2, r); rbColor = r[0]; rbDepth = r[1];
        GL.glBindFramebuffer(GL.FRAMEBUFFER, fbo);
        GL.glBindRenderbuffer(GL.RENDERBUFFER, rbColor);
        GL.glRenderbufferStorageMultisample(GL.RENDERBUFFER, samples, GL.RGBA8, w, h);
        GL.glFramebufferRenderbuffer(GL.FRAMEBUFFER, GL.COLOR_ATTACHMENT0, GL.RENDERBUFFER, rbColor);
        GL.glBindRenderbuffer(GL.RENDERBUFFER, rbDepth);
        GL.glRenderbufferStorageMultisample(GL.RENDERBUFFER, samples, GL.DEPTH_COMPONENT24, w, h);
        GL.glFramebufferRenderbuffer(GL.FRAMEBUFFER, GL.DEPTH_ATTACHMENT, GL.RENDERBUFFER, rbDepth);
        bool ok = GL.glCheckFramebufferStatus(GL.FRAMEBUFFER) == GL.FRAMEBUFFER_COMPLETE;
        GL.glBindFramebuffer(GL.FRAMEBUFFER, 0);
        if (!ok) { FreeFbo(); samples = 0; }
        fbW = w; fbH = h;
        return ok;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ctx == IntPtr.Zero)
        {
            if (!use3D || GlError != null) PaintSummary(e.Graphics);
            else e.Graphics.Clear(BackColor);
            return;
        }
        Render(false);
        GL.SwapBuffers(hdc);
    }

    /// <summary>Dessine la scène dans le tampon arrière ; read : rend aussi l'image (capture de test).</summary>
    Bitmap Render(bool read)
    {
        dirty = false;
        int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
        GL.wglMakeCurrent(hdc, ctx);
        bool ms = EnsureFbo(w, h);
        if (ms) GL.glBindFramebuffer(GL.FRAMEBUFFER, fbo);
        GL.glViewport(0, 0, w, h);
        GL.glClearColor(Bg[0], Bg[1], Bg[2], 1);
        GL.glDepthMask(true);
        GL.glClear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT);
        if (meshes.Count > 0) DrawScene(w, h);
        if (ms)
        {
            GL.glBindFramebuffer(GL.READ_FRAMEBUFFER, fbo);
            GL.glBindFramebuffer(GL.DRAW_FRAMEBUFFER, 0);
            GL.glBlitFramebuffer(0, 0, w, h, 0, 0, w, h, GL.COLOR_BUFFER_BIT, GL.NEAREST);
            GL.glBindFramebuffer(GL.FRAMEBUFFER, 0);
        }
        if (!read) return null;
        var px = new byte[w * h * 4];
        var hnd = GCHandle.Alloc(px, GCHandleType.Pinned);
        GL.glPixelStorei(GL.PACK_ALIGNMENT, 1);
        GL.glReadPixels(0, 0, w, h, GL.BGRA, GL.UNSIGNED_BYTE, hnd.AddrOfPinnedObject());
        hnd.Free();
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(px, (h - 1 - y) * w * 4, d.Scan0 + y * d.Stride, w * 4);   // OpenGL : bas en premier
        bmp.UnlockBits(d);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var c = bmp.GetPixel(x, y); if (c.A != 255) bmp.SetPixel(x, y, Color.FromArgb(255, c)); }
        return bmp;
    }

    /// <summary>Image de la vue 3D (tests : capture de la fenêtre native).</summary>
    public Bitmap Snapshot() => ctx == IntPtr.Zero ? null : Render(true);

    void DrawScene(int w, int h)
    {
        var eye = Eye();
        // plans proche et lointain suivant la distance : avec un plan proche fixe (0,1), la précision de profondeur ne suffisait
        // plus en corps entier (caméra à ~350 unités) et les couches d'une armure scintillaient (taches de la peau dessous)
        double near = Math.Max(0.1, dist - radius * 3), far = dist + radius * 6;
        near = Math.Min(near, dist * 0.5);
        var mvp = GL.Mul(GL.Perspective(22, (double)w / h, near, far), GL.LookAt(eye, target, new[] { 0.0, 1, 0 }));
        GL.glUseProgram(prog);
        GL.glUniformMatrix4fv(uMvp, 1, false, mvp);
        GL.glUniform1i(uTex, 0); GL.glUniform1i(uTint, 1);
        GL.glUniform1f(uKey, 1.2f);
        GL.glUniform3f(uKeyDir, -30, 40, 80);
        GL.glUniform3f(uRimDir, 60, 30, -60);
        GL.glUniform3f(uCam, (float)eye[0], (float)eye[1], (float)eye[2]);
        GL.glEnable(GL.DEPTH_TEST); GL.glDepthFunc(GL.LEQUAL);
        GL.glUniform1i(uNmap, 2);
        for (uint a = 0; a < 4; a++) GL.glEnableVertexAttribArray(a);
        GL.glDisable(GL.CULL_FACE);
        var visible = meshes.Where(m => (m.P.Body || m.P.Face || m.P.Eye || (hairOn && (m.P.Ref || !hideHair))) && (m.P.OverlayOf == null || overlaysOn)).ToList();
        var opaque = visible.Where(m => !m.P.Blend).ToList();
        var overlay = visible.Where(m => m.P.OverlayOf != null).OrderBy(m => m.P.OverlayIndex).ToList();
        var trans = visible.Where(m => m.P.Blend && m.P.OverlayOf == null).OrderByDescending(m => D2(m.P.Mid, eye)).ToList();   // loin d'abord
        GL.glDisable(GL.BLEND); GL.glDepthMask(true);
        foreach (var m in opaque) Draw(m);
        GL.glEnable(GL.BLEND); GL.glDepthMask(false);
        // maquillages, tatouages : sur la peau, avant cheveux et cils (décalage de profondeur comme la page)
        GL.glEnable(POLYGON_OFFSET_FILL); GL.glEnable(GL.CULL_FACE); GL.glCullFace(GL.BACK);
        GL.glBlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
        foreach (var m in overlay) { glPolygonOffset(-2 - m.P.OverlayIndex, -2 - m.P.OverlayIndex); Draw(m); }
        GL.glDisable(POLYGON_OFFSET_FILL);
        foreach (var m in trans)
        {
            GL.glBlendFunc(m.P.Src, m.P.Dst);
            GL.glCullFace(GL.FRONT); Draw(m);   // faces arrière, puis faces avant
            GL.glCullFace(GL.BACK); Draw(m);
        }
        GL.glDepthMask(true); GL.glDisable(GL.BLEND); GL.glDisable(GL.CULL_FACE);
    }

    static double D2(double[] a, double[] b) { double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2]; return x * x + y * y + z * z; }

    void Draw(Mesh m)
    {
        GL.glActiveTexture(GL.TEXTURE0 + 1);
        GL.glBindTexture(GL.TEXTURE_2D, m.TintTex);
        GL.glActiveTexture(GL.TEXTURE0 + 2);
        GL.glBindTexture(GL.TEXTURE_2D, reliefOn ? m.NmapTex : 0);
        GL.glUniform1i(uNmapMode, reliefOn && m.NmapTex != 0 ? (m.P.Msn ? 2 : 1) : 0);
        GL.glUniform1f(uOpacity, m.P.Opacity);
        GL.glUniform3f(uOffset, m.P.Ref ? (float)refOffset[0] : 0f, m.P.Ref ? (float)refOffset[1] : 0f, m.P.Ref ? (float)refOffset[2] : 0f);
        GL.glActiveTexture(GL.TEXTURE0);
        GL.glBindTexture(GL.TEXTURE_2D, m.Tex);
        GL.glUniform1i(uHasTex, m.Tex != 0 ? 1 : 0);
        GL.glUniform1i(uHasTint, m.TintTex != 0 ? 1 : m.P.Tone != null ? 2 : 0);
        if (m.P.Tone != null) GL.glUniform3f(uTone, m.P.Tone[0], m.P.Tone[1], m.P.Tone[2]);
        GL.glUniform3f(uColor, m.P.Color[0], m.P.Color[1], m.P.Color[2]);
        GL.glUniform1f(uAlphaTest, m.P.AlphaTest);
        GL.glUniform1f(uRough, m.P.Face ? 0.62f : 0.8f);
        GL.glBindBuffer(GL.ARRAY_BUFFER, m.Vbo);
        GL.glVertexAttribPointer(0, 3, GL.FLOAT, false, Stride * 4, IntPtr.Zero);
        GL.glVertexAttribPointer(1, 3, GL.FLOAT, false, Stride * 4, new IntPtr(12));
        GL.glVertexAttribPointer(2, 2, GL.FLOAT, false, Stride * 4, new IntPtr(24));
        GL.glVertexAttribPointer(3, 4, GL.FLOAT, false, Stride * 4, new IntPtr(32));
        GL.glBindBuffer(GL.ELEMENT_ARRAY_BUFFER, m.Ibo);
        GL.glDrawElements(GL.TRIANGLES, m.Count, GL.UNSIGNED_INT, IntPtr.Zero);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); dirty = true; Invalidate(); }
}
