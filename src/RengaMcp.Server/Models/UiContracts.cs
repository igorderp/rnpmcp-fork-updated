using System.Text.Json.Serialization;

namespace RengaMcp.Models;

public sealed record LevelInfo(RengaObjectSummary ModelObject, double ElevationMm, string LevelName,
    Placement3DValue Placement, bool ElevationWritableThroughApi);

public sealed record MoveObjectsResult(bool Committed, bool UndoRecorded, Point3DValue TranslationMm,
    IReadOnlyList<ObjectGeometry> Before, IReadOnlyList<ObjectGeometry> After);

public sealed record WindowSnapshot(string SnapshotId, int ProcessId, long WindowHandle, string Title,
    int Width, int Height, string CoordinateSpace, bool IsDialog, DateTimeOffset CapturedAt,
    string Context, [property: JsonIgnore] byte[] Png, uint ClipboardSequenceNumber = 0);

public sealed record UiInputRequest(string SnapshotId, string Action, string? Key = null, string? Text = null,
    int? X = null, int? Y = null, string Button = "left", bool DoubleClick = false, bool Preview = true,
    string Modifier = "none");

public sealed record UiInputResult(bool Executed, string Message, WindowSnapshot Snapshot);

public static class UiInputValidation
{
    // Fixed native commands only. No arbitrary key chords, shell commands or window handles.
    public static readonly IReadOnlyDictionary<string, ushort[]> Keys = new Dictionary<string, ushort[]>(StringComparer.Ordinal)
    {
        ["copy"] = [0x11, 0x43], ["paste"] = [0x11, 0x56],
        ["create_copy"] = [0x11, 0x10, 0x43], ["move"] = [0x4d],
        ["escape"] = [0x1b], ["enter"] = [0x0d], ["tab"] = [0x09],
        ["shift_tab"] = [0x10, 0x09], ["select_all"] = [0x11, 0x41],
        ["project_explorer"] = [0x11, 0x10, 0x4e],
        ["up"] = [0x26], ["down"] = [0x28], ["left"] = [0x25], ["right"] = [0x27],
        ["home"] = [0x24], ["end"] = [0x23], ["backspace"] = [0x08]
    };

    public static void Validate(UiInputRequest request, WindowSnapshot snapshot, string currentContext,
        DateTimeOffset now)
    {
        if (request.SnapshotId != snapshot.SnapshotId || snapshot.Context != currentContext)
            throw new InvalidOperationException("Snapshot is stale or belongs to another project/view. Capture renga_ui_snapshot again.");
        if (now - snapshot.CapturedAt > TimeSpan.FromSeconds(60) || now < snapshot.CapturedAt)
            throw new InvalidOperationException("Snapshot expired after 60 seconds. Capture renga_ui_snapshot again.");
        if (request.Modifier is not ("none" or "ctrl" or "alt" or "shift") ||
            (request.Modifier != "none" && (request.Action != "click" || request.Button != "left")))
            throw new ArgumentException("Modifier none/ctrl/alt/shift is supported only for left clicks.");
        switch (request.Action)
        {
            case "key":
                if (request.Key is null || !Keys.ContainsKey(request.Key) || request.Text is not null || request.X is not null || request.Y is not null || request.DoubleClick)
                    throw new ArgumentException("Provide one supported named key and no text/click fields.");
                break;
            case "text":
                if (string.IsNullOrEmpty(request.Text) || request.Text.Length > 2000 || request.Text.Any(char.IsControl) || request.Key is not null || request.X is not null || request.Y is not null || request.DoubleClick)
                    throw new ArgumentException("Text must contain 1..2000 characters, no control characters or key/click fields. Focus the intended field first.");
                break;
            case "click":
                if (request.X is not int x || request.Y is not int y || x < 0 || y < 0 || x >= snapshot.Width || y >= snapshot.Height || request.Button is not ("left" or "right") || request.Key is not null || request.Text is not null || (request.DoubleClick && request.Button != "left"))
                    throw new ArgumentException("Use an in-bounds coordinate from this UI screenshot, a left/right button and no key/text fields.");
                break;
            default: throw new ArgumentException("Action must be key, text, or click.");
        }
    }
}
