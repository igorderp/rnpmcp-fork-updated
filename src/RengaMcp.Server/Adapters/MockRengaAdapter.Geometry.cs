using RengaMcp.Models;

namespace RengaMcp.Adapters;

public sealed partial class MockRengaAdapter
{
    private readonly Dictionary<Guid, ObjectGeometry> _geometry = Objects.ToDictionary(obj => Guid.Parse(obj.UniqueId), MakeMockGeometry);
    private SelectionResult _selection = new([]);
    private ViewState _view = new(0, 1, 0, "Mock view");
    private int _nextId = 1000;

    private static ObjectGeometry MakeMockGeometry(RengaObjectSummary obj) => new(obj, "mm",
        new(new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)), 1, 0, 0, 0, 0, null, [], []);

    private ObjectGeometry GetMockGeometry(string uniqueId)
    {
        RequireConnection();
        if (!Guid.TryParse(uniqueId, out var id)) throw new ArgumentException("uniqueId must be a GUID.", nameof(uniqueId));
        return _geometry.TryGetValue(id, out var geometry) ? geometry : throw new KeyNotFoundException("Mock object was not found.");
    }

    public Task<ObjectGeometry> GetObjectGeometryAsync(string uniqueId, int samples = 16, CancellationToken cancellationToken = default)
    {
        if (samples is < 2 or > 256) throw new ArgumentOutOfRangeException(nameof(samples));
        return Task.FromResult(GetMockGeometry(uniqueId));
    }

    public Task<GeometryWriteResult> CreateObjectAtAsync(CreateGeometryRequest request, CancellationToken cancellationToken = default)
    {
        RequireConnection();
        GeometryValidation.Placement(request.Placement); GeometryValidation.Assignments(request.Parameters);
        if (request.BaselinePoints is not null) GeometryValidation.Baseline(request.BaselinePoints, request.CloseBaseline);
        var type = CreationCatalog.Types.FirstOrDefault(t => Guid.TryParse(request.TypeId, out var id) && Guid.Parse(t.TypeId) == id)
            ?? throw new ArgumentException("Unknown creation type.", nameof(request));
        if (type.RequiresHost && request.HostObjectId is null) throw new ArgumentException("Host is required.", nameof(request));
        if (request.HostObjectId is int host && !_geometry.Values.Any(g => g.ModelObject.LocalId == host))
            throw new KeyNotFoundException("Mock host does not exist.");
        var obj = new RengaObjectSummary(_nextId, Guid.NewGuid().ToString("B"), type.TypeId, "Mock " + type.Name, false);
        var geometry = MakeMockGeometry(obj) with { Placement = request.Placement, Baseline = request.BaselinePoints is null ? null : MockCurve(request.BaselinePoints, request.CloseBaseline) };
        if (!request.Preview) { _geometry.Add(Guid.Parse(obj.UniqueId), geometry); _nextId++; }
        return Task.FromResult(new GeometryWriteResult(!request.Preview, !request.Preview, "Synthetic mock operation.", null, geometry));
    }

    public Task<GeometryWriteResult> SetObjectPlacementAsync(string uniqueId, Placement3DValue placement, bool preview = true, CancellationToken cancellationToken = default)
    {
        GeometryValidation.Placement(placement);
        var before = GetMockGeometry(uniqueId);
        return Task.FromResult(StoreMock(before, before with { Placement = placement }, preview));
    }

    public Task<GeometryWriteResult> SetObjectBaselineAsync(string uniqueId, IReadOnlyList<Point2DValue> points, bool closed, bool preview = true, CancellationToken cancellationToken = default)
    {
        GeometryValidation.Baseline(points, closed);
        var before = GetMockGeometry(uniqueId);
        return Task.FromResult(StoreMock(before, before with { Baseline = MockCurve(points, closed) }, preview));
    }

    public Task<GeometryWriteResult> SetParametersAsync(string uniqueId, IReadOnlyList<ParameterAssignment> parameters, bool preview = true, CancellationToken cancellationToken = default)
    {
        GeometryValidation.Assignments(parameters);
        var before = GetMockGeometry(uniqueId);
        return Task.FromResult(StoreMock(before, before, preview));
    }

    private GeometryWriteResult StoreMock(ObjectGeometry before, ObjectGeometry after, bool preview)
    {
        if (!preview) _geometry[Guid.Parse(after.ModelObject.UniqueId)] = after;
        return new(!preview, !preview, "Synthetic mock operation.", before, after);
    }

    private static Curve2DValue MockCurve(IReadOnlyList<Point2DValue> points, bool closed)
    {
        var vertices = closed ? points.Append(points[0]).ToArray() : points.ToArray();
        var length = vertices.Zip(vertices.Skip(1), (a, b) => Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y))).Sum();
        return new(vertices.Length == 2 ? 1 : 0, closed, length, vertices[0], vertices[^1], vertices, false);
    }

    public Task<DeleteObjectsResult> DeleteObjectsAsync(IReadOnlyList<string> uniqueIds, IReadOnlyList<string>? expectedDeletedUniqueIds, bool preview = true, CancellationToken cancellationToken = default)
    {
        RequireConnection();
        if (uniqueIds.Count is < 1 or > 500) throw new ArgumentException("Provide 1..500 IDs.", nameof(uniqueIds));
        var removed = uniqueIds.Select(id => GetMockGeometry(id).ModelObject).ToArray();
        var actual = removed.Select(obj => Guid.Parse(obj.UniqueId)).ToHashSet();
        if (actual.Count != removed.Length) throw new ArgumentException("IDs must be distinct.", nameof(uniqueIds));
        if (!preview && expectedDeletedUniqueIds is null) throw new ArgumentException("Commit requires expectedDeletedUniqueIds.", nameof(expectedDeletedUniqueIds));
        if (expectedDeletedUniqueIds is not null && !actual.SetEquals(expectedDeletedUniqueIds.Select(Guid.Parse)))
            throw new InvalidOperationException("Deletion differs from expectedDeletedUniqueIds.");
        if (!preview) foreach (var id in actual) _geometry.Remove(id);
        return Task.FromResult(new DeleteObjectsResult(!preview, !preview, removed));
    }

    public Task<SelectionResult> GetSelectionAsync(CancellationToken cancellationToken = default) { RequireConnection(); return Task.FromResult(_selection); }
    public Task<SelectionResult> SelectObjectsAsync(IReadOnlyList<string> uniqueIds, CancellationToken cancellationToken = default)
    {
        _selection = new(uniqueIds.Select(id => GetMockGeometry(id).ModelObject).ToArray());
        return Task.FromResult(_selection);
    }
    public Task<ViewState> GetViewStateAsync(CancellationToken cancellationToken = default) { RequireConnection(); return Task.FromResult(_view); }
    public Task<ViewState> OpenViewAsync(int? levelId = null, CancellationToken cancellationToken = default)
    {
        RequireConnection(); _view = new(0, levelId ?? 1, levelId is null ? 0 : 1, "Mock view"); return Task.FromResult(_view);
    }
    public Task<byte[]> GetScreenshotAsync(int width = 1600, int height = 1000, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Mock mode has no renderer. Screenshot capture requires a live Renga model view.");
}
