using System.Runtime.InteropServices;

namespace RengaMcp.Infrastructure;

// COM retries only a call that Renga rejected as not yet executed. Never retry
// the containing model transaction, paste, or other user operation.
internal sealed class ComMessageFilter : IDisposable
{
    private readonly IMessageFilter _filter = new Filter();
    private IMessageFilter? _previous;
    private bool _registered;
    private bool _initialized;

    public ComMessageFilter()
    {
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        _initialized = true;
        var result = CoRegisterMessageFilter(_filter, out _previous);
        if (result < 0) { Dispose(); Marshal.ThrowExceptionForHR(result); }
        _registered = true;
    }

    public void Dispose()
    {
        if (_registered)
        {
            _ = CoRegisterMessageFilter(_previous, out _);
            _registered = false;
            if (_previous is not null && Marshal.IsComObject(_previous)) Marshal.ReleaseComObject(_previous);
            _previous = null;
        }
        if (_initialized) { OleUninitialize(); _initialized = false; }
        GC.KeepAlive(_filter);
    }

    [ComVisible(true), Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, uint elapsed, IntPtr interfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr callee, uint elapsed, int rejectType);
        [PreserveSig] int MessagePending(IntPtr callee, uint elapsed, int pendingType);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Filter : IMessageFilter
    {
        public int HandleInComingCall(int callType, IntPtr caller, uint elapsed, IntPtr interfaceInfo) => 0;
        public int RetryRejectedCall(IntPtr callee, uint elapsed, int rejectType) =>
            rejectType == 2 && elapsed < 2000 ? 100 : -1;
        public int MessagePending(IntPtr callee, uint elapsed, int pendingType) => 2;
    }

    internal static void PumpMessages()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
        { _ = TranslateMessage(ref message); _ = DispatchMessage(ref message); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void OleUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoRegisterMessageFilter(IMessageFilter? filter, out IMessageFilter? previous);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)] private static extern IntPtr DispatchMessage(ref Message message);
}
