using System.Diagnostics;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace GenerateFollower;

/// <summary>
/// Extrait d'une voix du jeu pour l'écouter dans le Builder (plan autonome .NET, étape 7 ; remplace
/// offline\voice_sample.py, même choix de réplique) : un .fuz lu dans l'archive de voix du jeu (Skyrim - Voices_*0.bsa,
/// Dawnguard.bsa / Dragonborn.bsa pour les voix des DLC), son audio xWMA joué directement par Windows (XAudio2, sans
/// ffmpeg) ; WAV par ffmpeg (PATH ou GF_FFMPEG) en secours, pour la page (navigateur sur un autre PC, XAudio2 absent).
/// Lecture seule dans le dossier du jeu.
/// </summary>
public static class VoiceSample
{
    /// <summary>(réplique choisie, audio xWMA) d'une voix, ou une erreur claire.</summary>
    public static (string Pick, byte[] Xwm) Extract(string dataDir, string voice, string plugin)
    {
        plugin = plugin.ToLowerInvariant();
        if (!Regex.IsMatch(voice, "^[A-Za-z0-9_]{1,64}$") || plugin is not ("skyrim.esm" or "dawnguard.esm" or "dragonborn.esm"))
            throw new GfException("paramètres refusés");
        // Skyrim SE : les voix des DLC sont aussi dans l'archive de voix principale (dossier sound\voice\dawnguard.esm\…)
        var archives = Directory.EnumerateFiles(dataDir, "Skyrim - Voices_*0.bsa").Order(StringComparer.Ordinal).ToList();
        if (plugin == "dawnguard.esm") archives.Add(Path.Combine(dataDir, "Dawnguard.bsa"));
        if (plugin == "dragonborn.esm") archives.Add(Path.Combine(dataDir, "Dragonborn.bsa"));
        var folder = $@"sound\voice\{plugin}\{voice.ToLowerInvariant()}\";
        foreach (var arc in archives.Where(File.Exists))
        {
            var reader = Archive.CreateReader(GameRelease.SkyrimSE, arc, new SharedFileSystem());
            var files = reader.Files.Select(f => (Key: f.Path.Replace('/', '\\').ToLowerInvariant(), File: f))
                .Where(f => f.Key.StartsWith(folder, StringComparison.Ordinal) && f.Key.EndsWith(".fuz", StringComparison.Ordinal))
                .OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
            if (files.Count == 0) continue;
            // une réplique de follower si la voix en a, sinon une réplique générique, sinon la première
            var pick = files.FirstOrDefault(f => f.Key.Contains(@"\dialoguefollower"));
            if (pick.File == null) pick = files.FirstOrDefault(f => f.Key.Contains(@"\dialoguegeneric"));
            if (pick.File == null) pick = files[0];
            var data = pick.File.GetBytes();
            if (data.Length < 12 || data[0] != 'F' || data[1] != 'U' || data[2] != 'Z' || data[3] != 'E')
                throw new GfException($"{pick.Key} : pas un fichier .fuz");
            int lipSize = BitConverter.ToInt32(data, 8);
            return (pick.Key, data[(12 + lipSize)..]);
        }
        throw new GfException($"aucune réplique pour {voice} dans {string.Join(", ", archives.Select(Path.GetFileName))}");
    }

    // ------------------------------------------------------------------ lecture par Windows (XAudio2)

    static readonly object gate = new();
    static IXAudio2? engine;
    static IXAudio2MasteringVoice? master;
    static IXAudio2SourceVoice? playing;
    static AudioBuffer? playingBuffer;

    /// <summary>Joue l'audio xWMA (fichier RIFF « XWMA » : fmt, dpds, data) sur la sortie son de Windows ; arrête l'extrait
    /// précédent. Rend la durée en secondes.</summary>
    public static double Play(byte[] xwm)
    {
        var (fmt, dpds, audio) = ParseXwma(xwm);
        var format = WaveFormat.MarshalFrom(fmt);
        lock (gate)
        {
            StopLocked();
            engine ??= XAudio2.XAudio2Create(ProcessorSpecifier.DefaultProcessor, true);
            master ??= engine.CreateMasteringVoice();
            playing = engine.CreateSourceVoice(format, true);
            playingBuffer = new AudioBuffer(audio, BufferFlags.EndOfStream);
            playing.SubmitSourceBuffer(playingBuffer, dpds).CheckError();
            playing.Start().CheckError();
        }
        // dernier compteur de la table des paquets = octets PCM décodés au total
        return dpds.Length > 0 && format.AverageBytesPerSecond > 0
            ? dpds[^1] / (double)(format.SampleRate * format.Channels * 2) : 0;
    }

    /// <summary>Échantillons déjà joués par l'extrait en cours (vérification).</summary>
    public static ulong SamplesPlayed { get { lock (gate) return playing?.State.SamplesPlayed ?? 0; } }

    static void StopLocked()
    {
        if (playing != null)
        {
            playing.Stop();
            playing.FlushSourceBuffers();
            playing.DestroyVoice();
            playing.Dispose();
            playing = null;
        }
        playingBuffer?.Dispose();
        playingBuffer = null;
    }

    /// <summary>Morceaux d'un fichier xWMA : en-tête WAVEFORMATEX (« fmt »), table des paquets (« dpds »), audio (« data »).</summary>
    static (byte[] Fmt, uint[] Dpds, byte[] Data) ParseXwma(byte[] d)
    {
        if (d.Length < 12 || System.Text.Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(d, 8, 4) != "XWMA")
            throw new GfException("audio de voix inattendu (pas du xWMA)");
        byte[]? fmt = null, data = null;
        uint[] dpds = [];
        int p = 12;
        while (p + 8 <= d.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(d, p, 4);
            int size = BitConverter.ToInt32(d, p + 4);
            if (size < 0 || p + 8 + (long)size > d.Length) size = d.Length - p - 8;
            var chunk = d.AsSpan(p + 8, size);
            if (id == "fmt ") fmt = chunk.ToArray();
            else if (id == "dpds")
            {
                dpds = new uint[size / 4];
                for (int i = 0; i < dpds.Length; i++) dpds[i] = BitConverter.ToUInt32(chunk[(i * 4)..]);
            }
            else if (id == "data") data = chunk.ToArray();
            p += 8 + size + (size & 1);
        }
        if (fmt == null || data == null) throw new GfException("audio de voix incomplet (xWMA sans fmt ou data)");
        return (fmt, dpds, data);
    }

    // ------------------------------------------------------------------ secours : WAV par ffmpeg

    /// <summary>xWMA → WAV mono 16 bits avec ffmpeg (GF_FFMPEG, sinon le PATH), comme voice_sample.py.</summary>
    public static void ToWav(byte[] xwm, string outWav)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("GF_FFMPEG") is { Length: > 0 } f ? f : FindOnPath("ffmpeg.exe");
        if (ffmpeg == null) throw new GfException("ffmpeg introuvable (PATH)");
        Directory.CreateDirectory(Path.GetDirectoryName(outWav)!);
        var tmp = outWav + ".xwm";
        File.WriteAllBytes(tmp, xwm);
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var a in new[] { "-y", "-loglevel", "error", "-i", tmp, "-ac", "1", "-c:a", "pcm_s16le", outWav }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(outWav)) throw new GfException("ffmpeg : " + err.Result.Trim());
        }
        finally { File.Delete(tmp); }
    }

    static string? FindOnPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(d => Path.Combine(d.Trim().Trim('"'), exe)).FirstOrDefault(File.Exists);
}
