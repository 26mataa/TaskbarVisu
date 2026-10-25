namespace TaskbarVisu.Dsp;

/// <summary>
/// Analyse harmonique précise pour l'accordeur (note, tonalité) et le piano roll (notes jouées) :
/// 1. FFT haute résolution (16384 points, ~2,9 Hz par case à 48 kHz), fenêtre Blackman-Harris ;
/// 2. séparation harmonique / percussive : médiane temporelle des spectres (les coups de batterie disparaissent) ;
/// 3. pics tonaux interpolés, accordage global estimé (432 Hz, voix pitchée…) ;
/// 4. chromagramme HPCP avec contribution des harmoniques, chroma de basse séparé ;
/// 5. tonalité par vote de trois profils (Krumhansl-Kessler, Temperley, Albrecht-Shanahan)
///    + indice de basse pour départager majeur / mineur relatif, avec hystérésis et confiance ;
/// 6. notes réellement jouées, octave comprise, après élimination des harmoniques ;
/// 7. fréquence fondamentale par somme harmonique (évite les erreurs d'octave).
/// </summary>
internal sealed class KeyAnalyzer
{
    public const int FftSize = 16384;
    private const int MedianFrames = 5;
    private const int MaxPeaks = 80;
    private const double FrameInterval = 0.08;
    private static readonly float[] HarmonicWeights = Tunable("KEY_HARM", new[] { 1f, 0.6f, 0.36f, 0.22f });
    private static readonly float BassCorrWeight = Tunable("KEY_BASSCORR", new[] { 0.2f })[0];
    private static readonly float TriadWeight = Tunable("KEY_TRIAD", new[] { 0.12f })[0];
    private static readonly float BassTonicWeight = Tunable("KEY_BASSTONIC", new[] { 0.18f })[0];
    private static readonly float CompressExp = Tunable("KEY_COMPRESS", new[] { 0.5f })[0];

    /// <summary>Diagnostic : permet d'essayer d'autres réglages via des variables d'environnement (valeurs séparées par « ; »).</summary>
    private static float[] Tunable(string name, float[] fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        try
        {
            return raw.Split(';').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }
        catch { return fallback; }
    }

    // Profils de tonalité (degré 0 = tonique).
    private static readonly double[][] MajorProfiles =
    {
        new[] { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 },      // Krumhansl-Kessler
        new[] { 0.748, 0.060, 0.488, 0.082, 0.670, 0.460, 0.096, 0.715, 0.104, 0.366, 0.057, 0.400 }, // Temperley
        new[] { 0.238, 0.006, 0.111, 0.006, 0.137, 0.094, 0.016, 0.214, 0.009, 0.080, 0.008, 0.081 }, // Albrecht-Shanahan
    };
    private static readonly double[][] MinorProfiles =
    {
        new[] { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 },
        new[] { 0.712, 0.084, 0.474, 0.618, 0.049, 0.460, 0.105, 0.747, 0.404, 0.067, 0.133, 0.330 },
        new[] { 0.220, 0.006, 0.104, 0.123, 0.019, 0.103, 0.012, 0.214, 0.062, 0.022, 0.061, 0.052 },
    };
    // Fondamentales attendues dans la basse (musique actuelle) : la tonique d'abord, puis les degrés usuels
    // (IV, V, vi en majeur ; VI, VII, III, iv, v en mineur).
    private static readonly double[] MajorBass = { 1.0, 0, 0.3, 0, 0.2, 0.6, 0, 0.6, 0, 0.45, 0, 0.05 };
    private static readonly double[] MinorBass = { 1.0, 0, 0.1, 0.45, 0, 0.45, 0, 0.5, 0.55, 0, 0.5, 0.05 };

    private readonly Fft _fft = new(FftSize);
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _mono = new float[FftSize];
    private readonly float[] _re = new float[FftSize], _im = new float[FftSize];
    private readonly float[][] _history = new float[MedianFrames][];
    private readonly float[] _sort = new float[MedianFrames];
    private readonly float _windowNorm;
    private float[] _harmonic = Array.Empty<float>();
    private int _historyHead, _historyCount, _bins, _rate;
    private float _binHz;
    private double _elapsed;
    private long _audioFrame = -1;
    private bool _analyzed;

    private readonly float[] _peakFreq = new float[MaxPeaks], _peakAmp = new float[MaxPeaks];
    private int _peakCount;

    private double _tuneSin, _tuneCos, _tuneWeight, _tuneTime;
    private double _evidence;
    private int _candidate = -1;
    private double _candidateTime;

    private readonly float[] _prevKeys = new float[MaxPeaks];
    private int _prevCount;

    private readonly float[] _frame = new float[12], _bassFrame = new float[12], _pureFrame = new float[12];
    private readonly int[] _order = new int[MaxPeaks];
    private readonly float[] _orderKeys = new float[MaxPeaks];
    private readonly float[] _residual = new float[MaxPeaks];

    /// <summary>
    /// Notes réellement jouées au dernier bloc analysé, harmoniques écartées : amplitude par numéro MIDI
    /// (60 = Do4, 261,6 Hz ; accordage estimé pris en compte).
    /// </summary>
    public float[] Notes { get; } = new float[128];

    /// <summary>Durée sur laquelle la tonalité est estimée (réglage de l'accordeur).</summary>
    public double KeyMemorySeconds { get; set; } = 30;

    /// <summary>Chroma long « pur » : uniquement les notes réellement entendues (sans harmoniques déduites).</summary>
    public float[] PureLong { get; } = new float[12];

    /// <summary>Notes réellement jouées (harmoniques retirées), sur toute la tessiture et dans le grave : sert à trouver la note centrale.</summary>
    private readonly float[] _notesLong = new float[12], _lowNotesLong = new float[12];
    private readonly float[] _notesFrame = new float[12], _lowNotesFrame = new float[12];

    /// <summary>
    /// Le mode (majeur / mineur) de la tonalité affichée n'est pas tranché : tierce absente,
    /// tierces majeure et mineure comparables, ou tonalité parallèle presque à égalité.
    /// </summary>
    public bool ModeAmbiguous { get; private set; }

    /// <summary>Énergie relative (0..1) de la tierce mineure et de la tierce majeure de la tonique affichée.</summary>
    public (float Minor, float Major) Thirds { get; private set; }

    // Présence de musique : seule une musique tenue alimente la tonalité. Chaque bloc reçoit un score (pics stables
    // × énergie concentrée dans des pics), lissé sur ~1,5 s ; la musique commence au-dessus de 0,45 et dure tant que
    // le score lissé reste au-dessus de 0,30 (passages très chantés). Mesuré : voix (Discord, bavardage), toux et
    // notification ≤ 0,39 ; musique ≥ 0,43 neuf fois sur dix (≥ 0,63 hors pop très chantée).
    private const double PresenceTau = 1.5, MusicStart = 0.45, MusicHold = 0.30;
    private const double HideAfter = 3, ForgetAfter = 10;
    private double _presence, _sinceMusic = double.MaxValue, _pendingEvidence;
    private bool _inMusic;
    private readonly float[] _pendingFrame = new float[12], _pendingBass = new float[12], _pendingPure = new float[12];

    /// <summary>Indice de musique tenue (0..1), lissé sur ~1,5 s.</summary>
    public double MusicPresence => _presence;

    /// <summary>
    /// La tonalité est à afficher : elle est établie et de la musique a été entendue il y a moins de 3 s.
    /// Sinon l'accordeur reste neutre (silence, conversation sur Discord, toux, notification…).
    /// </summary>
    public bool KeyActive => KeyTonic >= 0 && _sinceMusic <= HideAfter;

    public KeyAnalyzer()
    {
        double sum = 0;
        for (int i = 0; i < FftSize; i++)
        {
            double x = 2 * Math.PI * i / (FftSize - 1);
            _window[i] = (float)(0.35875 - 0.48829 * Math.Cos(x) + 0.14128 * Math.Cos(2 * x) - 0.01168 * Math.Cos(3 * x));
            sum += _window[i];
        }
        _windowNorm = (float)(2.0 / sum);
    }

    /// <summary>Chromagrammes normalisés (C = 0 … B = 11).</summary>
    public float[] ShortChroma { get; } = new float[12];
    public float[] LongChroma { get; } = new float[12];
    public float[] BassShort { get; } = new float[12];
    public float[] BassLong { get; } = new float[12];

    /// <summary>Écart d'accordage global estimé, en cents par rapport à La 440 Hz.</summary>
    public double TuningCents { get; private set; }
    public float Reference => (float)(440 * Math.Pow(2, TuningCents / 1200));

    /// <summary>Scores des 24 tonalités : index = tonique (+12 si mineur).</summary>
    public float[] KeyScores { get; } = new float[24];
    public int KeyTonic { get; private set; } = -1;
    public bool KeyMinor { get; private set; }
    public float KeyConfidence { get; private set; }

    /// <summary>Le dernier bloc analysé contenait de la musique tonale audible.</summary>
    public bool Tonal { get; private set; }

    /// <summary>Part de l'énergie concentrée dans des pics (×3) au dernier bloc : bruit ≈ 0, notes tenues ≈ 1+.</summary>
    public float Tonalness { get; private set; }

    /// <summary>Part de l'énergie des pics déjà présents (même fréquence à ±0,4 %) au bloc précédent : notes tenues ≈ 1, voix qui glisse ≈ 0.</summary>
    public float Persistence { get; private set; }


    public bool HasPitch { get; private set; }
    public float PitchHz { get; private set; }
    public float PitchDb { get; private set; }

    public void Reset()
    {
        Array.Clear(ShortChroma);
        Array.Clear(BassShort);
        Array.Clear(Notes);
        ForgetKey();
        _historyCount = 0;
        _prevCount = 0;
        _presence = 0;
        _inMusic = false;
        _sinceMusic = double.MaxValue;
    }

    /// <summary>
    /// À appeler à chaque image ; analyse un nouveau bloc toutes les ~80 ms. Renvoie true si un bloc a été analysé.
    /// Plusieurs modules peuvent l'appeler pour la même image : seul le premier appel fait le travail.
    /// </summary>
    public bool Update(AudioAnalyzer audio, double dt)
    {
        if (audio.Frame == _audioFrame) return _analyzed;
        _audioFrame = audio.Frame;
        _analyzed = Analyze(audio, dt);
        return _analyzed;
    }

    private bool Analyze(AudioAnalyzer audio, double dt)
    {
        _elapsed += dt;
        if (_elapsed < FrameInterval) return false;
        double frameDt = Math.Min(_elapsed, 0.5);
        _elapsed = 0;

        if (audio.SampleRate != _rate) Configure(audio.SampleRate);

        if (audio.SilentSeconds > 0.3)
        {
            Tonal = false;
            HasPitch = false;
            Decay(ShortChroma, frameDt, 0.5);
            Decay(BassShort, frameDt, 0.5);
            Array.Clear(Notes);
            _prevCount = 0;
            UpdatePresence(0, frameDt);
            return true;
        }

        Spectrum(audio);
        PickPeaks(out float tonalness, out double totalEnergy);
        Tonal = totalEnergy > 1e-9 && _peakCount > 0;
        if (!Tonal)
        {
            HasPitch = false;
            Array.Clear(Notes);
            _prevCount = 0;
            UpdatePresence(0, frameDt);
            return true;
        }

        UpdateTuning(frameDt);
        BuildChroma();
        Tonalness = tonalness;
        Persistence = PeakPersistence();
        // Score du bloc : pics qui restent en place (la voix glisse, une toux n'a pas de pics) et énergie concentrée dans
        // des pics (pas du bruit). Il pondère aussi la contribution du bloc à la tonalité.
        float score = Math.Clamp((Persistence - 0.5f) / 0.35f, 0, 1) * Math.Clamp((tonalness - 0.6f) / 0.4f, 0, 1);
        bool music = UpdatePresence(score, frameDt);
        float weight = Math.Clamp(tonalness * 3f, 0.15f, 1f) * score;
        // Pendant la ~1 s nécessaire pour confirmer qu'il s'agit de musique, les blocs sont mis de côté puis comptés
        // dès la confirmation : le premier accord du morceau n'est pas perdu.
        if (music) CommitPending();
        else if (score > 0) HoldPending(weight, frameDt);
        else if (_presence < 0.15) ClearPending();
        Accumulate(frameDt, KeyMemorySeconds, weight, music);
        if (music) EstimateKey(frameDt);
        DetectPitch();
        return true;
    }

    /// <summary>
    /// Met à jour l'indice de musique tenue et renvoie true si ce bloc doit compter pour la tonalité.
    /// Après 10 s sans musique, la tonalité est oubliée : le morceau suivant repart de zéro.
    /// </summary>
    private bool UpdatePresence(float score, double dt)
    {
        _presence += (score - _presence) * (1 - Math.Exp(-dt / PresenceTau));
        _inMusic = _presence >= (_inMusic ? MusicHold : MusicStart);
        bool music = _inMusic;
        if (music)
        {
            _sinceMusic = 0;
        }
        else if (_sinceMusic < double.MaxValue)
        {
            _sinceMusic += dt;
            if (_sinceMusic > ForgetAfter)
            {
                ForgetKey();
                _sinceMusic = double.MaxValue;
            }
        }
        return music;
    }

    private void HoldPending(float weight, double dt)
    {
        for (int i = 0; i < 12; i++)
        {
            _pendingFrame[i] += _frame[i] * weight * (float)dt;
            _pendingBass[i] += _bassFrame[i] * weight * (float)dt;
            _pendingPure[i] += _pureFrame[i] * weight * (float)dt;
        }
        _pendingEvidence += weight * dt;
    }

    private void CommitPending()
    {
        if (_pendingEvidence <= 0) return;
        for (int i = 0; i < 12; i++)
        {
            LongChroma[i] += _pendingFrame[i];
            BassLong[i] += _pendingBass[i];
            PureLong[i] += _pendingPure[i];
        }
        _evidence += _pendingEvidence;
        ClearPending();
    }

    private void ClearPending()
    {
        Array.Clear(_pendingFrame);
        Array.Clear(_pendingBass);
        Array.Clear(_pendingPure);
        _pendingEvidence = 0;
    }

    private void ForgetKey()
    {
        ClearPending();
        Array.Clear(LongChroma);
        Array.Clear(BassLong);
        Array.Clear(PureLong);
        Array.Clear(_notesLong);
        Array.Clear(_lowNotesLong);
        ModeAmbiguous = false;
        _evidence = 0;
        KeyTonic = -1;
        KeyConfidence = 0;
        _candidate = -1;
        _centre = _centreCandidate = _centreKey = -1;
    }

    private void Configure(int sampleRate)
    {
        _rate = sampleRate;
        _binHz = (float)sampleRate / FftSize;
        _bins = Math.Min(FftSize / 2, (int)MathF.Ceiling(5300 / _binHz) + 4);
        for (int i = 0; i < MedianFrames; i++) _history[i] = new float[_bins];
        _harmonic = new float[_bins];
        Reset();
    }

    private void Spectrum(AudioAnalyzer audio)
    {
        audio.CopyMono(_mono);
        for (int i = 0; i < FftSize; i++)
        {
            _re[i] = _mono[i] * _window[i];
            _im[i] = 0;
        }
        _fft.Transform(_re, _im);

        var current = _history[_historyHead];
        for (int k = 0; k < _bins; k++)
            current[k] = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * _windowNorm;
        _historyHead = (_historyHead + 1) % MedianFrames;
        if (_historyCount < MedianFrames) _historyCount++;

        // Médiane temporelle : les sons tenus restent, les transitoires (batterie) sont écartés.
        int n = _historyCount;
        for (int k = 0; k < _bins; k++)
        {
            for (int f = 0; f < n; f++) _sort[f] = _history[f][k];
            for (int a = 1; a < n; a++)
            {
                float v = _sort[a];
                int b = a - 1;
                while (b >= 0 && _sort[b] > v) { _sort[b + 1] = _sort[b]; b--; }
                _sort[b + 1] = v;
            }
            _harmonic[k] = _sort[n / 2];
        }
    }

    private void PickPeaks(out float tonalness, out double totalEnergy)
    {
        _peakCount = 0;
        int k0 = Math.Max(3, (int)MathF.Ceiling(40 / _binHz));
        int k1 = _bins - 3;
        float max = 0;
        totalEnergy = 0;
        for (int k = k0; k <= k1; k++)
        {
            float m = _harmonic[k];
            totalEnergy += m * m;
            if (m > max) max = m;
        }
        float threshold = Math.Max(max * 0.0018f, 1e-5f); // ~ -55 dB sous le pic le plus fort

        double peakEnergy = 0;
        for (int k = k0; k <= k1; k++)
        {
            float m = _harmonic[k];
            if (m < threshold || m <= _harmonic[k - 1] || m < _harmonic[k + 1] || m <= _harmonic[k - 2] || m < _harmonic[k + 2])
                continue;

            // Interpolation parabolique (en dB) : fréquence et amplitude précises.
            float a = Db(_harmonic[k - 1]), b = Db(m), c = Db(_harmonic[k + 1]);
            float denom = a - 2 * b + c;
            float p = Math.Abs(denom) > 1e-9f ? Math.Clamp(0.5f * (a - c) / denom, -0.5f, 0.5f) : 0;
            float freq = (k + p) * _binHz;
            float amp = MathF.Pow(10, (b - 0.25f * (a - c) * p) / 20f);
            peakEnergy += amp * amp;
            AddPeak(freq, amp);
        }
        tonalness = totalEnergy > 0 ? (float)(peakEnergy * 3 / totalEnergy) : 0;
    }

    private void AddPeak(float freq, float amp)
    {
        if (_peakCount < MaxPeaks)
        {
            _peakFreq[_peakCount] = freq;
            _peakAmp[_peakCount] = amp;
            _peakCount++;
            return;
        }
        int weakest = 0;
        for (int i = 1; i < MaxPeaks; i++) if (_peakAmp[i] < _peakAmp[weakest]) weakest = i;
        if (amp <= _peakAmp[weakest]) return;
        _peakFreq[weakest] = freq;
        _peakAmp[weakest] = amp;
    }

    /// <summary>Accordage : moyenne circulaire des écarts des pics à la grille tempérée (440 Hz).</summary>
    private void UpdateTuning(double dt)
    {
        double decay = Math.Exp(-dt / 15.0);
        double s = 0, c = 0, total = 0;
        for (int i = 0; i < _peakCount; i++)
        {
            float f = _peakFreq[i];
            if (f < 100 || f > 3000) continue;
            double cents = 1200 * Math.Log2(f / 440.0);
            double deviation = cents - 100 * Math.Round(cents / 100);
            double angle = 2 * Math.PI * deviation / 100;
            double w = _peakAmp[i];
            s += w * Math.Sin(angle);
            c += w * Math.Cos(angle);
            total += w;
        }
        _tuneSin = _tuneSin * decay + s;
        _tuneCos = _tuneCos * decay + c;
        _tuneWeight = _tuneWeight * decay + total;
        _tuneTime += dt;
        double length = Math.Sqrt(_tuneSin * _tuneSin + _tuneCos * _tuneCos);
        if (length < 1e-12 || _tuneWeight < 1e-12) return;

        // On n'applique l'accordage estimé que s'il est cohérent (les pics s'accordent sur le même décalage)
        // et établi sur quelques secondes ; au début, une estimation instable ferait « baver » les notes.
        double coherence = length / _tuneWeight;
        double trust = Math.Clamp((coherence - 0.35) / 0.35, 0, 1) * Math.Clamp(_tuneTime / 2.0, 0, 1);
        TuningCents = Math.Atan2(_tuneSin, _tuneCos) / (2 * Math.PI) * 100 * trust;
    }

    /// <summary>HPCP : chaque pic alimente sa classe de hauteur et celles de ses fondamentales possibles.</summary>
    private void BuildChroma()
    {
        Array.Clear(_frame);
        Array.Clear(_bassFrame);
        Array.Clear(_pureFrame);
        double reference = Reference;
        for (int i = 0; i < _peakCount; i++)
        {
            float f = _peakFreq[i], amp = _peakAmp[i];
            float freqWeight = f < 60 ? 0.5f : f > 2500 ? Math.Max(0.3f, 1f - (f - 2500) / 3000f) : 1f;
            float compressed = MathF.Pow(amp, CompressExp) * freqWeight;
            for (int h = 1; h <= HarmonicWeights.Length; h++)
            {
                double f0 = f / h;
                if (f0 < 45) break;
                Spread(_frame, f0, reference, compressed * HarmonicWeights[h - 1]);
            }
            if (f >= 40 && f <= 260) Spread(_bassFrame, f, reference, amp);
            // Chroma « pur » (sans harmoniques déduites) : sert à vérifier si la tierce est réellement jouée.
            if (f >= 80 && f <= 4000) Spread(_pureFrame, f, reference, amp * freqWeight);
        }
        Normalize(_frame);
        Normalize(_bassFrame);
        Normalize(_pureFrame);
        BuildNotes(reference);
        Array.Clear(_notesFrame);
        Array.Clear(_lowNotesFrame);
        for (int m = 0; m < 128; m++)
        {
            if (Notes[m] <= 0) continue;
            float v = MathF.Sqrt(Notes[m]);
            _notesFrame[m % 12] += v;
            if (m < 57 && m >= CentreLowestBass) _lowNotesFrame[m % 12] += v; // sous La3 (220 Hz) : la basse
        }
        Normalize(_notesFrame);
        Normalize(_lowNotesFrame);
    }

    private static void Spread(float[] chroma, double freq, double reference, float weight)
    {
        double semis = 12 * Math.Log2(freq / reference) + 9; // 0 = C
        double floor = Math.Floor(semis);
        float frac = (float)(semis - floor);
        int pc = (((int)floor % 12) + 12) % 12;
        chroma[pc] += weight * (1 - frac);
        chroma[(pc + 1) % 12] += weight * frac;
    }

    /// <summary>Chromas courts à chaque bloc ; la mémoire longue (tonalité) seulement pour la musique tenue, sinon figée.</summary>
    private void Accumulate(double dt, double memory, float weight, bool longTerm)
    {
        double decayLong = Math.Exp(-dt / Math.Max(2, memory));
        float kShort = 1f - (float)Math.Exp(-dt / 0.25);
        float kBass = 1f - (float)Math.Exp(-dt / 0.4);
        for (int i = 0; i < 12; i++)
        {
            ShortChroma[i] += (_frame[i] - ShortChroma[i]) * kShort;
            BassShort[i] += (_bassFrame[i] - BassShort[i]) * kBass;
            if (!longTerm) continue;
            LongChroma[i] = (float)(LongChroma[i] * decayLong + _frame[i] * weight * dt);
            BassLong[i] = (float)(BassLong[i] * decayLong + _bassFrame[i] * weight * dt);
            PureLong[i] = (float)(PureLong[i] * decayLong + _pureFrame[i] * weight * dt);
            _notesLong[i] = (float)(_notesLong[i] * decayLong + _notesFrame[i] * weight * dt);
            _lowNotesLong[i] = (float)(_lowNotesLong[i] * decayLong + _lowNotesFrame[i] * weight * dt);
        }
        if (longTerm) _evidence = _evidence * decayLong + weight * dt;
    }

    /// <summary>Diagnostic : détail du score d'une tonalité (chaque profil, basse, accord de tonique, tonique à la basse).</summary>
    public string ScoreBreakdown(int key)
    {
        int tonic = key % 12;
        bool minor = key >= 12;
        var all = minor ? MinorProfiles : MajorProfiles;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        float chromaMax = Math.Max(1e-9f, LongChroma.Max()), bassMax = Math.Max(1e-9f, BassLong.Max());
        float triad = (LongChroma[tonic] + LongChroma[(tonic + (minor ? 3 : 4)) % 12] + LongChroma[(tonic + 7) % 12]) / (3 * chromaMax);
        return string.Join(" ", all.Select((p, i) => $"P{i}={Correlation(LongChroma, p, tonic).ToString("0.000", ci)}")) +
               $" basse={Correlation(BassLong, minor ? MinorBass : MajorBass, tonic).ToString("0.000", ci)}" +
               $" triade={triad.ToString("0.000", ci)} toniqueBasse={(BassLong[tonic] / bassMax).ToString("0.000", ci)}" +
               $" pur={Correlation(PureLong, all[0], tonic).ToString("0.000", ci)}";
    }

    private void EstimateKey(double dt)
    {
        if (_evidence < 1.5) return; // il faut un peu de musique avant de se prononcer

        int best = 0, second = 1;
        float chromaMax = Math.Max(1e-9f, LongChroma.Max());
        float bassMax = Math.Max(1e-9f, BassLong.Max());
        for (int key = 0; key < 24; key++)
        {
            int tonic = key % 12;
            bool minor = key >= 12;
            var profiles = minor ? MinorProfiles : MajorProfiles;
            double score = 0;
            foreach (var profile in profiles) score += Correlation(LongChroma, profile, tonic);
            score /= profiles.Length;
            score += BassCorrWeight * Correlation(BassLong, minor ? MinorBass : MajorBass, tonic);

            // Présence de l'accord de tonique et de la tonique à la basse : c'est ce qui départage
            // une tonalité de son relatif (Do majeur / La mineur ont exactement les mêmes notes).
            float triad = (LongChroma[tonic] + LongChroma[(tonic + (minor ? 3 : 4)) % 12] + LongChroma[(tonic + 7) % 12]) / (3 * chromaMax);
            score += TriadWeight * triad + BassTonicWeight * BassLong[tonic] / bassMax;
            KeyScores[key] = (float)score;
        }
        for (int key = 0; key < 24; key++)
        {
            if (KeyScores[key] > KeyScores[best]) { second = best; best = key; }
            else if (key != best && KeyScores[key] > KeyScores[second]) second = key;
        }

        int current = KeyTonic < 0 ? -1 : KeyTonic + (KeyMinor ? 12 : 0);
        if (current < 0)
        {
            if (_evidence >= 2.5) SetKey(best);
        }
        else if (best != current)
        {
            // Hystérésis adaptative : plus la nouvelle tonalité domine nettement, plus on bascule vite
            // (de 2,5 s pour un écart minime à 0,5 s pour un écart franc).
            if (best == _candidate) _candidateTime += dt;
            else { _candidate = best; _candidateTime = dt; }
            float lead = KeyScores[best] - KeyScores[current];
            double needed = Math.Clamp(2.5 - (lead - 0.015) * 20, 0.5, 2.5);
            if (lead > 0.015 && _candidateTime >= needed) SetKey(best);
        }
        else
        {
            _candidate = -1;
            _candidateTime = 0;
        }

        // Confiance : niveau de corrélation et avance sur la tonalité suivante.
        if (KeyTonic < 0) return;
        int shown = KeyTonic + (KeyMinor ? 12 : 0);
        float runnerUp = float.MinValue;
        for (int key = 0; key < 24; key++) if (key != shown && KeyScores[key] > runnerUp) runnerUp = KeyScores[key];
        float level = Math.Clamp((KeyScores[shown] - 0.3f) / 0.6f, 0, 1);
        float margin = Math.Clamp((KeyScores[shown] - runnerUp) / 0.1f, 0, 1);
        KeyConfidence = 0.45f * level + 0.55f * margin;
        UpdateModeAmbiguity(shown);
        UpdateCentre(dt);
    }

    // Note centrale (morceaux modaux) : là où la musique se pose, d'après la basse et les notes réellement jouées
    // (harmoniques retirées : la 3e harmonique d'un Do ne doit pas faire passer Sol pour le centre).
    // La basse compte à partir de La1 (55 Hz) : en dessous, la tenue d'une grosse caisse passerait pour une note.
    private const float CentreBassRatio = 1.25f, CentreNotesRatio = 1.1f;
    private const int CentreLowestBass = 33;
    private const double CentreHold = 3;
    private int _centre = -1, _centreCandidate = -1, _centreKey = -1;
    private double _centreCandidateTime;

    /// <summary>
    /// Note centrale quand elle diffère nettement de la tonique affichée (gamme de Si♭ majeur posée sur Fa,
    /// par exemple), sinon -1. Elle doit dominer la tonique dans la basse (+25 %) et dans les notes (+10 %),
    /// et tenir 3 s avant d'être affichée.
    /// </summary>
    public int Centre => _centre >= 0 && _centre != KeyTonic ? _centre : -1;

    private void UpdateCentre(double dt)
    {
        if (KeyTonic < 0) return;
        // Nouvelle tonalité : la note centrale est réévaluée sans attendre.
        int key = KeyTonic + (KeyMinor ? 12 : 0);
        if (key != _centreKey) { _centreKey = key; _centre = _centreCandidate = -1; }
        float lowMax = Math.Max(1e-9f, _lowNotesLong.Max()), notesMax = Math.Max(1e-9f, _notesLong.Max());
        float Low(int pc) => _lowNotesLong[pc] / lowMax;
        float All(int pc) => _notesLong[pc] / notesMax;

        // La note centrale doit dominer la tonique à la fois dans la basse et dans l'ensemble des notes : la quinte,
        // très présente partout (accords I et V), ne suffit pas à passer pour le centre.
        int candidate = KeyTonic;
        float bestWeight = 0;
        foreach (int pc in Harmony.Scale(KeyTonic, KeyMinor))
        {
            if (pc == KeyTonic || Low(pc) < CentreBassRatio * Low(KeyTonic) || All(pc) < CentreNotesRatio * All(KeyTonic)) continue;
            float weight = Low(pc) + All(pc);
            if (weight > bestWeight) { bestWeight = weight; candidate = pc; }
        }
        if (candidate == _centre) { _centreCandidate = -1; return; }
        if (candidate != _centreCandidate) { _centreCandidate = candidate; _centreCandidateTime = 0; }
        _centreCandidateTime += dt;
        if (_centreCandidateTime >= CentreHold || _centre < 0) _centre = candidate;
    }

    /// <summary>
    /// Le mode se joue sur la tierce : si elle n'est pas jouée (quinte « à vide », power chord)
    /// ou si les deux tierces sont présentes à égalité, majeur et mineur sont tous deux défendables.
    /// </summary>
    private void UpdateModeAmbiguity(int shown)
    {
        int tonic = shown % 12;
        int parallel = shown >= 12 ? tonic : tonic + 12;
        float max = Math.Max(1e-9f, PureLong.Max());
        float minorThird = PureLong[(tonic + 3) % 12] / max;
        float majorThird = PureLong[(tonic + 4) % 12] / max;
        Thirds = (minorThird, majorThird);

        float strongest = Math.Max(minorThird, majorThird), weakest = Math.Min(minorThird, majorThird);
        float parallelGap = KeyScores[shown] - KeyScores[parallel];
        // Valeurs en amplitude (0,3 ≈ 10 % de l'énergie de la note la plus forte). Une tierce discrète suffit
        // si l'autre est absente ; le mode est incertain quand les deux sont faibles et du même ordre.
        bool noThird = strongest < 0.12f || (strongest < 0.3f && weakest > 0.4f * strongest);
        bool bothThirds = weakest > 0.8f * strongest && parallelGap < 0.15f;
        bool tie = parallelGap < 0.03f;
        ModeAmbiguous = noThird || bothThirds || tie;
        if (ModeAmbiguous) KeyConfidence = Math.Min(KeyConfidence, 0.6f);
    }

    private void SetKey(int key)
    {
        KeyTonic = key % 12;
        KeyMinor = key >= 12;
        _candidate = -1;
        _candidateTime = 0;
    }

    /// <summary>Diagnostic : meilleures tonalités d'un chroma quelconque (moyenne des profils), et le choix de chaque profil.</summary>
    public static string RankKeys(double[] chroma, int count)
    {
        var c = chroma.Select(v => (float)v).ToArray();
        string Label(int k) => Harmony.NoteName(k % 12, false) + (k >= 12 ? "m" : "");
        double Score(int k, int p) => Correlation(c, k >= 12 ? MinorProfiles[p] : MajorProfiles[p], k % 12);
        var avg = Enumerable.Range(0, 24).OrderByDescending(k => Enumerable.Range(0, MajorProfiles.Length).Average(p => Score(k, p))).Take(count)
            .Select(k => $"{Label(k)}:{Enumerable.Range(0, MajorProfiles.Length).Average(p => Score(k, p)):0.00}");
        var each = Enumerable.Range(0, MajorProfiles.Length).Select(p => $"P{p}={Label(Enumerable.Range(0, 24).OrderByDescending(k => Score(k, p)).First())}");
        return string.Join(" ", avg) + "  (" + string.Join(" ", each) + ")";
    }

    private static double Correlation(float[] chroma, double[] profile, int tonic)
    {
        double meanC = 0, meanP = 0;
        for (int i = 0; i < 12; i++) { meanC += chroma[i]; meanP += profile[i]; }
        meanC /= 12; meanP /= 12;
        double num = 0, vc = 0, vp = 0;
        for (int i = 0; i < 12; i++)
        {
            double c = chroma[i] - meanC;
            double p = profile[(i - tonic + 12) % 12] - meanP;
            num += c * p;
            vc += c * c;
            vp += p * p;
        }
        return vc < 1e-12 ? 0 : num / Math.Sqrt(vc * vp);
    }

    /// <summary>
    /// Notes réellement jouées, octave comprise, par soustraction des harmoniques : du grave à l'aigu, chaque pic
    /// est d'abord « expliqué » par les harmoniques possibles des notes plus graves (au plus HarmonicCeiling(h)
    /// de leur amplitude propre, et seulement si leur série harmonique monte bien jusque-là, voir InSeries).
    /// Seul le reste compte comme note jouée : une harmonique s'éteint, une note qui tombe sur une harmonique
    /// (octave doublée, Mi4 au-dessus d'un La2) reste visible, et les harmoniques de plusieurs notes qui se
    /// superposent (Sol5 = ×2 de Sol4 et ×3 de Do4) s'additionnent au lieu de passer pour une note.
    /// </summary>
    private void BuildNotes(double reference)
    {
        Array.Clear(Notes);
        int n = _peakCount;
        if (n == 0) return;

        for (int i = 0; i < n; i++)
        {
            _order[i] = i;
            _orderKeys[i] = _peakFreq[i];
        }
        Array.Sort(_orderKeys, _order, 0, n);
        float maxAmp = 0;
        for (int i = 0; i < n; i++) maxAmp = Math.Max(maxAmp, _peakAmp[i]);

        for (int a = 0; a < n; a++)
        {
            int j = _order[a];
            float f = _peakFreq[j], amp = _peakAmp[j];
            float explained = 0;
            for (int b = 0; b < a; b++)
            {
                int i = _order[b];
                if (_residual[i] <= 0) continue;
                // Tri grossier (les harmoniques aiguës d'un piano sont un peu trop hautes), le test exact est dans InSeries.
                float ratio = f / _peakFreq[i];
                int h = (int)MathF.Round(ratio);
                if (h < 2 || h > 12 || ratio < h * 0.988f || ratio > h * (1.012f + 0.0004f * h * h)) continue;
                if (InSeries(_peakFreq[i], h, f, n)) explained += _residual[i] * HarmonicCeiling(h);
            }
            _residual[j] = amp - explained;
            if (f < 40 || f > 4500) continue;

            int midi = (int)Math.Round(69 + 12 * Math.Log2(f / reference));
            if (midi is >= 0 and < 128) Notes[midi] = Math.Max(Notes[midi], Math.Max(0, _residual[j]));

            if (DebugPeaks != null) DebugPeaks.Append($"{f:F1}:{amp / maxAmp:F3}:{Math.Max(0, _residual[j]) / amp:F2} ");
        }
        DebugPeaks?.AppendLine();
    }

    /// <summary>Diagnostic (--notetrace avec KEY_PEAKS=1) : pics de chaque image, « fréquence:amplitude:part non expliquée ».</summary>
    public static System.Text.StringBuilder? DebugPeaks;

    /// <summary>
    /// Amplitude maximale attendue de l'harmonique h, relative à la fondamentale : un peu au-dessus de 1/h,
    /// le profil des timbres les plus riches courants (dents de scie ; piano ≈ 1/h^1,2, et décroît plus vite).
    /// </summary>
    private static float HarmonicCeiling(int h) => 1.15f / h;

    /// <summary>
    /// Vrai si le pic de fréquence f peut être l'harmonique h de f0 : toutes les harmoniques intermédiaires sont
    /// présentes et la fréquence colle. Une basse presque pure (sinus, sub) sans harmoniques 3 et 4 n'explique
    /// donc pas la tierce de l'accord qui tombe sur son ×5. L'inharmonicité (partiels du piano un peu trop hauts)
    /// est estimée sur la série elle-même, par une médiane qui résiste aux partiels confondus avec ceux d'une
    /// autre note de l'accord.
    /// </summary>
    private bool InSeries(float f0, int h, float f, int n)
    {
        Span<float> stretch = stackalloc float[h];
        Span<float> sorted = stackalloc float[h];
        int found = 0;
        for (int k = 2; k <= h; k++)
        {
            float inharmonicity = 0;
            if (found > 0)
            {
                stretch[..found].CopyTo(sorted);
                sorted[..found].Sort();
                inharmonicity = Math.Max(0, sorted[found / 2]);
            }
            float expected = k * f0 * MathF.Sqrt(1 + inharmonicity * k * k);
            if (k == h) return MathF.Abs(f - expected) <= 0.012f * expected;
            var (freq, a) = PeakNear(expected, n);
            if (a <= 0)
            {
                // Partiel non résolu car collé à une note voisine plus forte (moins de 4,5 cases d'écart) : supposé présent.
                if (!HasPeakWithin(expected, 4.5f * _binHz, n)) return false;
                continue;
            }
            float r = freq / (k * f0);
            stretch[found++] = (r * r - 1) / (k * k);
        }
        return false;
    }
    private bool HasPeakWithin(float target, float distance, int n)
    {
        int pos = Array.BinarySearch(_orderKeys, 0, n, target - distance);
        if (pos < 0) pos = ~pos;
        return pos < n && _orderKeys[pos] <= target + distance;
    }

    /// <summary>Plus fort pic à ±1,2 % de la fréquence donnée (amplitude 0 s'il n'y en a pas) ; pics triés dans _orderKeys.</summary>
    private (float Freq, float Amp) PeakNear(float target, int n)
    {
        float tolerance = 0.012f * target, freq = target, best = 0;
        int pos = Array.BinarySearch(_orderKeys, 0, n, target - tolerance);
        if (pos < 0) pos = ~pos;
        for (; pos < n && _orderKeys[pos] <= target + tolerance; pos++)
        {
            float a = _peakAmp[_order[pos]];
            if (a > best) { best = a; freq = _orderKeys[pos]; }
        }
        return (freq, best);
    }

    /// <summary>Fondamentale par somme harmonique pondérée sur les pics (robuste aux erreurs d'octave).</summary>
    private void DetectPitch()
    {
        HasPitch = false;
        if (_peakCount == 0) return;

        // Les 12 pics les plus forts servent de candidats (eux-mêmes, ou leur 1/2, 1/3).
        Span<int> top = stackalloc int[Math.Min(12, _peakCount)];
        Span<bool> used = stackalloc bool[_peakCount];
        for (int t = 0; t < top.Length; t++)
        {
            int bestIdx = -1;
            for (int i = 0; i < _peakCount; i++)
                if (!used[i] && (bestIdx < 0 || _peakAmp[i] > _peakAmp[bestIdx])) bestIdx = i;
            used[bestIdx] = true;
            top[t] = bestIdx;
        }

        float bestScore = 0, bestF0 = 0;
        foreach (int idx in top)
        {
            for (int d = 1; d <= 3; d++)
            {
                float f0 = _peakFreq[idx] / d;
                if (f0 < 27 || f0 > 4200) continue;
                float score = 0, fundamental = 0;
                for (int h = 1; h <= 8; h++)
                {
                    float target = f0 * h;
                    float match = 0;
                    for (int i = 0; i < _peakCount; i++)
                        if (Math.Abs(_peakFreq[i] - target) < target * 0.015f && _peakAmp[i] > match) match = _peakAmp[i];
                    if (h == 1) fundamental = match;
                    score += match * MathF.Pow(0.85f, h - 1);
                }
                // Sans pic à la fondamentale, le candidat est souvent une sous-harmonique fantôme.
                if (fundamental <= 0) score *= 0.5f;
                if (score > bestScore) { bestScore = score; bestF0 = f0; }
            }
        }
        if (bestF0 <= 0) return;

        // Affine la fréquence avec le pic réellement présent à la fondamentale, s'il existe.
        float refined = bestF0, amp = 0;
        for (int i = 0; i < _peakCount; i++)
            if (Math.Abs(_peakFreq[i] - bestF0) < bestF0 * 0.015f && _peakAmp[i] > amp) { amp = _peakAmp[i]; refined = _peakFreq[i]; }
        PitchHz = refined;
        PitchDb = Db(Math.Max(amp, bestScore * 0.5f));
        HasPitch = PitchDb > -60;
    }

    /// <summary>
    /// Part de l'énergie des pics (triés dans _orderKeys par BuildNotes) dont la fréquence était déjà là au bloc
    /// précédent. Une note tenue reste exactement en place ; la voix glisse sans arrêt, une toux n'a pas de pics stables.
    /// </summary>
    private float PeakPersistence()
    {
        float matched = 0, total = 0;
        for (int a = 0; a < _peakCount; a++)
        {
            float f = _orderKeys[a], power = _peakAmp[_order[a]] * _peakAmp[_order[a]];
            total += power;
            float tolerance = Math.Max(0.004f * f, 0.5f * _binHz);
            int pos = Array.BinarySearch(_prevKeys, 0, _prevCount, f);
            if (pos < 0) pos = ~pos;
            if ((pos < _prevCount && _prevKeys[pos] - f <= tolerance) || (pos > 0 && f - _prevKeys[pos - 1] <= tolerance))
                matched += power;
        }

        Array.Copy(_orderKeys, _prevKeys, _peakCount);
        _prevCount = _peakCount;
        return total > 0 ? matched / total : 0;
    }
    private static void Decay(float[] values, double dt, double tau)
    {
        float k = (float)Math.Exp(-dt / tau);
        for (int i = 0; i < values.Length; i++) values[i] *= k;
    }

    private static void Normalize(float[] values)
    {
        float max = values.Max();
        if (max <= 0) return;
        for (int i = 0; i < values.Length; i++) values[i] /= max;
    }

    private static float Db(float linear) => 20f * MathF.Log10(Math.Max(linear, 1e-10f));
}
