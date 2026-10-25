using System.Globalization;
using System.Text;
using TaskbarVisu.Audio;

namespace TaskbarVisu.Dsp;

/// <summary>
/// Diagnostic hors ligne : passe un fichier audio (ou un morceau synthétique) dans la même chaîne
/// d'analyse que l'accordeur, en accéléré.
/// --analyze rapport fichier…   : description détaillée de chaque fichier ;
/// --bench rapport csv dossier   : banc de test avec réponses attendues (voir tests/key-bench.csv) ;
/// --notetrace rapport source    : notes entendues image par image (piano roll).
/// </summary>
internal static class OfflineAnalysis
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>
    /// Résultat d'une analyse. Pour un morceau entier (plus d'une minute), Shares donne la part du temps d'affichage
    /// de chaque tonalité et Key la plus affichée ; pour un extrait court, Key est la tonalité finale.
    /// </summary>
    internal sealed record Result(string Key, bool Ambiguous, float Confidence, double Tuning, string Report,
        IReadOnlyDictionary<string, float>? Shares = null, double Seconds = 0);

    /// <summary>Au-delà, un fichier est jugé comme un morceau entier (part du temps), avec la mémoire par défaut de l'accordeur.</summary>
    private const double LongFormSeconds = 60;
    private static int DetailCount => int.TryParse(Environment.GetEnvironmentVariable("KEY_DETAIL"), out var n) ? n : 2;
    private static double DefaultMemorySeconds => Modules.ModuleCatalog.Find("tuner")?.Option("keyMemory")?.Default ?? 30;

    /// <summary>Source audio : remplit un tampon stéréo entrelacé, renvoie le nombre de trames (0 = fin).</summary>
    private delegate int StereoSource(float[] stereo, int frames);

    public static string Analyze(string path, double memorySeconds) =>
        (path.StartsWith("synth:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("voice:", StringComparison.OrdinalIgnoreCase)
            ? AnalyzeSynthetic(path, 30, memorySeconds)
            : AnalyzeFile(path, memorySeconds)).Report;

    /// <summary>WAV lu directement ; MP3, M4A, FLAC… décodés par Media Foundation (Windows).</summary>
    private static NAudio.Wave.WaveStream OpenAudio(string path) =>
        Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new NAudio.Wave.WaveFileReader(path)
            : new NAudio.Wave.MediaFoundationReader(path);

    private static Result AnalyzeFile(string path, double memorySeconds)
    {
        using var file = OpenAudio(path);
        var reader = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(file);
        int channels = reader.WaveFormat.Channels;
        float[] input = Array.Empty<float>();
        int Read(float[] stereo, int frames)
        {
            if (input.Length < frames * channels) input = new float[frames * channels];
            int got = reader.Read(input, 0, frames * channels) / channels;
            for (int f = 0; f < got; f++)
            {
                stereo[f * 2] = input[f * channels];
                stereo[f * 2 + 1] = input[f * channels + (channels > 1 ? 1 : 0)];
            }
            return got;
        }
        string label = $"{Path.GetFileName(path)}  ({file.TotalTime.TotalSeconds:0.0} s)";
        return Run(label, reader.WaveFormat.SampleRate, Read, memorySeconds);
    }

    /// <summary>
    /// Source synthétique : un morceau (synth:…), un son non musical (voice:…), ou un enchaînement avec durées
    /// en secondes, par exemple synth:C=10>silence=12>synth:F#m=15 (la durée totale est alors la somme).
    /// </summary>
    private static Result AnalyzeSynthetic(string spec, double seconds, double memorySeconds)
    {
        var parts = new List<(Func<double, (float L, float R)> Sample, double Start)>();
        double start = 0;
        foreach (var part in spec.Split('>'))
        {
            int eq = part.LastIndexOf('=');
            parts.Add((Source(eq > 0 ? part[..eq] : part), start));
            if (eq > 0) start += double.Parse(part[(eq + 1)..], Ci);
        }
        if (parts.Count > 1) seconds = start;

        const int rate = 48000;
        long n = 0, total = (long)(seconds * rate);
        int Read(float[] stereo, int frames)
        {
            int count = (int)Math.Min(frames, total - n);
            for (int f = 0; f < count; f++, n++)
            {
                double t = n / (double)rate;
                var current = parts.Last(p => p.Start <= t);
                var (l, r) = current.Sample(t - current.Start);
                stereo[f * 2] = l;
                stereo[f * 2 + 1] = r;
            }
            return count;
        }
        return Run($"{spec}  ({seconds:0} s)", rate, Read, memorySeconds);
    }

    /// <summary>Format : synth:Am:h[@désaccord][~bruit] (synth:E@-32, synth:F#m~0.3), voice:discord…, ou silence.</summary>
    private static Func<double, (float L, float R)> Source(string spec)
    {
        if (spec.Equals("silence", StringComparison.OrdinalIgnoreCase)) return _ => (0, 0);
        if (spec.StartsWith("voice:", StringComparison.OrdinalIgnoreCase))
            return (TestSounds.Parse(spec) ?? throw new ArgumentException($"Son inconnu : {spec}")).Sample;

        string body = spec["synth:".Length..];
        double noise = 0, detune = 0;
        int hash = body.IndexOf('~');
        if (hash >= 0) { noise = double.Parse(body[(hash + 1)..], Ci); body = body[..hash]; }
        int at = body.IndexOf('@');
        if (at >= 0) { detune = double.Parse(body[(at + 1)..], Ci); body = body[..at]; }
        var song = TestSong.Parse(body, detune) ?? throw new ArgumentException($"Tonalité inconnue : {spec}");
        song.Noise = noise;
        return song.Sample;
    }

    /// <summary>Seuil d'une note « allumée » dans le banc de test : à moins de 20 dB de la plus forte.</summary>
    private const float LitRatio = 0.1f;
    /// <summary>Notes du clavier le plus large du piano roll (Do1 – Si6).</summary>
    private const int LowestKey = 24, HighestKey = 95;

    /// <summary>
    /// Accord tenu (chord:… ou notes:…) : les ensembles de notes allumées (sur le clavier, après 1 s)
    /// et la part du temps de chacun ; le plus fréquent sert de réponse.
    /// </summary>
    private static Result AnalyzeNotes(string spec, double seconds)
    {
        var chord = TestChord.Parse(spec) ?? throw new ArgumentException($"Accord inconnu : {spec}");
        var counts = new Dictionary<string, int>();
        var levels = new double[128];
        int frames = 0;
        Simulate(chord.Sample, seconds, (t, keys) =>
        {
            if (t < 1) return;
            frames++;
            float max = keys.Notes.Max();
            var lit = new List<string>();
            for (int m = LowestKey; m <= HighestKey; m++)
            {
                if (max > 0) levels[m] += keys.Notes[m] / max;
                if (max > 0 && keys.Notes[m] >= max * LitRatio) lit.Add(TestChord.NoteName(m));
            }
            string label = lit.Count == 0 ? "--" : string.Join(" ", lit);
            counts[label] = counts.GetValueOrDefault(label) + 1;
        });
        var ranked = counts.OrderByDescending(kv => kv.Value).ToList();
        string top = ranked.Count > 0 ? ranked[0].Key : "--";
        float share = frames > 0 && ranked.Count > 0 ? ranked[0].Value / (float)frames : 0;
        var average = Enumerable.Range(0, 128).Where(m => levels[m] / Math.Max(1, frames) > 0.03)
            .Select(m => $"{TestChord.NoteName(m)}:{20 * Math.Log10(levels[m] / Math.Max(1, frames)):0}dB");
        string report = $"== {spec}{Environment.NewLine}  répartition : " +
                        string.Join("  ", ranked.Take(5).Select(kv => $"[{kv.Key}]={kv.Value * 100 / Math.Max(1, frames)}%")) + Environment.NewLine +
                        "  niveau moyen : " + string.Join(" ", average) + Environment.NewLine;
        var shares = counts.ToDictionary(kv => kv.Key, kv => kv.Value / (float)Math.Max(1, frames));
        return new Result(top, false, share, 0, report, shares);
    }

    /// <summary>
    /// Diagnostic : trace image par image des notes entendues (niveau en dB sous la plus forte) sur un morceau (synth:…)
    /// ou un accord (chord:…, notes:…) synthétique. KEY_PEAKS=1 ajoute les pics bruts.
    /// </summary>
    public static string NoteTrace(string spec, double seconds)
    {
        Func<double, (float L, float R)> sample = spec.StartsWith("synth:", StringComparison.OrdinalIgnoreCase)
            ? (TestSong.Parse(spec["synth:".Length..], 0) ?? throw new ArgumentException(spec)).Sample
            : (TestChord.Parse(spec) ?? throw new ArgumentException(spec)).Sample;
        var sb = new StringBuilder();
        if (Environment.GetEnvironmentVariable("KEY_PEAKS") == "1") KeyAnalyzer.DebugPeaks = new StringBuilder();
        Simulate(sample, seconds, (t, keys) =>
        {
            float max = keys.Notes.Max();
            var notes = Enumerable.Range(0, 128).Where(m => max > 0 && keys.Notes[m] >= max * 0.03f)
                .Select(m => $"{TestChord.NoteName(m)}:{20 * MathF.Log10(keys.Notes[m] / max):0}");
            sb.AppendLine($"{t,5:0.00}s accordage={keys.TuningCents:+0;-0}  {string.Join(" ", notes)}");
            if (KeyAnalyzer.DebugPeaks is { } peaks) { sb.Append("   ").Append(peaks); peaks.Clear(); }
        });
        return sb.ToString();
    }

    /// <summary>Fait passer une source synthétique dans l'analyse, image par image (60 i/s) ; appelle onFrame à chaque bloc analysé.</summary>
    private static void Simulate(Func<double, (float L, float R)> sample, double seconds, Action<double, KeyAnalyzer> onFrame)
    {
        const int rate = 48000;
        var ring = new SampleRing(1 << 17);
        ring.Reset(rate);
        var analyzer = new AudioAnalyzer(ring);
        var keys = analyzer.Harmony;
        const double dt = 1 / 60.0;
        int perFrame = (int)(rate * dt);
        var stereo = new float[perFrame * 2];
        long n = 0;
        for (double t = 0; t < seconds; t += dt)
        {
            for (int f = 0; f < perFrame; f++, n++)
            {
                var (l, r) = sample(n / (double)rate);
                stereo[f * 2] = l;
                stereo[f * 2 + 1] = r;
            }
            ring.Write(stereo, perFrame);
            analyzer.Process(dt);
            if (keys.Update(analyzer, dt)) onFrame(t, keys);
        }
    }

    private static Result Run(string label, int rate, StereoSource source, double memorySeconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"== {label}, {rate} Hz, mémoire {memorySeconds} s");

        var ring = new SampleRing(1 << 17);
        ring.Reset(rate);
        var analyzer = new AudioAnalyzer(ring);
        var keys = analyzer.Harmony;
        keys.KeyMemorySeconds = memorySeconds;

        const double dt = 1 / 60.0;
        int perFrame = (int)(rate * dt);
        var stereo = new float[perFrame * 2];
        double t = 0, nextReport = 2;
        var timeline = new List<string>();
        List<float> tonalness = new(), stability = new(), presence = new();
        bool shown = false;
        var displayed = new Dictionary<string, double>();
        var centres = new Dictionary<string, double>();
        bool slotted = Environment.GetEnvironmentVariable("KEY_SLOTS") == "1";
        double[] slotNotes = new double[12], slotBass = new double[12];
        var slots = new List<string>();
        double nextSlot = 20;

        while (true)
        {
            int frames = source(stereo, perFrame);
            if (frames == 0) break;
            ring.Write(stereo, frames);
            analyzer.Process(dt);
            if (keys.Update(analyzer, dt) && keys.Tonal)
            {
                tonalness.Add(keys.Tonalness);
                stability.Add(keys.Persistence);
            }
            if (t >= 5) presence.Add((float)keys.MusicPresence);
            if (slotted)
            {
                // KEY_SLOTS=1 : notes jouées et basse dominantes par tranches de 20 s (pour juger les sections d'un morceau).
                for (int m = 0; m < 128; m++) slotNotes[m % 12] += Math.Sqrt(keys.Notes[m]);
                for (int i = 0; i < 12; i++) slotBass[i] += keys.BassShort[i];
                if (t >= nextSlot)
                {
                    nextSlot += 20;
                    string Top(double[] v) => string.Join(" ", Enumerable.Range(0, 12).OrderByDescending(i => v[i]).Take(7)
                        .Select(i => $"{Harmony.NoteName(i, false)}{v[i] * 100 / Math.Max(1e-9, v.Max()):0}"));
                    slots.Add($"  {t - 20:0}-{t:0} s  notes {Top(slotNotes)}  |  basse {Top(slotBass)}");
                    Array.Clear(slotNotes);
                    Array.Clear(slotBass);
                }
            }
            if (keys.KeyActive)
            {
                shown = true;
                string name = Name(keys.KeyTonic, keys.KeyMinor);
                displayed[name] = displayed.GetValueOrDefault(name) + dt;
                string centre = keys.Centre < 0 ? "--" : CentreLabel(keys);
                centres[centre] = centres.GetValueOrDefault(centre) + dt;
            }
            t += dt;
            if (t >= nextReport)
            {
                nextReport += 2;
                string state = keys.KeyTonic < 0 ? "--" : $"{Name(keys.KeyTonic, keys.KeyMinor)}{(keys.ModeAmbiguous ? "?" : "")}({keys.KeyConfidence * 100:0})" +
                               (keys.Centre >= 0 ? ">" + CentreLabel(keys).Replace(' ', '.') : "");
                timeline.Add(keys.KeyTonic >= 0 && !keys.KeyActive ? $"[{state}]" : state);
            }
        }

        foreach (var s in slots) sb.AppendLine(s);
        sb.AppendLine("  toutes les 2 s (entre crochets = masquée, pas de musique) : " + string.Join(" ", timeline));
        sb.AppendLine($"  signal tonal : {tonalness.Count} blocs, tonalité p10/p50/p90 {Percentiles(tonalness)}, persistance {Percentiles(stability)}, musique p10/p50/p90 {Percentiles(presence)} max {(presence.Count > 0 ? presence.Max() : 0):0.00}");
        double displayedTotal = displayed.Values.Sum();
        var shares = displayed.ToDictionary(kv => kv.Key, kv => (float)(kv.Value / Math.Max(1e-9, displayedTotal)));
        sb.AppendLine($"  affichage : {displayedTotal * 100 / Math.Max(1e-9, t):0} % du temps ; " +
                      string.Join("  ", shares.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} {kv.Value * 100:0} %")));
        sb.AppendLine("  note centrale (-- = tonique) : " +
                      string.Join("  ", centres.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} {kv.Value * 100 / Math.Max(1e-9, displayedTotal):0} %")));
        string key = "--";
        if (keys.KeyTonic >= 0)
        {
            // Une tonalité jamais affichée (pas assez de musique tenue) compte comme « -- ».
            key = shown ? Name(keys.KeyTonic, keys.KeyMinor) : "--";
            var ranked = Enumerable.Range(0, 24).OrderByDescending(k => keys.KeyScores[k]).Take(4)
                .Select(k => $"{Name(k % 12, k >= 12)}={keys.KeyScores[k].ToString("0.000", Ci)}");
            sb.AppendLine($"  final : {key}{(keys.ModeAmbiguous ? " (mode incertain)" : "")} ({Harmony.Camelot(keys.KeyTonic, keys.KeyMinor)}), " +
                          $"confiance {keys.KeyConfidence * 100:0} %, accordage {keys.TuningCents.ToString("+0.0;-0.0", Ci)} cents, " +
                          $"tierces m/M {keys.Thirds.Minor:0.00}/{keys.Thirds.Major:0.00}");
            sb.AppendLine("  classement : " + string.Join("  ", ranked));
            foreach (int k in Enumerable.Range(0, 24).OrderByDescending(k => keys.KeyScores[k]).Take(DetailCount))
                sb.AppendLine($"  détail {Name(k % 12, k >= 12),-4}: {keys.ScoreBreakdown(k)}");
            sb.AppendLine("  chroma long : " + Describe(keys.LongChroma));
            sb.AppendLine("  chroma pur  : " + Describe(keys.PureLong));
            sb.AppendLine("  basse       : " + Describe(keys.BassLong));
        }
        if (t > LongFormSeconds)
        {
            var top = shares.OrderByDescending(kv => kv.Value).FirstOrDefault();
            return new Result(top.Key ?? "--", false, top.Value, keys.TuningCents, sb.ToString(), shares, t);
        }
        return new Result(key, keys.ModeAmbiguous, keys.KeyConfidence, keys.TuningCents, sb.ToString(), null, t);
    }

    /// <summary>
    /// Banc de test : chaque ligne du CSV donne une source (fichier du dossier, « synth:… », ou un accord
    /// « chord:… » / « notes:… »), les réponses acceptées (séparées par |), l'ambiguïté attendue (oui / non / *)
    /// et une justification. Pour un accord, la réponse est la liste des notes allumées, stable 80 % du temps ;
    /// pour un morceau entier, les tonalités acceptées doivent être affichées 70 % du temps où une tonalité l'est.
    /// Les fichiers sont cherchés dans les dossiers donnés, dans l'ordre.
    /// </summary>
    public static string Bench(string csvPath, IReadOnlyList<string> folders)
    {
        var sb = new StringBuilder();
        int passed = 0, total = 0, skipped = 0;
        foreach (var raw in File.ReadAllLines(csvPath, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var cols = line.Split(';');
            if (cols.Length < 3) continue;
            string source = cols[0].Trim();
            var accepted = cols[1].Split('|').Select(s => s.Trim()).ToArray();
            string ambiguity = cols[2].Trim();
            total++;

            bool notes = source.StartsWith("chord:", StringComparison.OrdinalIgnoreCase) ||
                         source.StartsWith("notes:", StringComparison.OrdinalIgnoreCase);
            Result result;
            try
            {
                result = notes ? AnalyzeNotes(source, 8)
                    : source.StartsWith("synth:", StringComparison.OrdinalIgnoreCase) || source.StartsWith("voice:", StringComparison.OrdinalIgnoreCase)
                        ? AnalyzeSynthetic(source, 25, 60)
                    : AnalyzeBenchFile(source, folders);
            }
            catch (FileNotFoundException)
            {
                // Fichiers audio non fournis avec le dépôt (droits d'auteur) : la ligne est ignorée, pas comptée.
                total--;
                skipped++;
                sb.AppendLine($"--  {source} : fichier absent, ignoré");
                continue;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"ERREUR {source} : {ex.Message}");
                continue;
            }

            // Accord : les ensembles acceptés doivent couvrir 80 % du temps.
            bool keyOk = notes ? accepted.Sum(a => result.Shares!.GetValueOrDefault(a)) >= 0.8f
                : result.Shares is { } shares ? accepted.Sum(a => shares.GetValueOrDefault(a)) >= 0.7f
                : accepted.Contains(result.Key);
            bool ambOk = ambiguity switch
            {
                "oui" => result.Ambiguous,
                "non" => !result.Ambiguous,
                _ => true,
            };
            bool ok = keyOk && ambOk;
            if (ok) passed++;
            sb.AppendLine($"{(ok ? "OK " : "XX ")} {source,-42} attendu {string.Join("|", accepted),-8} ambigu={ambiguity,-3} " +
                          $"-> {result.Key}{(result.Ambiguous ? "?" : "")} ({result.Confidence * 100:0} %)" +
                          (!notes && result.Shares is { } s ? $"  [réf. {accepted.Sum(a => s.GetValueOrDefault(a)) * 100:0} %]" : ""));
            if (!ok) sb.Append(result.Report);
            else if (notes && result.Confidence < 0.95f) sb.AppendLine(result.Report.Split(Environment.NewLine)[1]);
        }
        sb.Insert(0, $"Score : {passed} / {total}{(skipped > 0 ? $" ({skipped} fichiers absents ignorés)" : "")}{Environment.NewLine}");
        return sb.ToString();
    }

    private static Result AnalyzeBenchFile(string name, IReadOnlyList<string> folders)
    {
        string path = folders.Select(f => Path.Combine(f, name)).FirstOrDefault(File.Exists)
                      ?? throw new FileNotFoundException("absent des dossiers donnés", name);
        double seconds;
        using (var probe = OpenAudio(path)) seconds = probe.TotalTime.TotalSeconds;
        // Morceau entier : mémoire par défaut de l'accordeur (ce que l'on voit à l'écran) ; extrait : mémoire longue.
        return AnalyzeFile(path, seconds > LongFormSeconds ? DefaultMemorySeconds : 60);
    }

    private static string Percentiles(List<float> values)
    {
        if (values.Count == 0) return "-";
        var sorted = values.Order().ToArray();
        float At(double q) => sorted[(int)Math.Min(sorted.Length - 1, q * sorted.Length)];
        return string.Create(Ci, $"{At(0.1):0.00}/{At(0.5):0.00}/{At(0.9):0.00}");
    }

    private static string Describe(float[] chroma)
    {
        float max = Math.Max(1e-9f, chroma.Max());
        return string.Join(" ", chroma.Select((v, i) => $"{Harmony.NoteName(i, false)}:{v / max:0.00}"));
    }

    /// <summary>
    /// Contrôle indépendant : énergie brute par classe de hauteur sur tout le fichier
    /// (FFT simple, sans séparation harmonique ni sélection de pics).
    /// </summary>
    public static string RawChroma(string path)
    {
        using var file = OpenAudio(path);
        var reader = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(file);
        int rate = reader.WaveFormat.SampleRate, channels = reader.WaveFormat.Channels;
        const int n = 16384;
        var fft = new Fft(n);
        var re = new float[n];
        var im = new float[n];
        var input = new float[n * channels];
        var chroma = new double[12];
        var magnitude = new double[12];
        var frame = new double[12];
        float binHz = (float)rate / n;
        while (reader.Read(input, 0, input.Length) is var read && read == input.Length)
        {
            for (int i = 0; i < n; i++)
            {
                float w = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (n - 1));
                re[i] = (input[i * channels] + input[i * channels + (channels > 1 ? 1 : 0)]) * 0.5f * w;
                im[i] = 0;
            }
            fft.Transform(re, im);
            Array.Clear(frame);
            for (int k = (int)(80 / binHz); k < (int)(4000 / binHz); k++)
            {
                double midi = 69 + 12 * Math.Log2(k * binHz / 440.0);
                int pc = (((int)Math.Round(midi) % 12) + 12) % 12;
                double power = re[k] * re[k] + im[k] * im[k];
                chroma[pc] += power;
                frame[pc] += Math.Sqrt(power);
            }
            for (int i = 0; i < 12; i++) magnitude[i] += frame[i];
        }
        double max = chroma.Max();
        return "  chroma brut : " + string.Join(" ", chroma.Select((v, i) => $"{Harmony.NoteName(i, false)}:{v / max:0.00}")) + Environment.NewLine +
               "  tonalité (brut, énergie)   : " + KeyAnalyzer.RankKeys(chroma, 3) + Environment.NewLine +
               "  tonalité (brut, amplitude) : " + KeyAnalyzer.RankKeys(magnitude, 3);
    }

    private static string CentreLabel(KeyAnalyzer keys) =>
        $"{Harmony.NoteName(keys.Centre, false, Harmony.KeyUsesFlats(keys.KeyTonic, keys.KeyMinor))} {Harmony.ModeName(keys.Centre, keys.KeyTonic, keys.KeyMinor)}";

    private static string Name(int tonic, bool minor) => Harmony.KeyName(tonic, minor, false) + (minor ? "m" : string.Empty);
}
