using System.Runtime.InteropServices;
using SkiaSharp;
using TaskbarVisu.Native;

namespace TaskbarVisu.Rendering;

/// <summary>Signaux système reçus par les fenêtres overlay (thème, écrans, redémarrage d'Explorer).</summary>
internal static class SystemSignals
{
    private static int _themeVersion;
    public static event Action? TaskbarMayHaveChanged;

    public static int ThemeVersion => Volatile.Read(ref _themeVersion);

    public static void RaiseTheme() => Interlocked.Increment(ref _themeVersion);
    public static void RaiseTaskbar() => TaskbarMayHaveChanged?.Invoke();
}

/// <summary>
/// Fenêtre Win32 « layered » sans bordure, transparente aux clics, toujours au premier plan.
/// Le contenu est dessiné par Skia directement dans une DIB 32 bits (alpha prémultiplié)
/// puis envoyé à DWM via UpdateLayeredWindow : aucun fond, aucun contour.
/// </summary>
internal sealed unsafe class OverlayWindow : IDisposable
{
    public const string ClassName = "TaskbarVisuOverlay";

    private static readonly Win32.WndProc Proc = WndProc;
    private static bool _classRegistered;
    private static uint _taskbarCreatedMsg;

    private IntPtr _memDc, _dib, _oldBitmap;
    private IntPtr _bits;
    private SKSurface? _surface;
    private int _x, _y, _width, _height;
    private bool _shown;

    public OverlayWindow()
    {
        EnsureClass();
        const int ex = Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW
                       | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOACTIVATE;
        Hwnd = Win32.CreateWindowEx(ex, ClassName, "TaskbarVisu", Win32.WS_POPUP, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx a échoué ({Marshal.GetLastWin32Error()})");
    }

    public IntPtr Hwnd { get; }
    public SKCanvas? Canvas => _surface?.Canvas;
    public int Width => _width;
    public int Height => _height;
    public bool IsShown => _shown;

    private static void EnsureClass()
    {
        if (_classRegistered) return;
        _taskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarCreated");
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = Win32.GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        if (Win32.RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx a échoué ({Marshal.GetLastWin32Error()})");
        _classRegistered = true;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_NCHITTEST:
                return Win32.HTTRANSPARENT;
            case Win32.WM_MOUSEACTIVATE:
                return Win32.MA_NOACTIVATE;
            case Win32.WM_SETTINGCHANGE:
                SystemSignals.RaiseTheme();
                SystemSignals.RaiseTaskbar();
                break;
            case Win32.WM_DISPLAYCHANGE:
            case Win32.WM_DPICHANGED:
                SystemSignals.RaiseTaskbar();
                break;
        }
        if (msg == _taskbarCreatedMsg && msg != 0) SystemSignals.RaiseTaskbar();
        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>Positionne la fenêtre (pixels écran). Recrée la surface si la taille change.</summary>
    public void SetBounds(int x, int y, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        _x = x;
        _y = y;
        if (width == _width && height == _height && _surface is not null) return;

        ReleaseSurface();
        _width = width;
        _height = height;

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = width,
            biHeight = -height, // DIB « top-down »
            biPlanes = 1,
            biBitCount = 32,
        };
        var screen = Win32.GetDC(IntPtr.Zero);
        _memDc = Win32.CreateCompatibleDC(screen);
        Win32.ReleaseDC(IntPtr.Zero, screen);
        _dib = Win32.CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            ReleaseSurface();
            throw new InvalidOperationException("CreateDIBSection a échoué");
        }
        _oldBitmap = Win32.SelectObject(_memDc, _dib);

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info, _bits, width * 4);
    }

    public SKImage? Snapshot() => _surface?.Snapshot();

    public void Present(byte opacity)
    {
        if (_surface is null) return;
        _surface.Canvas.Flush();
        Win32.GdiFlush();
        var dst = new POINT { X = _x, Y = _y };
        var size = new SIZE { Cx = _width, Cy = _height };
        var src = new POINT();
        var blend = new BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            SourceConstantAlpha = opacity,
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };
        Win32.UpdateLayeredWindow(Hwnd, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, Win32.ULW_ALPHA);
    }

    public void Show()
    {
        if (_shown) return;
        Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
        _shown = true;
        BringToTop();
    }

    public void Hide()
    {
        if (!_shown) return;
        Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
        _shown = false;
    }

    /// <summary>Repasse au-dessus de la barre des tâches (elle remonte quand on clique dessus).</summary>
    public void BringToTop()
    {
        if (!_shown) return;
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    private void ReleaseSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
            Win32.DeleteDC(_memDc);
        }
        if (_dib != IntPtr.Zero) Win32.DeleteObject(_dib);
        _memDc = _dib = _oldBitmap = _bits = IntPtr.Zero;
        _width = _height = 0;
    }

    public void Dispose()
    {
        ReleaseSurface();
        Win32.DestroyWindow(Hwnd);
    }
}
