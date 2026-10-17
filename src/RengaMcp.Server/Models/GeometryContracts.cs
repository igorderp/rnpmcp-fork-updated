namespace RengaMcp.Models;

public sealed record Point2DValue(double X, double Y);

public sealed record Placement3DValue(Point3DValue Origin, Point3DValue AxisX, Point3DValue AxisZ);

public sealed record Curve2DValue(
    int CurveType, bool Closed, double Length,
    Point2DValue Start, Point2DValue End,
    IReadOnlyList<Point2DValue> Samples,
    bool SamplesAreApproximate,
    IReadOnlyList<Curve2DValue>? Segments = null);

public sealed record ObjectGeometry(
    RengaObjectSummary ModelObject,
    string LengthUnit,
    Placement3DValue? Placement,
    int? LevelId,
    double? LevelElevation,
    double? PlacementElevation,
    double? VerticalOffset,
    double? ElevationAboveLevel,
    Curve2DValue? Baseline,
    IReadOnlyList<string> Unavailable,
    IReadOnlyList<int>? DependentObjectIds = null);

public sealed record ParameterAssignment(string ParameterId, string Value);

public sealed record CreateGeometryRequest(
    string TypeId, int? HostObjectId, int? StyleId, int? CategoryId,
    Placement3DValue Placement,
    IReadOnlyList<Point2DValue>? BaselinePoints,
    bool CloseBaseline,
    IReadOnlyList<ParameterAssignment> Parameters,
    bool Preview = true);

public sealed record GeometryWriteResult(
    bool Committed, bool UndoRecorded, string Message,
    ObjectGeometry? Before, ObjectGeometry After,
    IReadOnlyList<ParameterAssignment>? RequestedParameters = null,
    bool ParameterReadbackRequired = false);

public sealed record SelectionResult(IReadOnlyList<RengaObjectSummary> Objects);

public sealed record ViewState(int MainWindowHandle, int? ViewId, int? ViewType, string? ViewName);

public sealed record DeleteObjectsResult(bool Committed, bool UndoRecorded, IReadOnlyList<RengaObjectSummary> DeletedObjects);
