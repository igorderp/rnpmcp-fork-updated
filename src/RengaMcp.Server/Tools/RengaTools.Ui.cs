using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RengaMcp.Models;

namespace RengaMcp.Tools;

public sealed partial class RengaTools
{
    [McpServerTool(Name = "renga_get_levels", ReadOnly = true, Idempotent = true)]
    [Description("Read every building level's GUID, name, exact global elevation in millimetres, placement and whether its elevation parameter is writable through the installed API. In API 2.49 LevelElevation is read-only: edit the existing level via native UI tools, then re-read this tool. Do not claim that a no-op parameter write changed the elevation.")]
    public Task<IReadOnlyList<LevelInfo>> GetLevels(CancellationToken cancellationToken = default) => adapter.GetLevelsAsync(cancellationToken);

    [McpServerTool(Name = "renga_move_objects", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Translate 1..500 explicit object GUIDs by global X/Y/Z millimetres in ONE undoable API operation, preserving axes and baselines. Preview defaults to real execution plus rollback. Rejects pinned objects, levels, hosts with dependents and any object for which Renga ignores/refuses SetPlacement. Failure rolls back the whole batch. Use native UI move for unsupported types, windows/doors or hosts. Re-read geometry after commit.")]
    public Task<MoveObjectsResult> MoveObjects(string[] uniqueIds, Point3DValue translationMm, bool preview = true,
        CancellationToken cancellationToken = default) => adapter.MoveObjectsAsync(uniqueIds, translationMm, preview, cancellationToken);

    [McpServerTool(Name = "renga_ui_snapshot", ReadOnly = true, Idempotent = false)]
    [Description("Capture the connected Renga WINDOW CLIENT AREA as PNG, including panels or the active modal dialog. Brings Renga to foreground, no model edit. Returns a one-use snapshotId valid for 60s with exact physical pixel coordinates, width/height, dialog state and project/view context. These pixels ARE usable by renga_ui_click. They are NOT the coordinates of renga_get_screenshot. Read the image before every UI input. Never copy project content to external apps without user authorization.")]
    public Task<CallToolResult> UiSnapshot(CancellationToken cancellationToken = default) =>
        UiCall(async () => SnapshotResult(await adapter.GetUiSnapshotAsync(cancellationToken).ConfigureAwait(false)));

    [McpServerTool(Name = "renga_ui_key", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Send ONE fixed native Renga command after inspecting a fresh renga_ui_snapshot: copy=Ctrl+C, paste=Ctrl+V, create_copy=Ctrl+Shift+C, move=M, project_explorer=Ctrl+Shift+N, escape, enter, tab, shift_tab, select_all, up/down/left/right, home/end, backspace. Enables Renga's own copy/paste without reconstructing objects. For a complete level, select its designation in 3D and Ctrl-click its observed handle using ui_click(modifier=ctrl); Renga copies its contents too. COPY/PASTE affects the system clipboard. Paste/Create copy/Move can leave interactive placement pending; inspect screenshots, finish once, then verify object IDs and level elevations. preview=true ONLY plans and sends NO keys; UI has NO transactional rollback. Explicit preview=false executes. No project save, arbitrary hotkeys or HWNDs.")]
    public Task<CallToolResult> UiKey(string snapshotId, string key, bool preview = true, CancellationToken cancellationToken = default) =>
        UiCall(async () => InputResult(await adapter.SendUiInputAsync(new(snapshotId, "key", Key: key, Preview: preview), cancellationToken).ConfigureAwait(false)));

    [McpServerTool(Name = "renga_ui_click", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Click one observed coordinate in the exact fresh renga_ui_snapshot PNG, in PHYSICAL CLIENT pixels (x,y). Use to select objects/level designations, place copied/moved objects, click a level handle for elevation editing or interact with Renga dialogs. Never use model-only screenshot coordinates. button=left/right; optional left doubleClick. modifier=none/ctrl/alt/shift for left clicks: Ctrl+level handle makes a native copy including its contents. Each actual input consumes snapshotId; returns next UI image. Changed/expired snapshots and obscured click targets are rejected. preview=true plans only; explicit preview=false sends input, no automatic rollback.")]
    public Task<CallToolResult> UiClick(string snapshotId, int x, int y, string button = "left", bool doubleClick = false, string modifier = "none",
        bool preview = true, CancellationToken cancellationToken = default) =>
        UiCall(async () => InputResult(await adapter.SendUiInputAsync(new(snapshotId, "click", X: x, Y: y, Button: button, DoubleClick: doubleClick, Preview: preview, Modifier: modifier), cancellationToken).ConfigureAwait(false)));

    [McpServerTool(Name = "renga_ui_type_text", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Type literal Unicode text into the CURRENTLY FOCUSED Renga field after inspecting its fresh UI snapshot. Does not alter clipboard. Focus/select the desired field first with ui_click and select_all. For level elevation, select its designation in 3D, click its HANDLE and enter the new position in the native dynamic input field; LevelElevation is API read-only. Native UI numbers follow the displayed UI units and locale (API levels are mm); verify whether the field is an absolute elevation or relative distance. Control characters, tabs/newlines and >2000 chars rejected; send enter separately. Re-read renga_get_levels after confirming. preview=true plans only; explicit preview=false types, no automatic rollback.")]
    public Task<CallToolResult> UiTypeText(string snapshotId, string text, bool preview = true, CancellationToken cancellationToken = default) =>
        UiCall(async () => InputResult(await adapter.SendUiInputAsync(new(snapshotId, "text", Text: text, Preview: preview), cancellationToken).ConfigureAwait(false)));

    private static async Task<CallToolResult> UiCall(Func<Task<CallToolResult>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error.Message }] };
        }
    }

    private static CallToolResult SnapshotResult(WindowSnapshot snapshot) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(snapshot) }, ImageContentBlock.FromBytes(snapshot.Png, "image/png")]
    };
    private static CallToolResult InputResult(UiInputResult result) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result) }, ImageContentBlock.FromBytes(result.Snapshot.Png, "image/png")]
    };
}
