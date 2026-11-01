using System.Reflection;
using System.Runtime.InteropServices;

namespace RengaMcp.Infrastructure;

public static class ComFailures
{
    public static Exception Unwrap(Exception error) => error switch
    {
        TargetInvocationException { InnerException: not null } wrapped => Unwrap(wrapped.InnerException!),
        AggregateException aggregate when aggregate.InnerExceptions.Count == 1 => Unwrap(aggregate.InnerExceptions[0]),
        _ => error
    };

    public static bool IsDisconnected(Exception error)
    {
        error = Unwrap(error);
        return error is InvalidComObjectException || error is COMException && (uint)error.HResult is
            0x80010108 or 0x800401fd or 0x800706ba or 0x800706be;
    }

    public static bool IsBusy(Exception error) => Unwrap(error) is COMException com &&
        (uint)com.HResult is 0x80010001 or 0x8001010a or 0x8001010b;

    public static string Describe(Exception error)
    {
        error = Unwrap(error);
        if (IsDisconnected(error))
            return $"Renga COM connection was lost (0x{error.HResult:X8}). The operation was NOT replayed. Read renga_status to reconnect before checking the model and retrying deliberately.";
        if (IsBusy(error))
            return $"Renga is busy or has an open modal operation (0x{error.HResult:X8}). Finish that operation/dialog, then try again. This is not an MCP transport failure; the whole operation was NOT replayed.";
        return error is COMException ? $"Renga COM error 0x{error.HResult:X8}: {error.Message}" : error.Message;
    }
}
