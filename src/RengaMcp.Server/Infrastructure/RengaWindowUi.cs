using System.ComponentModel;
using System.Runtime.InteropServices;
using RengaMcp.Models;

namespace RengaMcp.Infrastructure;

// Native, explicitly requested UI access for Renga commands absent from the COM API.
// Every input is bound to a fresh screenshot of the connected Renga process.
internal sealed class RengaWindowUi
{
    private WindowSnapshot? _last;
    private Rect _bounds;

    public void Invalidate() => _last = null;

    public WindowSnapshot Capture(IntPtr mainWindow, int processId, string context)
    {
        using var dpi = new DpiScope();
        _last = null;
        var window = ResolveWindow(mainWindow, processId);
        Focus(window);
        _ = DwmFlush();
        var bounds = ClientBounds(window);
        var width = bounds.Right - bounds.Left; var height = bounds.Bottom - bounds.Top;
        if (width < 1 || height < 1 || (long)width * height > 16_000_000)
            throw new InvalidOperationException("Renga window must be visible and at most 16 million pixels. Resize it and capture again.");
        // Capture actual visible client pixels, including Renga's panels and active modal dialog.
        // Source and clicks use the same physical desktop pixels under Per-Monitor-V2 DPI.
        var desktop = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(desktop);
        var bitmap = CreateCompatibleBitmap(desktop, width, height);
        IntPtr previous = IntPtr.Zero;
        try
        {
            if (desktop == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw NativeError("Cannot allocate window capture");
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, desktop, bounds.Left, bounds.Top, 0x40cc0020)) throw NativeError("Window capture failed");
            _ = SelectObject(memory, previous); previous = IntPtr.Zero;
            var pixels = new byte[checked(width * height * 4)];
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) != height) throw NativeError("Cannot read captured pixels");
            if (ResolveWindow(mainWindow, processId) != window || ClientBounds(window) != bounds || GetForegroundWindow() != window)
                throw new InvalidOperationException("Renga window changed during capture. Capture again.");
            var title = new char[1024]; var titleLength = GetWindowText(window, title, title.Length);
            var png = PngEncoder.EncodeBgra(width, height, pixels);
            if (png.Length > 10 * 1024 * 1024) throw new InvalidOperationException("UI screenshot exceeds 10 MiB; resize the Renga window.");
            _bounds = bounds;
            return _last = new(Guid.NewGuid().ToString("N"), processId, window.ToInt64(), new string(title, 0, Math.Max(0, titleLength)), width, height,
                "physical client pixels: x right, y down, origin top-left of THIS UI PNG; no scaling", window != mainWindow,
                DateTimeOffset.UtcNow, context, png, GetClipboardSequenceNumber());
        }
        finally
        {
            if (previous != IntPtr.Zero) _ = SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) _ = DeleteObject(bitmap);
            if (memory != IntPtr.Zero) _ = DeleteDC(memory);
            if (desktop != IntPtr.Zero) _ = ReleaseDC(IntPtr.Zero, desktop);
        }
    }

    public UiInputResult Input(IntPtr mainWindow, int processId, string context, UiInputRequest request)
    {
        using var dpi = new DpiScope();
        var snapshot = _last ?? throw new InvalidOperationException("Capture renga_ui_snapshot before every input.");
        UiInputValidation.Validate(request, snapshot, context, DateTimeOffset.UtcNow);
        var window = ResolveWindow(mainWindow, processId);
        if (window.ToInt64() != snapshot.WindowHandle || snapshot.ProcessId != processId || ClientBounds(window) != _bounds)
        {
            _last = null;
            throw new InvalidOperationException("Window/dialog moved, resized or changed. Capture renga_ui_snapshot again.");
        }
        if (request.Preview)
            return new(false, "UI plan only: NO input was sent. UI actions cannot be transactionally previewed or automatically rolled back. Use preview=false to execute this single action; then inspect a new screenshot and re-query the model.", snapshot);
        _last = null; // Consume before sending: never replay an input after a partial failure.
        Focus(window);
        if (GetAsyncKeyState(0x10) < 0 || GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0 || GetAsyncKeyState(0x5b) < 0 || GetAsyncKeyState(0x5c) < 0)
            throw new InvalidOperationException("Release Shift/Ctrl/Alt/Windows keys, capture again and retry.");
        if (request.Action == "click")
        {
            var point = new Point { X = _bounds.Left + request.X!.Value, Y = _bounds.Top + request.Y!.Value };
            var hit = GetAncestor(WindowFromPoint(point), 2);
            _ = GetWindowThreadProcessId(hit, out var hitPid);
            if (hitPid != processId || (hit != window && GetAncestor(hit, 3) != GetAncestor(mainWindow, 3)))
                throw new InvalidOperationException("Click is obscured by another window. Capture again.");
            if (!SetCursorPos(point.X, point.Y)) throw NativeError("Cannot position pointer");
            ushort modifier = request.Modifier switch { "ctrl" => 0x11, "alt" => 0x12, "shift" => 0x10, _ => 0 };
            try
            {
                var mouse = new[] { Mouse(request.Button, false), Mouse(request.Button, true) };
                var clicks = request.DoubleClick ? mouse.Concat(mouse) : mouse;
                Send(modifier == 0 ? clicks.ToArray() : new[] { Keyboard(modifier, 0, 0) }.Concat(clicks).Append(Keyboard(modifier, 0, 2)).ToArray());
            }
            finally { if (modifier != 0) Send(Keyboard(modifier, 0, 2)); }
        }
        else if (request.Action == "key")
        {
            var keys = UiInputValidation.Keys[request.Key!];
            try { Send(keys.Select(k => Keyboard(k, 0, 0)).Concat(keys.Reverse().Select(k => Keyboard(k, 0, 2))).ToArray()); }
            finally { Send(keys.Reverse().Select(k => Keyboard(k, 0, 2)).ToArray()); }
        }
        else
        {
            Send(request.Text!.SelectMany(c => new[] { Keyboard(0, c, 4), Keyboard(0, c, 6) }).ToArray());
        }
        // Let Renga drain queued native input before taking the next observation.
        _ = SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 2, 1500, out _);
        return new(true, "Native UI input sent, not a verified model edit. Paste/Create copy/Move can require further placement or confirmation. Re-query objects/levels to verify completion. Native UI has no automatic rollback and may replace the system clipboard.",
            Capture(mainWindow, processId, context));
    }

    private static IntPtr ResolveWindow(IntPtr main, int pid)
    {
        if (!IsWindow(main) || GetWindowThreadProcessId(main, out var actual) == 0 || actual != pid)
            throw new InvalidOperationException("Connected Renga window/process is no longer available.");
        if (IsWindowEnabled(main))
        {
            var foreground = GetForegroundWindow();
            if (foreground != main && IsWindowVisible(foreground) && GetAncestor(foreground, 3) == main &&
                GetWindowThreadProcessId(foreground, out actual) != 0 && actual == pid) return foreground;
            return main;
        }
        var popup = GetLastActivePopup(main);
        if (popup == main || !IsWindowVisible(popup) || GetWindowThreadProcessId(popup, out actual) == 0 || actual != pid)
            throw new InvalidOperationException("Renga has a modal window that cannot be safely targeted.");
        return popup;
    }

    private static void Focus(IntPtr window)
    {
        if (IsIconic(window)) _ = ShowWindow(window, 9);
        if (GetForegroundWindow() != window) _ = SetForegroundWindow(window);
        if (GetForegroundWindow() != window) throw new InvalidOperationException("Windows denied focus. Bring Renga to the foreground and capture again. No input was sent.");
    }

    private static Rect ClientBounds(IntPtr window)
    {
        if (!GetClientRect(window, out var r)) throw NativeError("Cannot read Renga client bounds");
        var origin = new Point();
        if (!ClientToScreen(window, ref origin)) throw NativeError("Cannot map Renga coordinates");
        return new(origin.X, origin.Y, origin.X + r.Right, origin.Y + r.Bottom);
    }

    private static NativeInput Keyboard(ushort key, ushort scan, uint flags) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Scan = scan, Flags = flags } } };
    private static NativeInput Mouse(string button, bool up) => new() { Type = 0, Data = new() { Mouse = new() { Flags = button == "left" ? (up ? 4u : 2u) : (up ? 16u : 8u) } } };
    private static void Send(params NativeInput[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
            throw NativeError("Windows did not accept all input (possibly a privilege mismatch). Outcome is uncertain; capture again before any retry");
    }
    private static Win32Exception NativeError(string message) => new(Marshal.GetLastWin32Error(), message);

    private sealed class DpiScope : IDisposable
    {
        private readonly IntPtr _previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        public void Dispose() { if (_previous != IntPtr.Zero) _ = SetThreadDpiAwarenessContext(_previous); }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Rect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint ColorsUsed, ColorsImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, [Out] char[] title, int maximum);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flag);
    [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError=true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] pixels, ref BitmapInfo info, uint usage);
}
