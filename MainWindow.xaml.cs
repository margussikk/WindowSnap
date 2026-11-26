using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.IO;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;
using Forms = System.Windows.Forms;

namespace WindowSnap;

public unsafe partial class MainWindow : Window
{
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int SnapTolerance = 22;
    private const int NeighborTolerance = 90;
    private UnhookWinEventSafeHandle? _hook;
    private WINEVENTPROC? _callback;
    private bool _busy;
    private bool _exitRequested;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private System.Drawing.Icon? _trayIconImage;
    private Stream? _trayIconStream;
    private readonly Dictionary<HWND, RectI> _rectsAtMoveStart = new();

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
        Closing += MainWindowClosing;
        System.Windows.Application.Current.SessionEnding += (_, _) => _exitRequested = true;
        Closed += (_, _) =>
        {
            StopListening();
            DisposeTrayIcon();
        };

        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        StartListening();
    }

    private void InitializeTrayIcon()
    {
        var iconResource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/WindowSnap.ico"))
            ?? throw new InvalidOperationException("The WindowSnap tray icon resource could not be loaded.");
        _trayIconStream = iconResource.Stream;
        _trayIconImage = new System.Drawing.Icon(_trayIconStream);

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Open settings", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowSettingsWindow)));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "WindowSnap",
            ContextMenuStrip = _trayMenu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowSettingsWindow));
    }

    internal void ShowSettingsWindow()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested) return;

        e.Cancel = true;
        Hide();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayIconImage?.Dispose();
        _trayIconImage = null;
        _trayIconStream?.Dispose();
        _trayIconStream = null;
    }

    private void StartListening()
    {
        _callback = OnWinEvent;
        _hook = PInvoke.SetWinEventHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd, default, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        StatusText.Text = _hook is { IsInvalid: false }
            ? "Ready · Move a window to try snapping"
            : "Could not start the Windows event hook. Try reopening the app.";
    }

    private void StopListening()
    {
        _hook?.Dispose();
        _hook = null;
        _callback = null;
    }

    private void SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EdgeValue is not null) EdgeValue.Text = $"{(int)e.NewValue} px";
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint eventType, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd.Value == null || idObject != 0 || hwnd == (HWND)new System.Windows.Interop.WindowInteropHelper(this).Handle) return;
        if (eventType == EventSystemMoveSizeStart)
        {
            if (PInvoke.IsWindowVisible(hwnd) && GetWindowRect(hwnd, out var startOuter))
                _rectsAtMoveStart[hwnd] = GetVisibleRect(hwnd, startOuter);
            return;
        }
        if (_busy || eventType != EventSystemMoveSizeEnd || !PInvoke.IsWindowVisible(hwnd)) return;

        _busy = true;
        try
        {
            if (!GetWindowRect(hwnd, out var outerRect)) return;
            var rect = GetVisibleRect(hwnd, outerRect);
            var wasResized = _rectsAtMoveStart.Remove(hwnd, out var startRect) &&
                (Math.Abs(rect.Width - startRect.Width) > 2 || Math.Abs(rect.Height - startRect.Height) > 2);
            if (wasResized && TryResizeMagnet(hwnd, startRect, rect, out var sizeMatch))
            {
                MoveVisibleFrameTo(hwnd, sizeMatch.Left, sizeMatch.Top, outerRect, rect, sizeMatch.Width, sizeMatch.Height);
                Report(sizeMatch.Width != rect.Width && sizeMatch.Height != rect.Height
                    ? "Resized to match a nearby window"
                    : sizeMatch.Width != rect.Width ? "Width matched a nearby window" : "Height matched a nearby window");
                return;
            }
            var work = GetWorkArea(hwnd);
            var distance = (int)EdgeSlider.Value;
            var left = Math.Abs(rect.Left - work.Left) <= distance;
            var right = Math.Abs(rect.Right - work.Right) <= distance;
            var top = Math.Abs(rect.Top - work.Top) <= distance;
            var bottom = Math.Abs(rect.Bottom - work.Bottom) <= distance;
            var snapX = left ? work.Left : right ? work.Right - rect.Width : rect.Left;
            var snapY = top ? work.Top : bottom ? work.Bottom - rect.Height : rect.Top;
            if (left || right || top || bottom)
            {
                MoveVisibleFrameTo(hwnd, snapX, snapY, outerRect, rect);
                Report(left || right ? top || bottom ? "Snapped to screen corner" : "Snapped to screen edge" : "Snapped to screen edge");
                return;
            }

            if (TryMagnet(hwnd, rect, out var target))
            {
                MoveVisibleFrameTo(hwnd, target.Left, target.Top, outerRect, rect);
                Report("Aligned with a nearby window");
            }
        }
        finally { _busy = false; }
    }

    private bool TryMagnet(HWND moving, RectI original, out RectI aligned)
    {
        var candidate = original;
        aligned = original;
        var best = SnapTolerance + 1;
        PInvoke.EnumWindows((other, _) =>
        {
            if (other == moving || other == (HWND)new System.Windows.Interop.WindowInteropHelper(this).Handle || !PInvoke.IsWindowVisible(other) || !GetWindowRect(other, out var otherOuter)) return true;
            var r = GetVisibleRect(other, otherOuter);
            var overlapY = original.Bottom > r.Top && original.Top < r.Bottom;
            var overlapX = original.Right > r.Left && original.Left < r.Right;
            if (overlapY)
            {
                Consider(Math.Abs(original.Left - r.Right), original.Left, original.Top, r.Right, original.Top);
                Consider(Math.Abs(original.Right - r.Left), original.Left, original.Top, r.Left - original.Width, original.Top);
                Consider(Math.Abs(original.Left - r.Left), original.Left, original.Top, r.Left, original.Top);
                Consider(Math.Abs(original.Right - r.Right), original.Left, original.Top, r.Right - original.Width, original.Top);
            }
            if (overlapX)
            {
                Consider(Math.Abs(original.Top - r.Bottom), original.Left, original.Top, original.Left, r.Bottom);
                Consider(Math.Abs(original.Bottom - r.Top), original.Left, original.Top, original.Left, r.Top - original.Height);
                Consider(Math.Abs(original.Top - r.Top), original.Left, original.Top, original.Left, r.Top);
                Consider(Math.Abs(original.Bottom - r.Bottom), original.Left, original.Top, original.Left, r.Bottom - original.Height);
            }
            return true;

            void Consider(int delta, int oldX, int oldY, int newX, int newY)
            {
                if (delta >= best) return;
                best = delta;
                candidate = new RectI(newX, newY, newX + original.Width, newY + original.Height);
            }
        }, default);
        aligned = candidate;
        return best <= SnapTolerance;
    }

    private bool TryResizeMagnet(HWND moving, RectI before, RectI resized, out RectI matched)
    {
        var candidate = resized;
        matched = resized;
        var best = SnapTolerance + 1;
        var leftChanged = Math.Abs(resized.Left - before.Left);
        var rightChanged = Math.Abs(resized.Right - before.Right);
        var topChanged = Math.Abs(resized.Top - before.Top);
        var bottomChanged = Math.Abs(resized.Bottom - before.Bottom);
        var resizingLeft = leftChanged > rightChanged && leftChanged > 2;
        var resizingRight = rightChanged > leftChanged && rightChanged > 2;
        var resizingTop = topChanged > bottomChanged && topChanged > 2;
        var resizingBottom = bottomChanged > topChanged && bottomChanged > 2;

        PInvoke.EnumWindows((other, _) =>
        {
            if (other == moving || other == (HWND)new System.Windows.Interop.WindowInteropHelper(this).Handle ||
                !PInvoke.IsWindowVisible(other) || !GetWindowRect(other, out var otherOuter)) return true;
            var neighbor = GetVisibleRect(other, otherOuter);
            var nearVertically = resized.Bottom + NeighborTolerance > neighbor.Top && resized.Top - NeighborTolerance < neighbor.Bottom;
            var nearHorizontally = resized.Right + NeighborTolerance > neighbor.Left && resized.Left - NeighborTolerance < neighbor.Right;

            // Snap only the edge being dragged. The opposite edge stays fixed,
            // so the window grows or shrinks in place instead of being repositioned.
            if (nearVertically && resizingRight)
            {
                Consider(Math.Abs(resized.Right - neighbor.Left), resized.Left, resized.Top, neighbor.Left, resized.Bottom);
                Consider(Math.Abs(resized.Right - neighbor.Right), resized.Left, resized.Top, neighbor.Right, resized.Bottom);
            }
            if (nearVertically && resizingLeft)
            {
                Consider(Math.Abs(resized.Left - neighbor.Left), neighbor.Left, resized.Top, resized.Right, resized.Bottom);
                Consider(Math.Abs(resized.Left - neighbor.Right), neighbor.Right, resized.Top, resized.Right, resized.Bottom);
            }
            if (nearHorizontally && resizingBottom)
            {
                Consider(Math.Abs(resized.Bottom - neighbor.Top), resized.Left, resized.Top, resized.Right, neighbor.Top);
                Consider(Math.Abs(resized.Bottom - neighbor.Bottom), resized.Left, resized.Top, resized.Right, neighbor.Bottom);
            }
            if (nearHorizontally && resizingTop)
            {
                Consider(Math.Abs(resized.Top - neighbor.Top), resized.Left, neighbor.Top, resized.Right, resized.Bottom);
                Consider(Math.Abs(resized.Top - neighbor.Bottom), resized.Left, neighbor.Bottom, resized.Right, resized.Bottom);
            }
            return true;

            void Consider(int delta, int left, int top, int right, int bottom)
            {
                if (delta >= best || delta > SnapTolerance || right <= left || bottom <= top) return;
                best = delta;
                candidate = new RectI(left, top, right, bottom);
            }
        }, default);
        matched = candidate;
        return best <= SnapTolerance;
    }

    private RectI GetWorkArea(HWND hwnd)
    {
        var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (PInvoke.GetMonitorInfo(monitor, ref info)) return new RectI(info.rcWork.left, info.rcWork.top, info.rcWork.right, info.rcWork.bottom);
        return new RectI(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
    }

    private static RectI GetVisibleRect(HWND hwnd, RectI fallback)
    {
        RECT visible = default;
        var result = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref visible, 1)));
        var rect = result.Succeeded ? new RectI(visible.left, visible.top, visible.right, visible.bottom) : fallback;

        return new RectI(rect.Left + 1, rect.Top + 1, rect.Right - 1, rect.Bottom - 1);
    }

    private static bool GetWindowRect(HWND hwnd, out RectI rect)
    {
        var success = PInvoke.GetWindowRect(hwnd, out RECT nativeRect);
        rect = new RectI(nativeRect.left, nativeRect.top, nativeRect.right, nativeRect.bottom);
        return success;
    }

    private static void MoveVisibleFrameTo(HWND hwnd, int visibleX, int visibleY, RectI outer, RectI visible, int? visibleWidth = null, int? visibleHeight = null)
    {
        // GetWindowRect includes invisible resize borders; shift the outer window by the
        // visible-frame delta so the part users can see lands exactly on the target edge.
        PInvoke.SetWindowPos(hwnd, default,
            outer.Left + visibleX - visible.Left,
            outer.Top + visibleY - visible.Top,
            outer.Width + (visibleWidth ?? visible.Width) - visible.Width,
            outer.Height + (visibleHeight ?? visible.Height) - visible.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        // Some window managers round the requested outer bounds independently from
        // DWM's visible frame bounds. Read back the result and correct any residual
        // position error so adjacent frames and monitor edges meet exactly.
        var targetWidth = visibleWidth ?? visible.Width;
        var targetHeight = visibleHeight ?? visible.Height;
        if (GetWindowRect(hwnd, out var movedOuter))
        {
            var movedVisible = GetVisibleRect(hwnd, movedOuter);
            var correctionX = visibleX - movedVisible.Left;
            var correctionY = visibleY - movedVisible.Top;
            var correctionWidth = targetWidth - movedVisible.Width;
            var correctionHeight = targetHeight - movedVisible.Height;
            if (correctionX != 0 || correctionY != 0 || correctionWidth != 0 || correctionHeight != 0)
            {
                PInvoke.SetWindowPos(hwnd, default,
                    movedOuter.Left + correctionX,
                    movedOuter.Top + correctionY,
                    movedOuter.Width + correctionWidth,
                    movedOuter.Height + correctionHeight,
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
            }
        }
    }

    private void Report(string message) => Dispatcher.BeginInvoke(() => StatusText.Text = $"{message} · {DateTime.Now:HH:mm}", DispatcherPriority.Background);

    private struct RectI { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; public RectI(int l, int t, int r, int b) { Left=l; Top=t; Right=r; Bottom=b; } }
}
