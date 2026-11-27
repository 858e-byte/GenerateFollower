#nullable disable
using System.Text.Json.Nodes;

namespace GenerateFollower.Native;

/// <summary>
/// Pose naturelle du corps entier (vue native, étape 3), comme la page (poseArms / applyPose de builder\index.html) : le corps
/// du jeu est en pose de repos (bras écartés). Chaque bras pivote à l'épaule (os « UpperArm » du squelette de la race) pour
/// descendre le long du corps ; ce qu'il porte suit. L'avant-bras (os « Forearm » et ce qui en descend) se plie un peu au
/// coude, vers l'avant (+Y). Un sommet se déplace selon la part de ses poids sur les os du bras. Rotation de chaque sommet
/// gardée (quaternion) pour tourner aussi la normale d'une normal map en espace objet (« _msn »). Repère Skyrim (Z en haut).
/// </summary>
static class Pose
{
    internal sealed class Arm { public HashSet<string> Set; public Func<double[], double[]> D; public double[] Q; }

    static readonly string[][] Bones =
    [
        ["NPC L UpperArm [LUar]", "NPC L Forearm [LLar]", "NPC L Hand [LHnd]"],
        ["NPC R UpperArm [RUar]", "NPC R Forearm [RLar]", "NPC R Hand [RHnd]"],
    ];
    const double Gap = 14 * Math.PI / 180;     // angle gardé entre le bras et la verticale (sans traverser la hanche)
    const double Elbow = 18 * Math.PI / 180;   // pli du coude, bras détendu

    static string S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    static double[] Norm(double[] a) { double l = Math.Sqrt(Dot(a, a)); return l < 1e-12 ? a : [a[0] / l, a[1] / l, a[2] / l]; }
    static double[] QAxis(double[] axis, double angle) { var n = Norm(axis); double s = Math.Sin(angle / 2); return [n[0] * s, n[1] * s, n[2] * s, Math.Cos(angle / 2)]; }
    // v + 2·q.xyz × (q.xyz × v + w·v)
    static double[] QRot(double[] q, double[] v)
    {
        double[] u = [q[0], q[1], q[2]];
        var c = Cross(u, v); c = [c[0] + q[3] * v[0], c[1] + q[3] * v[1], c[2] + q[3] * v[2]];
        var d = Cross(u, c);
        return [v[0] + 2 * d[0], v[1] + 2 * d[1], v[2] + 2 * d[2]];
    }
    // a·b : b d'abord, puis a (comme Quaternion.multiply de three.js)
    static double[] QMul(double[] a, double[] b) =>
    [
        a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
        a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
        a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
        a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
    ];
    static Func<double[], double[]> Pivot(double[] p, double[] q) => v => { var r = QRot(q, Sub(v, p)); return [r[0] + p[0], r[1] + p[1], r[2] + p[2]]; };

    /// <summary>Transformations des deux bras d'après les os du squelette (/api/body : os = nom, parent, matrice par lignes).</summary>
    internal static List<Arm> Arms(List<JsonObject> os)
    {
        var byName = new Dictionary<string, JsonObject>();
        foreach (var b in os) if (S(b, "nom") is { } n) byName.TryAdd(n, b);
        double[] Pos(JsonObject b) { var m = b["m"] as JsonArray; return m is { Count: 16 } ? [(double)m[3], (double)m[7], (double)m[11]] : [0, 0, 0]; }
        HashSet<string> Subtree(string root)
        {
            var set = new HashSet<string> { root };
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var b in os) if (S(b, "nom") is { } n && !set.Contains(n) && S(b, "parent") is { } par && set.Contains(par)) { set.Add(n); grew = true; }
            }
            return set;
        }
        var arms = new List<Arm>();
        foreach (var names in Bones)
        {
            if (!byName.TryGetValue(names[0], out var u) || !byName.TryGetValue(names[2], out var h)) continue;
            byName.TryGetValue(names[1], out var f);
            var set = Subtree(names[0]);
            var p = Pos(u);
            var dir = Norm(Sub(Pos(h), p));
            double[] down = [0, 0, -1];
            double angle = Math.Acos(Math.Clamp(Dot(dir, down), -1, 1)) - Gap;
            if (angle <= 0) continue;
            var q = QAxis(Cross(dir, down), angle);
            var d = Pivot(p, q);
            if (f == null || !set.Contains(names[1])) { arms.Add(new Arm { Set = set, D = d, Q = q }); continue; }
            // coude : pivot = l'os Forearm une fois le bras descendu ; axe = bras descendu × avant (la main avance)
            var foreSet = Subtree(names[1]);
            var pf = d(Pos(f));
            var axis = Cross(QRot(q, dir), [0, 1, 0]);
            if (Dot(axis, axis) < 1e-6) { arms.Add(new Arm { Set = set, D = d, Q = q }); continue; }
            var qe = QAxis(axis, Elbow);
            var pe = Pivot(pf, qe);
            arms.Add(new Arm { Set = set.Where(x => !foreSet.Contains(x)).ToHashSet(), D = d, Q = q });
            arms.Add(new Arm { Set = foreSet, D = v => pe(d(v)), Q = QMul(qe, q) });
        }
        return arms;
    }

    /// <summary>Sommets posés d'une forme (repère Skyrim) et rotation de chaque sommet (4 par sommet) ; (null, null) si la forme
    /// n'a pas de poids (laissée en pose de repos).</summary>
    internal static (double[], float[]) Apply(JsonObject s, List<Arm> arms, HashSet<string> known)
    {
        if (s["bones"] is not JsonArray bonesJ || s["bi"] is not JsonArray biJ || s["bw"] is not JsonArray bwJ) return (null, null);
        var bones = bonesJ.Select(x => x?.ToString()).ToArray();
        var parents = (s["boneParents"] as JsonArray ?? []).Select(x => x?.ToString()).ToArray();
        var parentOf = new Dictionary<string, string>();
        for (int k = 0; k < bones.Length; k++) if (bones[k] != null) parentOf.TryAdd(bones[k], k < parents.Length ? parents[k] : null);
        // os absent du squelette de la race (os propre à l'armure) : on remonte ses parents jusqu'à un os du squelette
        bool InSet(HashSet<string> set, string b)
        {
            for (int k = 0; b != null && k < 16; k++, b = parentOf.GetValueOrDefault(b)) if (known.Contains(b)) return set.Contains(b);
            return false;
        }
        var inArm = arms.Select(a => bones.Select(b => InSet(a.Set, b)).ToArray()).ToArray();
        var rest = (s["verts"] as JsonArray ?? []).Select(x => (double)x!).ToArray();
        var bi = biJ.Select(x => (int)x!).ToArray();
        var bw = bwJ.Select(x => (double)x!).ToArray();
        int n = rest.Length / 3;
        if (bi.Length < n * 4 || bw.Length < n * 4) return (null, null);
        var outv = new double[rest.Length];
        var quat = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            double[] v = [rest[i * 3], rest[i * 3 + 1], rest[i * 3 + 2]];
            double x = v[0], y = v[1], z = v[2], qx = 0, qy = 0, qz = 0, qw = 0, wRest = 1;
            for (int k = 0; k < arms.Count; k++)
            {
                double w = 0;
                for (int j = 0; j < 4; j++) { int bix = bi[i * 4 + j]; if (bix >= 0 && bix < bones.Length && inArm[k][bix]) w += bw[i * 4 + j]; }
                if (w <= 0) continue;
                var a = arms[k].D(v);
                x += (a[0] - v[0]) * w; y += (a[1] - v[1]) * w; z += (a[2] - v[2]) * w;
                var q = arms[k].Q;
                qx += q[0] * w; qy += q[1] * w; qz += q[2] * w; qw += q[3] * w;
                wRest -= w;
            }
            outv[i * 3] = x; outv[i * 3 + 1] = y; outv[i * 3 + 2] = z;
            // rotation du sommet : moyenne pondérée (normalisée) de l'identité et des rotations des bras
            qw += Math.Max(wRest, 0);
            double l = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (l < 1e-12) { qx = qy = qz = 0; qw = 1; l = 1; }
            quat[i * 4] = (float)(qx / l); quat[i * 4 + 1] = (float)(qy / l); quat[i * 4 + 2] = (float)(qz / l); quat[i * 4 + 3] = (float)(qw / l);
        }
        return (outv, quat);
    }
}
