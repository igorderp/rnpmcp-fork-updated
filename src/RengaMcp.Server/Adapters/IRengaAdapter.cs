using RengaMcp.Models;

namespace RengaMcp.Adapters;

public interface IRengaAdapter : IDisposable
{
    Task<ObjectGeometry> GetObjectGeometryAsync(string uniqueId, int samples = 16, CancellationToken cancellationToken = default);
    Task<GeometryWriteResult> CreateObjectAtAsync(CreateGeometryRequest request, CancellationToken cancellationToken = default);
    Task<GeometryWriteResult> SetObjectPlacementAsync(string uniqueId, Placement3DValue placement, bool preview = true, CancellationToken cancellationToken = default);
    Task<GeometryWriteResult> SetObjectBaselineAsync(string uniqueId, IReadOnlyList<Point2DValue> points, bool closed, bool preview = true, CancellationToken cancellationToken = default);
    Task<GeometryWriteResult> SetParametersAsync(string uniqueId, IReadOnlyList<ParameterAssignment> parameters, bool preview = true, CancellationToken cancellationToken = default);
    Task<DeleteObjectsResult> DeleteObjectsAsync(IReadOnlyList<string> uniqueIds, IReadOnlyList<string>? expectedDeletedUniqueIds, bool preview = true, CancellationToken cancellationToken = default);
    Task<SelectionResult> GetSelectionAsync(CancellationToken cancellationToken = default);
    Task<SelectionResult> SelectObjectsAsync(IReadOnlyList<string> uniqueIds, CancellationToken cancellationToken = default);
    Task<ViewState> GetViewStateAsync(CancellationToken cancellationToken = default);
    Task<ViewState> OpenViewAsync(int? levelId = null, CancellationToken cancellationToken = default);
    Task<byte[]> GetScreenshotAsync(int width = 1600, int height = 1000, CancellationToken cancellationToken = default);
    Task<RengaStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RengaInstanceInfo>> ListInstancesAsync(
        CancellationToken cancellationToken = default);

    Task<ConnectionResult> ConnectAsync(
        int? processId,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<ProjectSummary> GetProjectInfoAsync(CancellationToken cancellationToken = default);

    Task<ObjectPage> QueryObjectsAsync(
        ObjectQuery query,
        CancellationToken cancellationToken = default);

    Task<RengaObjectDetails> GetObjectAsync(
        string uniqueId,
        bool includeParameters,
        bool includeProperties,
        int maxValues,
        CancellationToken cancellationToken = default);

    Task<StylePage> ListStylesAsync(
        string collection,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    Task<CreateObjectResult> CreateObjectAsync(
        CreateObjectRequest request,
        CancellationToken cancellationToken = default);

    Task<ParameterUpdateResult> SetParameterAsync(
        string objectUniqueId,
        string parameterId,
        string value,
        bool preview,
        CancellationToken cancellationToken = default);

    Task<ObjectPortsResult> GetPortsAsync(
        string objectUniqueId,
        CancellationToken cancellationToken = default);

    Task<PipeConnectionResult> CreatePipeConnectionAsync(
        PipeConnectionRequest request,
        CancellationToken cancellationToken = default);
}
