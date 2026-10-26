using System.Diagnostics;
using SkiaSharp;
using TaskbarVisu.Audio;
using TaskbarVisu.Core;
using TaskbarVisu.Dsp;
using TaskbarVisu.Modules;
using TaskbarVisu.Native;
using TaskbarVisu.Taskbar;

namespace TaskbarVisu.Rendering;

/// <summary>
/// Thread de rendu dédié : boucle cadencée par un timer haute résolution, deux fenêtres overlay
/// (zone gauche / zone droite), analyse audio partagée et mise en veille quand rien ne bouge.
/// </summary>
internal sealed partial class RenderHost : IDisposable
{
    private sealed class Placed
    {
        public required VisualizerModule Module { get; init; }
        public SKRect Rect { get; init; }
        public SKPaint? FadeLeft { get; init; }
        public SKPaint? FadeRight { get; init; }
        public float FadeWidth { get; init; }
    }

    private sealed class ZoneView
    {
        public required OverlayWindow Window { get; init; }
        public List<Placed> Items { get; } = new();
        /// <summary>Guide de démarrage : nom de la zone présentée (les modules sont alors masqués).</summary>
        public string? Guide { get; set; }
        public bool HasContent => Items.Count > 0 || Guide is not null;
    }

    private readonly AudioCapture _audio;
    private readonly TaskbarTracker _tracker;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile AppSettings? _pending;
    private volatile bool _paused;

    // État du thread de rendu uniquement.
    private AppSettings _settings = AppSettings.CreateDefault();
    private AudioAnalyzer _analyzer = null!;
    private RenderContext _ctx = null!;
    private ZoneView _left = null!, _right = null!;
    private readonly Dictionary<string, VisualizerModule> _modules = new();
    private TaskbarState? _bar;
    private int _barVersion = -1;
    private int _themeSignal = -1;
    private Theme _globalTheme = null!;
    private readonly Dictionary<string, Theme> _moduleThemes = new();
    private bool _visible, _wasActive, _idlePresented;
    private string? _hiddenReason;
    private ZoneView[] _zones = Array.Empty<ZoneView>();
    private readonly Dictionary<string, double> _profile = new();
    private int _profileFrames;
    private volatile bool _raiseRequested;
    private IntPtr _timer, _hook, _showHideHook;
    private Win32.WinEventProc? _hookProc, _showHideProc;
    private readonly IntPtr[] _timerHandles = new IntPtr[1];
    private readonly SKPaint _panel = new() { IsAntialias = true };

    public RenderHost(AudioCapture audio, TaskbarTracker tracker, AppSettings settings)
    {
        _audio = audio;
        _tracker = tracker;
        _pending = settings;
        _thread = new Thread(Run) { IsBackground = true, Name = "TaskbarVisu.Render", Priority = ThreadPriority.AboveNormal };
    }

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Visualiseurs masqués par l'utilisateur (raccourci clavier) ; l'audio continue d'être capté.</summary>
    public bool UserHidden
    {
        get => _userHidden;
        set
        {
            // Réaffichés : l'animation de démarrage est rejouée, comme un appareil qu'on rallume.
            if (_userHidden && !value) _bootPending = true;
            _userHidden = value;
        }
    }

    private volatile bool _userHidden;

    // Animation « auto-test » : les segments s'allument de gauche à droite, balayage retour, puis dissolution.
    private const float BootDuration = 1.3f, BootSweep = 0.5f, BootReveal = 0.8f;
    private volatile bool _bootPending = true;
    private double _bootAt = -1, _now;
    private static readonly float[] BootShotTimes = { 0.25f, 0.65f, 1.05f };
    private int _bootShots;
    private readonly SKPaint _bootPaint = new() { IsAntialias = false };

    /// <summary>Secondes écoulées depuis le début de l'animation de démarrage, ou -1 si elle est finie.</summary>
    private float BootTime => _bootAt < 0 || !_settings.BootAnimation || _now - _bootAt >= BootDuration ? -1 : (float)(_now - _bootAt);

    /// <summary>Diagnostic (--dump dossier) : enregistre régulièrement le rendu des zones en PNG.</summary>
    public string? DumpDirectory { get; set; }

    /// <summary>Diagnostic (--headless) : rendu en mémoire uniquement, aucune fenêtre affichée.</summary>
    public bool Headless { get; set; }

    public void Start() => _thread.Start();

    public void ApplySettings(AppSettings settings) => _pending = settings;

    private void Run()
    {
        try
        {
            Initialize();
            Loop();
        }
        catch (Exception ex)
        {
            Log.Error("Thread de rendu arrêté", ex);
        }
        finally
        {
            Cleanup();
        }
    }

    private void Initialize()
    {
        _analyzer = new AudioAnalyzer(_audio.Ring);
        _ctx = new RenderContext();
        _left = new ZoneView { Window = new OverlayWindow() };
        _right = new ZoneView { Window = new OverlayWindow() };
        _zones = new[] { _left, _right };
        _tracker.SetOwnWindows(new[] { _left.Window.Hwnd, _right.Window.Hwnd });
        SystemSignals.TaskbarMayHaveChanged += _tracker.RequestRefresh;

        _timer = Win32.CreateWaitableTimerExW(IntPtr.Zero, null, Win32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Win32.TIMER_ALL_ACCESS);
        if (_timer == IntPtr.Zero)
            _timer = Win32.CreateWaitableTimerExW(IntPtr.Zero, null, 0, Win32.TIMER_ALL_ACCESS);

        // Quand une autre fenêtre (ou la barre elle-même) passe au premier plan, on se replace au-dessus.
        _hookProc = (_, _, _, _, _, _, _) => _raiseRequested = true;
        _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _hookProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

        // Une fenêtre d'application apparaît / disparaît (icône ajoutée ou retirée de la barre),
        // ou un widget se pose sur la barre : on relance la détection des zones libres.
        _showHideProc = OnWindowShownOrHidden;
        _showHideHook = Win32.SetWinEventHook(Win32.EVENT_OBJECT_SHOW, Win32.EVENT_OBJECT_HIDE, IntPtr.Zero,
            _showHideProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        double last = 0, nextCheck = 0, nextRaise = 0, nextFrame = 0, nextDump = 4;

        while (_running)
        {
            Pump();
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(now - last, 0, 0.1);
            last = now;
            _now = now;

            if (_pending is { } pending)
            {
                _pending = null;
                ApplySettingsInternal(pending);
            }
            SyncGuide();

            var bar = _tracker.Current;
            if (bar.Version != _barVersion)
            {
                _barVersion = bar.Version;
                _bar = bar;
                Relayout();
            }

            if (SystemSignals.ThemeVersion != _themeSignal) RefreshTheme();

            if (now >= nextCheck)
            {
                nextCheck = now + 0.25;
                UpdateVisibility();
            }

            if (_visible && (_raiseRequested || now >= nextRaise))
            {
                _raiseRequested = false;
                nextRaise = now + 1.0;
                _left.Window.BringToTop();
                _right.Window.BringToTop();
                _pointer?.BringToTop();
            }

            double interval = 1.0 / Math.Max(15, _settings.Fps);
            // En mode diagnostic on calcule les images même masqué (les fenêtres restent cachées).
            bool active = _visible || DumpDirectory is not null;
            if (active)
            {
                if (!_wasActive) _analyzer.Resync();
                long t0 = Stopwatch.GetTimestamp();
                _analyzer.Process(dt);
                Profile("analyse", t0);
                AnimateThemes(now);
                bool allIdle = true;
                foreach (var zone in _zones)
                {
                    foreach (var item in zone.Items)
                    {
                        t0 = Stopwatch.GetTimestamp();
                        _ctx.Theme = ThemeFor(item.Module);
                        item.Module.Update(_analyzer, dt);
                        Profile(item.Module.Info.Id + ".update", t0);
                        allIdle &= item.Module.IsIdle;
                    }
                }

                bool idle = _settings.PowerSaving && _analyzer.SilentSeconds > 0.5 && allIdle && BootTime < 0 && !GuideActive;
                if (!idle || !_idlePresented)
                {
                    RenderFrame();
                    _idlePresented = idle;
                    _profileFrames++;
                }
                if (idle) interval = 0.1; // Silence complet et affichage figé : on ne redessine plus.
            }
            else
            {
                interval = 0.1;
            }

            if (DumpDirectory is not null && now >= nextDump)
            {
                nextDump = now + 5;
                Dump(DumpDirectory);
            }
            // Diagnostic : trois images de l'animation d'allumage (balayage, retour, fondu).
            if (DumpDirectory is not null && _bootShots < BootShotTimes.Length && BootTime >= BootShotTimes[_bootShots])
                SaveImages(DumpDirectory, $"boot{++_bootShots}-");
            _wasActive = active;

            nextFrame += interval;
            now = clock.Elapsed.TotalSeconds;
            if (nextFrame < now - interval) nextFrame = now + interval;
            WaitUntil(clock, nextFrame);
        }
    }

    private ZoneView[] Zones() => _zones;

    private void Profile(string key, long start)
    {
        if (DumpDirectory is null) return;
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        _profile[key] = _profile.GetValueOrDefault(key) + ms;
    }

    private void Pump()
    {
        while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }

    private void OnWindowShownOrHidden(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (Win32.GetAncestor(hwnd, Win32.GA_ROOT) != hwnd) return;

        int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        bool appWindow = (ex & Win32.WS_EX_APPWINDOW) != 0
                         || ((ex & Win32.WS_EX_TOOLWINDOW) == 0 && Win32.GetWindow(hwnd, Win32.GW_OWNER) == IntPtr.Zero);
        bool onTaskbar = _bar is { Valid: true } bar && Win32.GetWindowRect(hwnd, out var r)
                         && r.Left < bar.Taskbar.Right && r.Right > bar.Taskbar.Left
                         && r.Top < bar.Taskbar.Bottom && r.Bottom > bar.Taskbar.Top
                         && r.Height <= bar.Taskbar.Height * 2;
        if (appWindow || onTaskbar) _tracker.RequestRefresh();
    }

    private void WaitUntil(Stopwatch clock, double deadline)
    {
        var handles = _timerHandles;
        handles[0] = _timer;
        while (_running)
        {
            double remaining = deadline - clock.Elapsed.TotalSeconds;
            if (remaining <= 0.0005) return;
            long due = -(long)(remaining * 10_000_000);
            Win32.SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
            uint r = Win32.MsgWaitForMultipleObjectsEx(1, handles, Win32.INFINITE, Win32.QS_ALLINPUT, Win32.MWMO_INPUTAVAILABLE);
            if (r == Win32.WAIT_OBJECT_0) return;
            Pump();
        }
    }

    private void ApplySettingsInternal(AppSettings s)
    {
        _settings = s;
        _analyzer.SetFftSize(s.FftSize);
        _ctx.ShowScales = s.ShowScales;
        _ctx.Scanlines = s.Scanlines;
        _ctx.Labels.Configure(s.PixelFont, _ctx.Scale);
        foreach (var cfg in s.AllModules)
        {
            if (!_modules.TryGetValue(cfg.Id, out var module))
            {
                var info = ModuleCatalog.Find(cfg.Id);
                if (info is null) continue;
                module = info.Create();
                _modules[cfg.Id] = module;
            }
            module.Configure(cfg);
        }
        RefreshTheme(force: true);
        Relayout();
        _idlePresented = false;
    }

    private void RefreshTheme(bool force = false)
    {
        int signal = SystemSignals.ThemeVersion;
        if (!force && signal == _themeSignal) return;
        _themeSignal = signal;
        _globalTheme = Theme.Build(_settings);
        _ctx.Theme = _globalTheme;

        // Thème propre à chaque module qui a ses propres couleurs.
        _moduleThemes.Clear();
        foreach (var cfg in _settings.AllModules)
        {
            _moduleThemes[cfg.Id] = cfg.Colors switch
            {
                ModuleColorMode.Palette => Theme.Build(_settings, cfg.ColorPalette, null, cfg.Gradient),
                ModuleColorMode.Solid => Theme.Build(_settings, null, Theme.Parse(cfg.ColorSolid) ?? _globalTheme.Ink),
                _ => _globalTheme,
            };
        }
        _idlePresented = false;
    }

    private Theme ThemeFor(VisualizerModule module) =>
        _moduleThemes.TryGetValue(module.Info.Id, out var theme) ? theme : _globalTheme;

    /// <summary>Fait tourner les palettes animées (un tour toutes les ~14 s).</summary>
    private void AnimateThemes(double now)
    {
        float phase = (float)(now / 14.0 % 1.0);
        _globalTheme.Phase = phase;
        foreach (var theme in _moduleThemes.Values) theme.Phase = phase;
    }

    private void Relayout()
    {
        foreach (var zone in Zones())
        {
            DisposeItems(zone);
            zone.Guide = null;
        }
        _idlePresented = false;

        var bar = _bar;
        if (bar is null || !bar.Valid || !bar.Supported)
        {
            _pointer?.Hide();
            UpdateVisibility();
            return;
        }

        _ctx.Scale = bar.Scale;
        _ctx.EdgeInset = _settings.EdgeFade * bar.Scale * 0.8f;
        _ctx.Labels.Configure(_settings.PixelFont, bar.Scale);
        int vpad = (int)Math.Round(_settings.VerticalPadding * bar.Scale);
        int y = bar.Taskbar.Top + vpad;
        int height = Math.Max(8, bar.Taskbar.Height - 2 * vpad);

        // Guide de démarrage : les zones libres sont présentées à la place des modules.
        bool guided = LayoutGuideZones(bar, y, height);
        if (!guided && _settings.SingleGap == SingleGapMode.Center && !bar.Spanning.IsEmpty)
        {
            // Icônes alignées à gauche : tous les modules (gauche puis droite) groupés au milieu du grand espace.
            LayoutZone(_left, bar.Spanning, _settings.Left.Concat(_settings.Right).ToList(), ZoneAlign.Center, y, height, bar.Scale);
        }
        else if (!guided)
        {
            LayoutZone(_left, bar.Left, _settings.Left, _settings.LeftAlign, y, height, bar.Scale);
            LayoutZone(_right, bar.Right, _settings.Right, _settings.RightAlign, y, height, bar.Scale);
        }
        LayoutPointer(bar);
        UpdateVisibility();
    }

    private void LayoutZone(ZoneView view, Zone zone, List<ModuleConfig> configs, ZoneAlign align, int y, int height, float scale)
    {
        if (zone.IsEmpty) return;
        var modules = configs
            .Where(c => c.Enabled && _modules.ContainsKey(c.Id))
            .Select(c => (Module: _modules[c.Id], Info: ModuleCatalog.Find(c.Id)!, Config: c))
            .ToList();
        if (modules.Count == 0) return;

        var slots = ModuleLayout.Compute(
            modules.Select(m => (m.Config.Width * scale, m.Info.MinWidth * scale)).ToList(),
            zone.Width, _settings.Spacing * scale, _settings.FillZones, align);
        if (slots.Count == 0) return;

        // La fenêtre ne couvre que les modules (pas toute la zone) : moins de pixels à composer.
        float left = slots[0].X;
        float right = slots[^1].X + slots[^1].Width;
        view.Window.SetBounds(zone.X0 + (int)left, y, (int)Math.Ceiling(right - left), height);
        float fade = _settings.EdgeFade * scale;
        for (int i = 0; i < slots.Count; i++)
        {
            var (sx, w) = slots[i];
            float x = sx - left;
            var rect = new SKRect(x, 0, x + w, height);
            var module = modules[i].Module;
            module.Resize((int)Math.Round(w), height, scale);
            float f = Math.Min(fade, w / 4);
            view.Items.Add(new Placed
            {
                Module = module,
                Rect = rect,
                FadeLeft = f >= 1 ? FadePaint(rect.Left, rect.Left + f, fromTransparent: true) : null,
                FadeRight = f >= 1 ? FadePaint(rect.Right - f, rect.Right, fromTransparent: false) : null,
                FadeWidth = f,
            });
        }
    }

    /// <summary>Fondu des bords via DstIn : les modules se fondent dans la barre au lieu d'être coupés net.</summary>
    private static SKPaint FadePaint(float x0, float x1, bool fromTransparent)
    {
        var colors = fromTransparent
            ? new[] { SKColors.Transparent, SKColors.Black }
            : new[] { SKColors.Black, SKColors.Transparent };
        return new SKPaint
        {
            BlendMode = SKBlendMode.DstIn,
            Shader = SKShader.CreateLinearGradient(new SKPoint(x0, 0), new SKPoint(x1, 0), colors, SKShaderTileMode.Clamp),
        };
    }

    private void RenderFrame()
    {
        byte opacity = (byte)Math.Round(Math.Clamp(_settings.Opacity, 0, 1) * 255);
        float boot = BootTime;
        foreach (var zone in Zones())
        {
            if (!zone.HasContent) continue;
            var canvas = zone.Window.Canvas;
            if (canvas is null) continue;

            if (zone.Guide is { } label)
            {
                DrawZoneGuide(canvas, zone.Window.Width, zone.Window.Height, label);
                zone.Window.Present(255);
                if (_visible && !Headless && !zone.Window.IsShown) zone.Window.Show();
                continue;
            }

            long t0 = Stopwatch.GetTimestamp();
            canvas.Clear(SKColors.Transparent);
            Profile("clear", t0);
            foreach (var item in zone.Items)
            {
                t0 = Stopwatch.GetTimestamp();
                canvas.Save();
                canvas.ClipRect(item.Rect);
                if (_settings.Background != BackgroundChoice.None)
                {
                    _panel.Color = _settings.Background switch
                    {
                        BackgroundChoice.Black => new SKColor(0, 0, 0, 0xF2),
                        BackgroundChoice.Lcd => new SKColor(0x9A, 0xA5, 0x7E, 0xF2), // écran LCD rétroéclairé
                        _ => new SKColor(0, 0, 0, 0x8C),
                    };
                    float radius = 3 * _ctx.Scale;
                    canvas.DrawRoundRect(item.Rect, radius, radius, _panel);
                }
                try
                {
                    _ctx.Theme = ThemeFor(item.Module);
                    // Pendant l'allumage, le module n'apparaît qu'à la dissolution des segments.
                    if (boot < 0 || boot >= BootReveal) item.Module.Render(canvas, item.Rect, _ctx);
                    if (boot >= 0) DrawBoot(canvas, item.Rect, boot);
                }
                catch (Exception ex)
                {
                    Log.Error($"Rendu du module {item.Module.Info.Id}", ex);
                }
                Profile(item.Module.Info.Id + ".render", t0);

                t0 = Stopwatch.GetTimestamp();
                var r = item.Rect;
                if (item.FadeLeft is not null)
                    canvas.DrawRect(r.Left, r.Top, item.FadeWidth, r.Height, item.FadeLeft);
                if (item.FadeRight is not null)
                    canvas.DrawRect(r.Right - item.FadeWidth, r.Top, item.FadeWidth, r.Height, item.FadeRight);
                canvas.Restore();
                Profile("fade", t0);
            }

            t0 = Stopwatch.GetTimestamp();
            zone.Window.Present(opacity);
            Profile("present", t0);
            if (_visible && !Headless && !zone.Window.IsShown) zone.Window.Show();
        }
        DrawPointer();
    }

    /// <summary>
    /// Une image de l'animation « auto-test » sur un module : segments allumés de gauche à droite (0 – 0,5 s),
    /// balayage retour avec « OK » (0,5 – 0,8 s), puis dissolution aléatoire qui révèle le module.
    /// </summary>
    private void DrawBoot(SKCanvas canvas, SKRect rect, float t)
    {
        var theme = _ctx.Theme;
        int p = _ctx.P;
        float cell = 3 * p;
        int cols = Math.Max(1, (int)(rect.Width / cell)), rows = Math.Max(1, (int)(rect.Height / cell));
        float sweep = Math.Clamp(t / BootSweep, 0, 1);
        float dissolve = t < BootReveal ? 0 : (t - BootReveal) / (BootDuration - BootReveal);
        for (int c = 0; c < cols; c++)
        {
            float u = c / (float)cols;
            if (u > sweep) break;
            for (int r = 0; r < rows; r++)
            {
                uint h = (uint)(c * 73856093) ^ (uint)(r * 19349663) ^ 0x5bd1e995u;
                h ^= h >> 13;
                h *= 0x85ebca6bu;
                float hv = (h & 0xFFFF) / 65535f;
                if ((t < BootSweep && hv < 0.3f) || hv < dissolve) continue;
                byte alpha = (byte)(0xD8 * (1 - dissolve * 0.5f));
                _bootPaint.Color = theme.Spectral(u).WithAlpha(alpha);
                canvas.DrawRect(rect.Left + c * cell, rect.Top + r * cell, cell - p, cell - p, _bootPaint);
            }
        }

        if (t >= BootSweep && t < BootReveal)
        {
            float x = rect.Right - (t - BootSweep) / (BootReveal - BootSweep) * rect.Width;
            _bootPaint.Color = theme.Ink;
            canvas.DrawRect(MathF.Round(x), rect.Top, p, rect.Height, _bootPaint);
        }
        if (t < BootReveal)
        {
            string text = t < BootSweep ? "SELF-TEST" : "OK";
            float y = MathF.Round(rect.MidY - _ctx.Labels.CapHeight / 2);
            float x = MathF.Round(rect.Left + Math.Max(p, _ctx.EdgeInset));
            _bootPaint.Color = new SKColor(0, 0, 0, 0xC0);
            canvas.DrawRect(x - p, y - p, _ctx.Labels.Measure(text) + 2 * p, _ctx.Labels.CapHeight + 2 * p, _bootPaint);
            _ctx.Labels.Draw(canvas, text, x, y, theme.Ink);
        }
    }

    private void UpdateVisibility()
    {
        var bar = _bar;
        string reason = _paused ? "pause" : _userHidden ? "masqués par le raccourci clavier"
            : bar is null ? "pas encore de barre" : !bar.Valid || !bar.Supported ? bar.Message : string.Empty;
        if (reason.Length == 0)
        {
            if (!Win32.IsWindow(bar!.Hwnd) || !Win32.GetWindowRect(bar.Hwnd, out var now) || !now.SameAs(bar.Taskbar))
            {
                // La barre a bougé / changé de taille / été masquée : nouvelle détection.
                _tracker.RequestRefresh();
                reason = "la barre a changé, nouvelle détection";
            }
            else if (!Win32.IsWindowVisible(bar.Hwnd))
            {
                reason = "barre invisible";
            }
            else if (_settings.HideWhenFullscreen && FullscreenOnMonitor(bar))
            {
                reason = "application en plein écran";
            }
        }

        bool visible = reason.Length == 0;
        if (visible && _bootPending)
        {
            _bootPending = false;
            _bootAt = _now;
        }
        if (reason != _hiddenReason)
        {
            _hiddenReason = reason;
            Log.Info(visible ? "Visualiseurs affichés" : $"Visualiseurs masqués : {reason}");
        }
        _visible = visible;
        foreach (var zone in Zones())
        {
            // Une fenêtre n'apparaît qu'après avoir reçu une image (dans RenderFrame).
            if (!visible || !zone.HasContent) zone.Window.Hide();
        }
        if (!visible)
        {
            _idlePresented = false;
            _pointer?.Hide();
        }
    }

    private bool FullscreenOnMonitor(TaskbarState bar)
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _left.Window.Hwnd || fg == _right.Window.Hwnd) return false;
        if (Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONULL) != bar.HMonitor) return false;
        if (!Win32.GetWindowRect(fg, out var r)) return false;
        var m = bar.Monitor;
        if (r.Left > m.Left || r.Top > m.Top || r.Right < m.Right || r.Bottom < m.Bottom) return false;
        // Fenêtre simplement maximisée (barre masquée automatiquement) : ce n'est pas du plein écran.
        if (Win32.IsZoomed(fg) && (Win32.GetWindowLong(fg, Win32.GWL_STYLE) & Win32.WS_CAPTION) == Win32.WS_CAPTION) return false;
        var cls = Win32.ClassOf(fg);
        return cls is not ("Progman" or "WorkerW" or TaskbarLocator.PrimaryClass or TaskbarLocator.SecondaryClass);
    }

    private void Dump(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var lines = new List<string>
            {
                $"time={DateTime.Now:HH:mm:ss} visible={_visible} sampleRate={_analyzer.SampleRate} silent={_analyzer.SilentSeconds:0.0}s audio='{_audio.Status}'",
                $"taskbar={_bar?.Taskbar} scale={_bar?.Scale} left={_bar?.Left} right={_bar?.Right} msg='{_bar?.Message}'",
            };
            if (_guide is { } guide)
                lines.Add($"guide={guide.Mark} icône={guide.Icon?.ToString() ?? "?"} cachée={_pointerHidden} pointe={_pointerTip}");
            foreach (var (zone, name) in new[] { (_left, "left"), (_right, "right") })
            {
                lines.Add($"{name}: shown={zone.Window.IsShown} size={zone.Window.Width}x{zone.Window.Height} " +
                          string.Join(" ", zone.Items.Select(i => $"{i.Module.Info.Id}@{i.Rect.Left:0}+{i.Rect.Width:0}")));
                foreach (var item in zone.Items)
                    if (item.Module.Diagnostics is { } diag) lines.Add("  " + diag);
            }
            SaveImages(directory, string.Empty);
            if (_profileFrames > 0)
            {
                lines.Add($"profil ({_profileFrames} images, ms moyennes par image) — détection barre : {_tracker.LastDetectMs:0.0} ms");
                foreach (var (key, total) in _profile.OrderByDescending(p => p.Value))
                    lines.Add($"  {key,-22} {total / _profileFrames,7:0.000}");
                _profile.Clear();
                _profileFrames = 0;
            }
            File.WriteAllLines(Path.Combine(directory, "state.txt"), lines);
        }
        catch (Exception ex)
        {
            Log.Error("Dump", ex);
        }
    }

    /// <summary>Dernière image de chaque zone en PNG (left.png, right.png, précédés de <paramref name="prefix"/>).</summary>
    private void SaveImages(string directory, string prefix)
    {
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var (zone, name) in new[] { (_left, "left"), (_right, "right") })
            {
                using var image = zone.Window.Snapshot();
                if (image is null || !zone.HasContent) continue;
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(directory, $"{prefix}{name}.png"), data.ToArray());
            }
            if (_guide is { Mark: GuideMark.Tray } && _pointer?.Snapshot() is { } pointer)
            {
                using (pointer)
                using (var data = pointer.Encode(SKEncodedImageFormat.Png, 100))
                    File.WriteAllBytes(Path.Combine(directory, $"{prefix}pointer.png"), data.ToArray());
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dump", ex);
        }
    }

    private static void DisposeItems(ZoneView zone)
    {
        foreach (var item in zone.Items)
        {
            item.FadeLeft?.Shader?.Dispose();
            item.FadeLeft?.Dispose();
            item.FadeRight?.Shader?.Dispose();
            item.FadeRight?.Dispose();
        }
        zone.Items.Clear();
    }

    private void Cleanup()
    {
        SystemSignals.TaskbarMayHaveChanged -= _tracker.RequestRefresh;
        if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
        if (_showHideHook != IntPtr.Zero) Win32.UnhookWinEvent(_showHideHook);
        if (_timer != IntPtr.Zero) Win32.CloseHandle(_timer);
        foreach (var module in _modules.Values) module.Dispose();
        _modules.Clear();
        if (_left is not null) { DisposeItems(_left); _left.Window.Dispose(); }
        if (_right is not null) { DisposeItems(_right); _right.Window.Dispose(); }
        _ctx?.Dispose();
        _panel.Dispose();
        _bootPaint.Dispose();
        _pointer?.Dispose();
        _guidePaint.Dispose();
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(1000);
    }
}
