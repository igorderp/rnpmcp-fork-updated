using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RengaMcp.Models;

namespace RengaMcp.Tools;

public sealed partial class RengaTools
{
    [McpServerTool(Name = "renga_get_geometry", ReadOnly = true, Idempotent = true)]
    [Description("Read actual placement, parent level elevation, offsets, dependent IDs and 2D baseline/contour. Lengths are millimetres. Baseline points are in OBJECT-LOCAL coordinates; placement is relative to its parent level. Curved samples are approximate; segment endpoints are exact. Unsupported interfaces are listed explicitly.")]
    public Task<ObjectGeometry> GetGeometry(string uniqueId,
        [Description("Number of samples per top-level curve, 2..256. Polycurve segments also include exact endpoints.")] int samples = 16,
        CancellationToken cancellationToken = default) => adapter.GetObjectGeometryAsync(uniqueId, samples, cancellationToken);

    [McpServerTool(Name = "renga_create_object_at", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create an object at a GLOBAL placement, optionally set its OBJECT-LOCAL polyline baseline/closed floor contour and typed parameters in ONE undoable transaction. Supports windows/doors on an explicit wall host and stairs on a level. Unit axes must be perpendicular. Preview defaults to rollback; preview object IDs do not persist. Read actual placement after host projection. Never treats successful creation as design validation.")]
    public Task<GeometryWriteResult> CreateObjectAt(string typeId, Placement3DValue placement,
        int? hostObjectId = null, int? styleId = null, int? categoryId = null,
        [Description("Ordered local XY vertices. For a closed contour omit the repeated first point.")] Point2DValue[]? baselinePoints = null,
        bool closeBaseline = false, ParameterAssignment[]? parameters = null, bool preview = true,
        CancellationToken cancellationToken = default) => adapter.CreateObjectAtAsync(
            new(typeId, hostObjectId, styleId, categoryId, placement, baselinePoints, closeBaseline, parameters ?? [], preview), cancellationToken);

    [McpServerTool(Name = "renga_set_placement", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Set a level object's placement in its PARENT LEVEL coordinate system, as returned by renga_get_geometry. Millimetres, perpendicular unit axes. Renga restricts moving windows, doors and objects with dependents. Never reconstructs or deletes them implicitly. Preview defaults to rollback.")]
    public Task<GeometryWriteResult> SetPlacement(string uniqueId, Placement3DValue placement, bool preview = true,
        CancellationToken cancellationToken = default) => adapter.SetObjectPlacementAsync(uniqueId, placement, preview, cancellationToken);

    [McpServerTool(Name = "renga_set_baseline", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Replace a supported wall/stair baseline or floor/landing contour with a polyline in OBJECT-LOCAL XY coordinates, in millimetres. Closed contours require at least 3 vertices, no repeated first point or self intersections. Objects with dependents are rejected. Preview defaults to rollback.")]
    public Task<GeometryWriteResult> SetBaseline(string uniqueId, Point2DValue[] points, bool closed = false, bool preview = true,
        CancellationToken cancellationToken = default) => adapter.SetObjectBaselineAsync(uniqueId, points, closed, preview, cancellationToken);

    [McpServerTool(Name = "renga_set_parameters", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Set up to 100 distinct writable parameters atomically. GUIDs come from renga_get_object. Values use invariant formatting and API units. A missing/read-only parameter, parse error or COM failure rolls back the entire operation. Preview defaults to rollback and may not evaluate deferred values. After commit, read renga_get_object to verify actual values; parameterReadbackRequired reports this explicitly.")]
    public Task<GeometryWriteResult> SetParameters(string uniqueId, ParameterAssignment[] parameters, bool preview = true,
        CancellationToken cancellationToken = default) => adapter.SetParametersAsync(uniqueId, parameters, preview, cancellationToken);

    [McpServerTool(Name = "renga_delete_objects", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Delete explicit objects in one undoable operation. Preview reports ALL actually deleted objects, including cascading dependents/connections, then rolls back. Commit requires expectedDeletedUniqueIds exactly matching that full preview set; any mismatch rolls back. No saving or closing the project.")]
    public Task<DeleteObjectsResult> DeleteObjects(string[] uniqueIds, string[]? expectedDeletedUniqueIds = null, bool preview = true,
        CancellationToken cancellationToken = default) => adapter.DeleteObjectsAsync(uniqueIds, expectedDeletedUniqueIds, preview, cancellationToken);

    [McpServerTool(Name = "renga_get_selection", ReadOnly = true, Idempotent = true)]
    [Description("Read selected model objects in the active model/level view.")]
    public Task<SelectionResult> GetSelection(CancellationToken cancellationToken = default) => adapter.GetSelectionAsync(cancellationToken);

    [McpServerTool(Name = "renga_select_objects", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Select explicit object GUIDs in the active model/level view for visual inspection. Empty array clears selection. Changes UI selection only, never model geometry.")]
    public Task<SelectionResult> SelectObjects(string[] uniqueIds, CancellationToken cancellationToken = default) => adapter.SelectObjectsAsync(uniqueIds, cancellationToken);

    [McpServerTool(Name = "renga_get_view", ReadOnly = true, Idempotent = true)]
    [Description("Read active view ID/type and Renga's main-window handle. The handle is not a screenshot coordinate map.")]
    public Task<ViewState> GetView(CancellationToken cancellationToken = default) => adapter.GetViewStateAsync(cancellationToken);

    [McpServerTool(Name = "renga_open_view", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Open the 3D building view, or an existing level's plan when levelId is supplied. Changes UI only; never opens/closes/saves a project.")]
    public Task<ViewState> OpenView(int? levelId = null, CancellationToken cancellationToken = default) => adapter.OpenViewAsync(levelId, cancellationToken);

    [McpServerTool(Name = "renga_get_screenshot", ReadOnly = true, Idempotent = true)]
    [Description("Return a PNG of the ACTIVE MODEL VIEW through Renga's IScreenshotService, independently of Windows desktop capture. The model viewport only: no toolbars/dialogs or screen-coordinate mapping for computer-use clicks. Width/height 64..4096, at most 8 million pixels. Requires a supported live model view; mock has no renderer.")]
    public async Task<CallToolResult> GetScreenshot(int width = 1600, int height = 1000, CancellationToken cancellationToken = default)
    {
        var png = await adapter.GetScreenshotAsync(width, height, cancellationToken).ConfigureAwait(false);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png")] };
    }
}
