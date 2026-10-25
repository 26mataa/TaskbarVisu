using System.Windows;
using TaskbarVisu.Audio;
using TaskbarVisu.Core;
using TaskbarVisu.Rendering;
using TaskbarVisu.Taskbar;
using TaskbarVisu.UI;

namespace TaskbarVisu;

/// <summary>
/// Point d'entrée : une seule instance, vit dans la zone de notification.
/// La fenêtre de réglages n'est créée qu'à la demande et libérée à la fermeture.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showSignalWait;
    private RenderHost? _render;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private bool _trayHintShown;
    private bool _exiting;

    internal static App Instance => (App)Current;

    internal SettingsStore Store { get; private set; } = null!;
    internal AppSettings Settings => Store.Current;
    internal AudioCapture Audio { get; private set; } = null!;
    internal TaskbarTracker Tracker { get; private set; } = null!;
    internal bool IsPaused { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Diagnostic : --notetrace <rapport> synth:… | chord:… | notes:… : trace des notes entendues, puis quitte.
        int traceIndex = Array.FindIndex(e.Args, a => a.Equals("--notetrace", StringComparison.OrdinalIgnoreCase));
        if (traceIndex >= 0 && traceIndex + 2 < e.Args.Length)
        {
            File.WriteAllText(e.Args[traceIndex + 1], Dsp.OfflineAnalysis.NoteTrace(e.Args[traceIndex + 2], 12));
            Shutdown();
            return;
        }

        // Diagnostic : --bench <rapport> <csv> <dossier>… : banc de test de la détection de tonalité, puis quitte.
        int benchIndex = Array.FindIndex(e.Args, a => a.Equals("--bench", StringComparison.OrdinalIgnoreCase));
        if (benchIndex >= 0 && benchIndex + 3 < e.Args.Length)
        {
            var folders = e.Args.Skip(benchIndex + 3).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            File.WriteAllText(e.Args[benchIndex + 1], Dsp.OfflineAnalysis.Bench(e.Args[benchIndex + 2], folders));
            Shutdown();
            return;
        }

        // Diagnostic : --analyze <rapport> <fichier>… analyse des fichiers audio hors ligne puis quitte.
        int analyzeIndex = Array.FindIndex(e.Args, a => a.Equals("--analyze", StringComparison.OrdinalIgnoreCase));
        if (analyzeIndex >= 0 && analyzeIndex + 2 < e.Args.Length)
        {
            var report = new System.Text.StringBuilder();
            foreach (var file in e.Args.Skip(analyzeIndex + 2))
            {
                foreach (var memory in new[] { 30.0, 60.0 })
                {
                    try { report.AppendLine(Dsp.OfflineAnalysis.Analyze(file, memory)); }
                    catch (Exception ex) { report.AppendLine($"== {file} : {ex.Message}"); }
                }
                try { report.AppendLine(Dsp.OfflineAnalysis.RawChroma(file)); }
                catch (Exception ex) { report.AppendLine($"  chroma brut : {ex.Message}"); }
            }
            File.WriteAllText(e.Args[analyzeIndex + 1], report.ToString());
            Shutdown();
            return;
        }

        // --headless (diagnostic) : instance séparée qui n'affiche rien et ne dérange pas l'instance normale.
        bool headless = e.Args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase));
        string suffix = headless ? $".Headless.{Environment.ProcessId}" : string.Empty;
        _singleInstance = new Mutex(true, @"Local\TaskbarVisu.SingleInstance" + suffix, out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TaskbarVisu.ShowSettings" + suffix);
        if (!isFirst)
        {
            // Déjà lancé : on demande à l'instance existante d'ouvrir ses réglages.
            _showSignal.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Exception non gérée (interface)", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Exception non gérée", args.ExceptionObject as Exception);

        Log.Info($"Démarrage de TaskbarVisu {typeof(App).Assembly.GetName().Version}");
        Store = SettingsStore.Load();
        Loc.Set(Settings.Language);
        int langIndex = Array.FindIndex(e.Args, a => a.Equals("--lang", StringComparison.OrdinalIgnoreCase));
        if (langIndex >= 0 && langIndex + 1 < e.Args.Length)
        {
            // Diagnostic : force une langue sans toucher aux réglages.
            Loc.Set(e.Args[langIndex + 1] switch
            {
                "en" => Language.English,
                "es" => Language.Spanish,
                "de" => Language.German,
                _ => Language.French,
            });
        }
        StartupManager.Sync(Settings.StartWithWindows);

        Audio = new AudioCapture();
        int keyIndex = Array.FindIndex(e.Args, a => a.Equals("--test-key", StringComparison.OrdinalIgnoreCase));
        int detuneIndex = Array.FindIndex(e.Args, a => a.Equals("--test-detune", StringComparison.OrdinalIgnoreCase));
        double detune = detuneIndex >= 0 && detuneIndex + 1 < e.Args.Length
            && double.TryParse(e.Args[detuneIndex + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        if (keyIndex >= 0 && keyIndex + 1 < e.Args.Length && TestSong.Parse(e.Args[keyIndex + 1], detune) is { } song)
        {
            int noiseIndex = Array.FindIndex(e.Args, a => a.Equals("--test-noise", StringComparison.OrdinalIgnoreCase));
            if (noiseIndex >= 0 && noiseIndex + 1 < e.Args.Length
                && double.TryParse(e.Args[noiseIndex + 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var noise))
                song.Noise = noise;
            Audio.StartTestSignal(song);
        }
        else if (Array.FindIndex(e.Args, a => a.Equals("--test-file", StringComparison.OrdinalIgnoreCase)) is var fileIndex
                 && fileIndex >= 0 && fileIndex + 1 < e.Args.Length)
            Audio.StartTestFile(e.Args[fileIndex + 1]);
        else if (e.Args.Any(a => a.Equals("--test-signal", StringComparison.OrdinalIgnoreCase)))
            Audio.StartTestSignal();
        else
            Audio.Start();

        Tracker = new TaskbarTracker();
        Tracker.Configure(Settings);
        Tracker.Start();

        _render = new RenderHost(Audio, Tracker, Settings.Clone());
        var dumpIndex = Array.FindIndex(e.Args, a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase));
        if (dumpIndex >= 0 && dumpIndex + 1 < e.Args.Length) _render.DumpDirectory = e.Args[dumpIndex + 1];
        _render.Headless = headless;
        _render.Start();

        _tray = new TrayIcon(this);

        _showSignalWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        if (e.Args.Any(a => a.Equals("--selftest-reorder", StringComparison.OrdinalIgnoreCase)))
        {
            // Diagnostic : après 4 s, le dernier module de la zone gauche remonte en 2e position.
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var last = Settings.Left.Where(m => m.Enabled).Last();
                Settings.Left.Remove(last);
                Settings.Left.Insert(1, last);
                Log.Info($"Auto-test : {last.Id} déplacé en 2e position");
                OnSettingsChanged();
            };
            timer.Start();
        }

        if (e.Args.Any(a => a.Equals("--selftest-ui", StringComparison.OrdinalIgnoreCase)))
        {
            RunUiSelfTest();
            return;
        }

        bool startHidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!startHidden) ShowSettings();
    }

    /// <summary>
    /// Diagnostic : construit la fenêtre de réglages hors écran, sans l'activer, parcourt ses onglets
    /// puis quitte. Le résultat est écrit dans le journal.
    /// </summary>
    private void RunUiSelfTest()
    {
        try
        {
            var window = new SettingsWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            window.Show();
            // Déplie le premier module (et ses options avancées) pour la capture de l'onglet Modules.
            if (window.ModulesPanel.Children.OfType<System.Windows.Controls.Expander>().FirstOrDefault() is { } first)
            {
                first.IsExpanded = true;
                if (first.Content is System.Windows.Controls.Panel body)
                    foreach (var inner in body.Children.OfType<System.Windows.Controls.Expander>()) inner.IsExpanded = true;
            }
            for (int tab = 0; tab < window.Tabs.Items.Count; tab++)
            {
                window.Tabs.SelectedIndex = tab;
                window.UpdateLayout();
                if (_render?.DumpDirectory is { } dir) SaveSnapshot(window, Path.Combine(dir, $"settings-{tab}.png"));
            }
            if (_render?.DumpDirectory is { } dumpDir)
            {
                var picker = window.AppearancePanel.Children.OfType<System.Windows.Controls.Panel>()
                    .SelectMany(p => p.Children.OfType<ColorPickerButton>()).FirstOrDefault();
                picker?.RenderPreview(Path.Combine(dumpDir, "colorpicker.png"));
            }
            Dispatcher.BeginInvoke(() =>
            {
                Log.Info($"Auto-test interface OK ({window.ModulesPanel.Children.Count} éléments dans l'onglet Modules)");
                lock (Loc.Missing)
                    if (Loc.Missing.Count > 0) Log.Info($"Traductions manquantes ({Loc.Code}) : {string.Join(" | ", Loc.Missing)}");
                window.Close();
                ExitApp();
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        catch (Exception ex)
        {
            Log.Error("Auto-test interface ÉCHEC", ex);
            ExitApp();
        }
    }

    private static void SaveSnapshot(Window window, string path)
    {
        if (window.Content is not System.Windows.FrameworkElement root) return;
        int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
        if (w <= 0 || h <= 0) return;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Le fond Mica n'est pas capturable : on le remplace par un aplat.
            dc.DrawRectangle(window.Background is { } bg && bg != System.Windows.Media.Brushes.Transparent
                ? bg : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)), null,
                new Rect(0, 0, w, h));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(0, 0, w, h));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    internal void ShowSettings()
    {
        if (_exiting) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                if (_exiting) return;
                if (!Settings.CloseToTray)
                {
                    ExitApp();
                    return;
                }
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray?.ShowHint(Loc.T("TaskbarVisu continue de tourner"),
                        Loc.T("Les visualiseurs restent actifs. Cliquez sur l'icône de la zone de notification pour rouvrir les réglages."));
                }
                // La fenêtre fermée est libérée : on rend la mémoire au système.
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            };
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    /// <summary>Appelé par l'interface après chaque modification de <see cref="Settings"/>.</summary>
    internal void OnSettingsChanged()
    {
        var snapshot = Settings.Clone();
        _render?.ApplySettings(snapshot);
        Tracker.Configure(snapshot);
        Store.SaveSoon();
    }

    internal void TogglePause()
    {
        IsPaused = !IsPaused;
        if (_render is not null) _render.Paused = IsPaused;
        if (IsPaused) Audio.Stop();
        else Audio.Start();
        _tray?.UpdatePauseState(IsPaused);
    }

    internal void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _settingsWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _showSignalWait?.Unregister(null);
        _tray?.Dispose();
        _render?.Dispose();
        Tracker?.Dispose();
        Audio?.Dispose();
        Store?.Dispose();
        _singleInstance?.Dispose();
        _showSignal?.Dispose();
        base.OnExit(e);
    }
}
