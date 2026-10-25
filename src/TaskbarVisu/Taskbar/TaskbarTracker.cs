using System.Windows;
using System.Windows.Automation;
using TaskbarVisu.Core;
using TaskbarVisu.Native;

namespace TaskbarVisu.Taskbar;

/// <summary>Zone horizontale libre sur la barre des tâches (coordonnées écran, pixels physiques).</summary>
internal readonly record struct Zone(int X0, int X1)
{
    public int Width => X1 - X0;
    public bool IsEmpty => X1 <= X0;
    public static readonly Zone None = new(0, 0);
}

/// <summary>Instantané de la barre des tâches cible et des zones libres gauche / droite.</summary>
internal sealed class TaskbarState
{
    public int Version { get; set; }
    public bool Valid { get; init; }
    public bool Supported { get; init; } = true;
    public string Message { get; init; } = string.Empty;
    public IntPtr Hwnd { get; init; }
    public IntPtr HMonitor { get; init; }
    public RECT Taskbar { get; init; }
    public RECT Monitor { get; init; }
    public float Scale { get; init; } = 1f;
    public bool AtTop { get; init; }
    public Zone Left { get; init; }
    public Zone Right { get; init; }

    public bool SameLayout(TaskbarState o) =>
        Valid == o.Valid && Supported == o.Supported && Message == o.Message && Hwnd == o.Hwnd
        && Taskbar.SameAs(o.Taskbar) && Math.Abs(Scale - o.Scale) < 0.001f
        && Near(Left, o.Left) && Near(Right, o.Right);

    private static bool Near(Zone a, Zone b) => Math.Abs(a.X0 - b.X0) <= 2 && Math.Abs(a.X1 - b.X1) <= 2;

    public static TaskbarState Invalid(string message) => new() { Valid = false, Message = message };
}

/// <summary>
/// Thread d'arrière-plan qui localise la barre des tâches et calcule les zones libres :
/// éléments de la barre via UI Automation (Démarrer, icônes, zone de notification…)
/// + fenêtres tierces posées dessus (FluentFlyout, widgets…).
/// </summary>
internal sealed class TaskbarTracker : IDisposable
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        TaskbarLocator.PrimaryClass, TaskbarLocator.SecondaryClass,
        "Progman", "WorkerW", "tooltips_class32", "Xaml_WindowedPopupClass",
        "TaskListThumbnailWnd", "TaskbarVisuOverlay",
    };

    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile TaskbarState _state = TaskbarState.Invalid("Recherche de la barre des tâches…");
    private volatile string? _monitorDevice;
    private volatile int _intervalMs = 3000;
    private volatile int _spacing = 14;
    private volatile IntPtr[] _ownWindows = Array.Empty<IntPtr>();
    private CacheRequest? _cache;

    public TaskbarTracker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "TaskbarVisu.Tracker", Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public TaskbarState Current => _state;

    /// <summary>Durée de la dernière détection (diagnostic).</summary>
    public double LastDetectMs { get; private set; }

    public void Start() => _thread.Start();

    public void Configure(AppSettings s)
    {
        _monitorDevice = s.MonitorDevice;
        _intervalMs = s.DetectionIntervalMs;
        _spacing = s.Spacing;
        RequestRefresh();
    }

    public void SetOwnWindows(IntPtr[] handles)
    {
        _ownWindows = handles;
        RequestRefresh();
    }

    public void RequestRefresh() => _wake.Set();

    private void Run()
    {
        while (_running)
        {
            try
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var next = Detect(_state);
                LastDetectMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (!next.SameLayout(_state))
                {
                    next.Version = _state.Version + 1;
                    _state = next;
                    Log.Info($"Barre des tâches : {(next.Valid ? $"{next.Taskbar} échelle {next.Scale:0.##}, gauche {next.Left.Width}px, droite {next.Right.Width}px" : next.Message)}");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Détection de la barre des tâches", ex);
            }

            if (_wake.WaitOne(_intervalMs))
            {
                // Réveil sur événement : on laisse la barre finir son animation et on regroupe les rafales.
                Thread.Sleep(300);
                _wake.WaitOne(0);
            }
        }
    }

    private TaskbarState Detect(TaskbarState previous)
    {
        var bars = TaskbarLocator.FindAll();
        if (bars.Count == 0) return TaskbarState.Invalid("Barre des tâches introuvable");

        var device = _monitorDevice;
        var bar = bars.FirstOrDefault(b => device is not null && b.DeviceName == device)
                  ?? bars.FirstOrDefault(b => b.Primary)
                  ?? bars[0];

        if (!Win32.GetWindowRect(bar.Hwnd, out var tb) || tb.IsEmpty)
            return TaskbarState.Invalid("Barre des tâches introuvable");

        float scale = Win32.ScaleForMonitor(bar.Monitor);
        var mon = bar.MonitorRect;

        if (tb.Width < tb.Height)
        {
            return new TaskbarState
            {
                Valid = true, Supported = false, Hwnd = bar.Hwnd, HMonitor = bar.Monitor, Taskbar = tb, Monitor = mon,
                Scale = scale, Message = "Barre des tâches verticale : non prise en charge",
            };
        }

        int visibleTop = Math.Max(tb.Top, mon.Top), visibleBottom = Math.Min(tb.Bottom, mon.Bottom);
        if (!Win32.IsWindowVisible(bar.Hwnd) || visibleBottom - visibleTop < tb.Height * 0.6)
            return TaskbarState.Invalid("Barre des tâches masquée");

        var occupied = new List<(int A, int B)>();
        int uiaCount = CollectAutomationElements(bar.Hwnd, tb, occupied);
        if (uiaCount == 0)
        {
            // UI Automation indisponible pour l'instant (Explorer qui redémarre…) : on garde la dernière disposition connue.
            if (previous.Valid && previous.Hwnd == bar.Hwnd && previous.Taskbar.SameAs(tb)) return previous;
            return TaskbarState.Invalid("Analyse de la barre des tâches impossible pour le moment");
        }

        var exclude = new HashSet<IntPtr>(_ownWindows);
        foreach (var b in bars) exclude.Add(b.Hwnd);
        CollectOverlappingWindows(tb, exclude, occupied);

        var (left, right) = ComputeZones(tb, occupied, scale, _spacing);
        return new TaskbarState
        {
            Valid = true, Hwnd = bar.Hwnd, HMonitor = bar.Monitor, Taskbar = tb, Monitor = mon, Scale = scale,
            AtTop = Math.Abs(tb.Top - mon.Top) < Math.Abs(tb.Bottom - mon.Bottom),
            Left = left, Right = right,
        };
    }

    private int CollectAutomationElements(IntPtr hwnd, RECT tb, List<(int, int)> occupied)
    {
        if (_cache is null)
        {
            _cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None, TreeScope = TreeScope.Element };
            _cache.Add(AutomationElement.BoundingRectangleProperty);
            _cache.Add(AutomationElement.IsOffscreenProperty);
        }

        int count = 0;
        try
        {
            // La racine doit être un élément « vivant » ; seuls les résultats de la recherche sont en cache.
            var root = AutomationElement.FromHandle(hwnd);
            using (_cache.Activate())
            {
                var elements = root.FindAll(TreeScope.Descendants, Automation.ControlViewCondition);
                foreach (AutomationElement el in elements)
                {
                    if (el.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty, true) is not Rect rc) continue;
                    if (rc.IsEmpty || double.IsInfinity(rc.Width) || rc.Width < 2 || rc.Height < 2) continue;
                    if (el.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty, true) is true) continue;
                    if (rc.Right <= tb.Left || rc.Left >= tb.Right || rc.Bottom <= tb.Top || rc.Top >= tb.Bottom) continue;
                    // Les conteneurs qui couvrent toute la barre ne représentent pas un élément visible.
                    if (rc.Width > tb.Width * 0.45) continue;
                    occupied.Add(((int)Math.Floor(rc.Left), (int)Math.Ceiling(rc.Right)));
                    count++;
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            return 0;
        }
        return count;
    }

    private static void CollectOverlappingWindows(RECT tb, HashSet<IntPtr> exclude, List<(int, int)> occupied)
    {
        Win32.EnumWindows((h, _) =>
        {
            if (exclude.Contains(h) || !Win32.IsWindowVisible(h) || Win32.IsIconic(h) || Win32.IsCloaked(h)) return true;
            if (!Win32.GetWindowRect(h, out var w) || w.IsEmpty) return true;

            int ix0 = Math.Max(w.Left, tb.Left), ix1 = Math.Min(w.Right, tb.Right);
            int iy0 = Math.Max(w.Top, tb.Top), iy1 = Math.Min(w.Bottom, tb.Bottom);
            if (ix1 <= ix0 || iy1 <= iy0) return true;

            // Fenêtre essentiellement posée sur la barre (widget, lecteur média…), pas une fenêtre normale.
            long inter = (long)(ix1 - ix0) * (iy1 - iy0);
            long area = (long)w.Width * w.Height;
            if (inter < area * 0.6 || w.Width > tb.Width * 0.5) return true;
            if (IgnoredClasses.Contains(Win32.ClassOf(h))) return true;

            occupied.Add((ix0, ix1));
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>
    /// Plus grand espace libre à gauche du centre et à droite du centre.
    /// Si un seul grand espace traverse le centre (icônes alignées à gauche), il est partagé en deux.
    /// </summary>
    internal static (Zone Left, Zone Right) ComputeZones(RECT tb, List<(int A, int B)> occupied, float scale, int spacing)
    {
        int pad = (int)Math.Round(10 * scale);
        int edge = (int)Math.Round(6 * scale);
        int minZone = (int)Math.Round(48 * scale);

        var merged = occupied
            .Select(o => (A: Math.Max(tb.Left, o.A - pad), B: Math.Min(tb.Right, o.B + pad)))
            .Where(o => o.B > o.A)
            .OrderBy(o => o.A)
            .ToList();

        var gaps = new List<Zone>();
        int cursor = tb.Left + edge;
        foreach (var (a, b) in merged)
        {
            if (a > cursor) gaps.Add(new Zone(cursor, a));
            cursor = Math.Max(cursor, b);
        }
        if (tb.Right - edge > cursor) gaps.Add(new Zone(cursor, tb.Right - edge));

        var usable = gaps.Where(g => g.Width >= minZone).ToList();
        int center = (tb.Left + tb.Right) / 2;
        int slack = (int)(tb.Width * 0.02);

        Zone left = usable.Where(g => g.X1 <= center + slack).OrderByDescending(g => g.Width).FirstOrDefault();
        Zone right = usable.Where(g => g.X0 >= center - slack).OrderByDescending(g => g.Width).FirstOrDefault();
        Zone spanning = usable.Where(g => g.X0 < center - slack && g.X1 > center + slack)
                              .OrderByDescending(g => g.Width).FirstOrDefault();

        if (!spanning.IsEmpty && spanning.Width > Math.Max(left.Width, right.Width))
        {
            int mid = (spanning.X0 + spanning.X1) / 2;
            int half = (int)Math.Round(spacing * scale / 2);
            left = new Zone(spanning.X0, mid - half);
            right = new Zone(mid + half, spanning.X1);
        }

        return (left, right);
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
    }
}
