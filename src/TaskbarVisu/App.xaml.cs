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

    /// <summary>Thème de la fenêtre forcé par --ui-theme (diagnostic) : clair, sombre, ou null pour suivre Windows.</summary>
    private static bool? _uiThemeOverride;

    /// <summary>La fenêtre de réglages est-elle en thème clair ? (suit « AppsUseLightTheme », comme le thème Fluent.)</summary>
    internal static bool UiIsLight => _uiThemeOverride ?? Rendering.Theme.AppsUseLightTheme();

    /// <summary>La barre des tâches est-elle claire ? (« SystemUsesLightTheme » ; les aperçus de la barre la suivent.)</summary>
    internal static bool TaskbarIsLight => _uiThemeOverride ?? Rendering.Theme.SystemUsesLightTheme();
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showSignalWait;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private HotkeyManager? _hotkey;
    private HotkeyChoice _hotkeyApplied = (HotkeyChoice)(-1);
    private bool _trayHintShown;
    private bool _exiting;

    // Un couple « détection de barre + rendu » par écran affiché (un seul, sauf « tous les écrans »).
    private readonly List<(TaskbarTracker Tracker, RenderHost Host)> _screens = new();
    private string _screensKey = string.Empty;

    /// <summary>Rendu de l'écran principal : il fournit aussi les aperçus en direct de la fenêtre de réglages.</summary>
    internal Rendering.RenderHost? PreviewHost => _screens.Count > 0 ? _screens[0].Host : null;
    private System.Windows.Threading.DispatcherTimer? _screenTimer, _updateTimer;
    private string? _dumpDirectory;
    private bool _headless;

    internal static App Instance => (App)Current;

    internal SettingsStore Store { get; private set; } = null!;
    internal AppSettings Settings => Store.Current;
    internal AudioCapture Audio { get; private set; } = null!;

    /// <summary>Barre des tâches du premier écran affiché (pied de la fenêtre de réglages).</summary>
    internal TaskbarTracker Tracker => _screens[0].Tracker;

    internal int ScreenCount => _screens.Count;
    internal bool IsPaused { get; private set; }

    /// <summary>Visualiseurs masqués par le raccourci clavier.</summary>
    internal bool VisualsHidden { get; private set; }

    /// <summary>False si le raccourci choisi est déjà pris par une autre application.</summary>
    internal bool HotkeyAvailable { get; private set; } = true;

    internal UpdateInfo? AvailableUpdate { get; private set; }
    internal string UpdateStatus { get; private set; } = string.Empty;
    internal event Action? UpdateStateChanged;

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
        if (!headless) StartupManager.Sync(Settings.StartWithWindows);

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
        {
            Audio.Configure(Settings.AudioSource, Settings.AudioDeviceId, Settings.AudioApp);
            Audio.Start();
        }

        var dumpIndex = Array.FindIndex(e.Args, a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase));
        if (dumpIndex >= 0 && dumpIndex + 1 < e.Args.Length) _dumpDirectory = e.Args[dumpIndex + 1];
        if (_dumpDirectory is not null) AllocationSampler.Start();
        _headless = headless;
        SyncScreens(force: true);
        // « Tous les écrans » : un écran branché ou débranché est pris en compte en quelques secondes.
        _screenTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _screenTimer.Tick += (_, _) => { if (Settings.AllMonitors) SyncScreens(); };
        _screenTimer.Start();

        _tray = new TrayIcon(this);

        if (!headless)
        {
            try
            {
                _hotkey = new HotkeyManager();
                _hotkey.Pressed += ToggleVisuals;
                ApplyHotkey();
            }
            catch (Exception ex)
            {
                Log.Error("Raccourci clavier indisponible", ex);
            }

            // Mises à jour : un premier contrôle peu après le démarrage, puis une fois par jour.
            _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(24);
                if (Settings.CheckUpdates) await CheckForUpdatesAsync(manual: false);
            };
            _updateTimer.Start();
        }

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

        // Diagnostic : --ui-theme light | dark force le thème de la fenêtre (captures sans toucher à Windows).
        int uiThemeIndex = Array.FindIndex(e.Args, a => a.Equals("--ui-theme", StringComparison.OrdinalIgnoreCase));
        if (uiThemeIndex >= 0 && uiThemeIndex + 1 < e.Args.Length)
        {
            _uiThemeOverride = e.Args[uiThemeIndex + 1].Equals("light", StringComparison.OrdinalIgnoreCase);
            ThemeMode = _uiThemeOverride.Value ? ThemeMode.Light : ThemeMode.Dark;
        }

        if (e.Args.Any(a => a.Equals("--selftest-ui", StringComparison.OrdinalIgnoreCase)))
        {
            RunUiSelfTest();
            return;
        }

        // Diagnostic : --guide zones | tray affiche un repère du guide de démarrage (avec --dump : images).
        int guideIndex = Array.FindIndex(e.Args, a => a.Equals("--guide", StringComparison.OrdinalIgnoreCase));
        if (guideIndex >= 0 && guideIndex + 1 < e.Args.Length)
            ShowGuideMark(e.Args[guideIndex + 1] == "tray" ? GuideMark.Tray : GuideMark.Zones);

        bool startHidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (startHidden)
        {
            // Démarrage avec Windows : une fois tout initialisé, la mémoire du démarrage est rendue au système.
            TrimMemorySoon(15);
            return;
        }
        ShowSettings();
        // Premier lancement : le guide de démarrage accompagne la découverte.
        if (Store.IsFirstRun && !headless) _settingsWindow?.ShowWelcome();
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
            window.PrepareLayoutSnapshot();
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
                if (_dumpDirectory is not { } dir) continue;
                SaveSnapshot(window, Path.Combine(dir, $"settings-{tab}.png"));
                if (window.Tabs.SelectedContent is System.Windows.Controls.ScrollViewer { Content: System.Windows.FrameworkElement body })
                    SaveElement(window, body, Path.Combine(dir, $"settings-{tab}-full.png"));
            }
            if (_dumpDirectory is { } dumpDir)
            {
                // Le sélecteur est rangé dans une carte : on le cherche dans tout l'arbre logique de la page.
                static ColorPickerButton? FindPicker(object node) => node is ColorPickerButton found ? found
                    : node is DependencyObject d ? LogicalTreeHelper.GetChildren(d).Cast<object>().Select(FindPicker).FirstOrDefault(x => x is not null)
                    : null;
                FindPicker(window.AppearancePanel)?.RenderPreview(Path.Combine(dumpDir, "colorpicker.png"));
            }
            // Guide de démarrage : une capture par étape (welcome-1.png…).
            window.ShowWelcome();
            for (int step = 0; step < 5; step++)
            {
                window.GoToWelcomeStep(step);
                window.UpdateLayout();
                if (_dumpDirectory is { } welcomeDir) SaveSnapshot(window, Path.Combine(welcomeDir, $"welcome-{step + 1}.png"));
            }
            Dispatcher.BeginInvoke(() =>
            {
                // Textes des modules, même ceux des modules repliés.
                foreach (var info in Modules.ModuleCatalog.All)
                {
                    Loc.T(info.Name);
                    Loc.T(info.Description);
                    foreach (var opt in info.Options)
                    {
                        Loc.T(opt.Label);
                        if (opt.Help is { } help) Loc.T(help);
                        foreach (var choice in opt.Choices ?? Array.Empty<string>()) Loc.T(choice);
                    }
                }
                foreach (var name in HotkeyManager.Names) Loc.T(name);
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
        if (window.Content is System.Windows.FrameworkElement root) SaveElement(window, root, path);
    }

    /// <summary>Image d'un élément entier, y compris la partie cachée hors d'un défilement.</summary>
    private static void SaveElement(Window window, System.Windows.FrameworkElement root, string path)
    {
        int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
        if (w <= 0 || h <= 0) return;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Le fond Mica n'est pas capturable : on le remplace par un aplat.
            dc.DrawRectangle(window.Background is { } bg && bg != System.Windows.Media.Brushes.Transparent
                ? bg : new System.Windows.Media.SolidColorBrush(UiIsLight
                    ? System.Windows.Media.Color.FromRgb(243, 243, 243) : System.Windows.Media.Color.FromRgb(32, 32, 32)), null,
                new Rect(0, 0, w, h));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(0, 0, w, h));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
                TrimMemorySoon();
            };
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Une fois la fenêtre de réglages fermée (ou après le démarrage), quand WPF a lâché ses dernières références :
    /// collecte complète qui rend la mémoire libre au système, puis ensemble de travail allégé (les pages encore
    /// utiles par le rendu reviennent d'elles-mêmes). Sans cela, la mémoire de la fenêtre restait réservée.
    /// </summary>
    private void TrimMemorySoon(double seconds = 2)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_settingsWindow is not null || _exiting) return; // rouverte entre-temps
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            Native.Win32.SetProcessWorkingSetSize(Native.Win32.GetCurrentProcess(), -1, -1);
        };
        timer.Start();
    }

    /// <summary>Appelé par l'interface après chaque modification de <see cref="Settings"/>.</summary>
    internal void OnSettingsChanged()
    {
        SyncScreens();
        var snapshot = Settings.Clone();
        foreach (var (tracker, host) in _screens)
        {
            host.ApplySettings(snapshot);
            tracker.Configure(snapshot);
        }
        Audio.Configure(snapshot.AudioSource, snapshot.AudioDeviceId, snapshot.AudioApp);
        ApplyHotkey();
        Store.SaveSoon();
    }

    /// <summary>
    /// Crée ou recrée les couples détection + rendu : un pour l'écran choisi, ou un par barre des tâches
    /// en mode « tous les écrans ». Ne fait rien si la liste des écrans n'a pas changé.
    /// </summary>
    private void SyncScreens(bool force = false)
    {
        var devices = Settings.AllMonitors
            ? TaskbarLocator.FindAll().Select(b => (string?)b.DeviceName).ToList()
            : new List<string?> { null };
        if (devices.Count == 0) devices.Add(null);
        string key = string.Join("|", devices.Select(d => d ?? "-"));
        if (!force && key == _screensKey && _screens.Count > 0) return;
        _screensKey = key;

        foreach (var (tracker, host) in _screens)
        {
            host.Dispose();
            tracker.Dispose();
        }
        _screens.Clear();

        var snapshot = Settings.Clone();
        for (int i = 0; i < devices.Count; i++)
        {
            var tracker = new TaskbarTracker(devices[i]);
            tracker.Configure(snapshot);
            tracker.Start();
            var host = new RenderHost(Audio, tracker, snapshot)
            {
                DumpDirectory = i == 0 ? _dumpDirectory : null,
                Headless = _headless,
                Paused = IsPaused,
                UserHidden = VisualsHidden,
            };
            host.Start();
            _screens.Add((tracker, host));
        }
        if (devices.Count > 1) Log.Info($"Visualiseurs sur {devices.Count} écrans");
        ApplyGuide();
    }

    private GuideRequest? _guide;

    /// <summary>Guide de démarrage : repère sur la vraie barre (zones libres, ou flèche vers l'icône de notification).</summary>
    internal void ShowGuideMark(GuideMark mark)
    {
        _guide = mark == GuideMark.None ? null : new GuideRequest(mark,
            Loc.T("Zone gauche").ToUpperInvariant(), Loc.T("Zone droite").ToUpperInvariant(), Loc.T("Zone centrale").ToUpperInvariant(),
            Loc.T("Vos réglages sont ici").ToUpperInvariant(), Loc.T("Icône cachée ? Cliquez sur ^").ToUpperInvariant(),
            mark == GuideMark.Tray ? _tray?.IconRect() : null);
        ApplyGuide();
    }

    private void ApplyGuide()
    {
        // La zone de notification n'est que sur la barre principale : la flèche n'apparaît que là.
        int tray = Math.Max(0, _screens.FindIndex(s => s.Tracker.Current is { Valid: true } bar
                                                      && Native.Win32.ClassOf(bar.Hwnd) == TaskbarLocator.PrimaryClass));
        for (int i = 0; i < _screens.Count; i++)
            _screens[i].Host.SetGuide(_guide?.Mark != GuideMark.Tray || i == tray ? _guide : null);
    }

    internal void TogglePause()
    {
        IsPaused = !IsPaused;
        foreach (var (_, host) in _screens) host.Paused = IsPaused;
        if (IsPaused) Audio.Stop();
        else Audio.Start();
        _tray?.UpdatePauseState(IsPaused);
    }

    /// <summary>Masque / réaffiche les visualiseurs (raccourci clavier ou menu de l'icône).</summary>
    internal void ToggleVisuals()
    {
        VisualsHidden = !VisualsHidden;
        foreach (var (_, host) in _screens) host.UserHidden = VisualsHidden;
        _tray?.UpdateHiddenState(VisualsHidden);
    }

    private void ApplyHotkey()
    {
        if (_hotkey is null || Settings.Hotkey == _hotkeyApplied) return;
        _hotkeyApplied = Settings.Hotkey;
        HotkeyAvailable = _hotkey.Set(Settings.Hotkey);
        _tray?.UpdateHiddenState(VisualsHidden);
    }

    internal string HotkeyText => Settings.Hotkey == HotkeyChoice.None ? string.Empty : Loc.T(HotkeyManager.Names[(int)Settings.Hotkey]);

    /// <summary>Interroge GitHub ; en arrière-plan, ne prévient qu'une fois par version (et pas si elle a été ignorée).</summary>
    internal async Task CheckForUpdatesAsync(bool manual)
    {
        UpdateStatus = Loc.T("Recherche de mises à jour…");
        UpdateStateChanged?.Invoke();
        try
        {
            var update = await UpdateChecker.CheckAsync();
            bool known = AvailableUpdate?.Version == update?.Version;
            AvailableUpdate = update;
            UpdateStatus = update is null
                ? Loc.F("Version {0} : à jour", UpdateChecker.CurrentText)
                : Loc.F("Version {0} disponible (installée : {1})", update.Version, UpdateChecker.CurrentText);
            if (update is not null && !known && (manual || Settings.SkippedVersion != update.Tag))
                _tray?.ShowHint(Loc.F("TaskbarVisu {0} est disponible", update.Version),
                    Loc.T("Ouvrez le menu de l'icône ou les réglages (Général) pour l'installer."));
        }
        catch (Exception ex)
        {
            Log.Error("Recherche de mises à jour", ex);
            UpdateStatus = Loc.T("Impossible de joindre GitHub pour le moment");
        }
        _tray?.UpdateUpdateState(AvailableUpdate);
        UpdateStateChanged?.Invoke();
    }

    /// <summary>Installe la mise à jour disponible (ou ouvre sa page en version portable).</summary>
    internal async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } update) return;
        try
        {
            UpdateStatus = Loc.T("Téléchargement de la mise à jour…");
            UpdateStateChanged?.Invoke();
            if (await UpdateChecker.InstallAsync(update)) ExitApp();
        }
        catch (Exception ex)
        {
            Log.Error("Installation de la mise à jour", ex);
            UpdateStatus = Loc.T("Échec du téléchargement : réessayez ou passez par la page des versions");
            UpdateStateChanged?.Invoke();
        }
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
        _screenTimer?.Stop();
        _updateTimer?.Stop();
        _hotkey?.Dispose();
        _tray?.Dispose();
        foreach (var (tracker, host) in _screens)
        {
            host.Dispose();
            tracker.Dispose();
        }
        Audio?.Dispose();
        Store?.Dispose();
        _singleInstance?.Dispose();
        _showSignal?.Dispose();
        base.OnExit(e);
    }
}
