using System.Reflection;
using System.Runtime.InteropServices;
using RengaMcp.Infrastructure;
using RengaMcp.Models;

namespace RengaMcp.Adapters;

public sealed partial class DynamicComRengaAdapter
{
    public Task<ObjectGeometry> GetObjectGeometryAsync(string uniqueId, int samples = 16, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() => WithModelObject(uniqueId, (_, objects, modelObject) => ReadGeometry(objects, modelObject, samples)), cancellationToken);

    private T WithModelObject<T>(string uniqueId, Func<object, object, object, T> action)
    {
        if (!Guid.TryParse(uniqueId, out var id)) throw new ArgumentException("uniqueId must be a GUID.", nameof(uniqueId));
        var application = RequireApplicationWithProject();
        var project = RequireComObject(ComDispatch.Get(application, "Project"), "project");
        object? model = null;
        object? objects = null;
        object? modelObject = null;
        try
        {
            model = RequireComObject(ComDispatch.Get(project, "Model"), "model");
            objects = RequireComObject(ComDispatch.Call(model, "GetObjects"), "model objects");
            modelObject = FindModelObject(objects, id);
            return action(model, objects, modelObject);
        }
        finally
        {
            ComDispatch.Release(modelObject);
            ComDispatch.Release(objects);
            ComDispatch.Release(model);
            ComDispatch.Release(project);
        }
    }

    private static ObjectGeometry ReadGeometry(object objects, object modelObject, int samples)
    {
        if (samples is < 2 or > 256) throw new ArgumentOutOfRangeException(nameof(samples), "samples must be 2..256.");
        var missing = new List<string>();
        Placement3DValue? placement = null;
        Curve2DValue? baseline = null;
        int? levelId = null;
        double? levelElevation = null;
        double? placementElevation = null;
        double? offset = null;
        double? elevation = null;
        var levelObject = OptionalInterface(modelObject, "ILevelObject");
        try
        {
            if (levelObject is not null)
            {
                levelId = ComDispatch.Get<int>(levelObject, "LevelId");
                placementElevation = ComDispatch.Get<double>(levelObject, "PlacementElevation");
                offset = ComDispatch.Get<double>(levelObject, "VerticalOffset");
                elevation = ComDispatch.Get<double>(levelObject, "ElevationAboveLevel");
                var p = RequireComObject(ComDispatch.Call(levelObject, "GetPlacement"), "placement");
                try { placement = ReadPlacement(p); }
                finally { ComDispatch.Release(p); }
                if (levelId > 0)
                {
                    var level = ComDispatch.Call(objects, "GetById", levelId.Value);
                    if (level is not null)
                    {
                        try
                        {
                            var info = ComDispatch.Call(level, "GetInterfaceByName", "ILevel");
                            try { if (info is not null) levelElevation = ComDispatch.Get<double>(info, "Elevation"); }
                            finally { ComDispatch.Release(info); }
                        }
                        finally { ComDispatch.Release(level); }
                    }
                }
            }
            else missing.Add("ILevelObject");
        }
        finally { ComDispatch.Release(levelObject); }

        var baselineObject = OptionalInterface(modelObject, "IBaseline2DObject");
        try
        {
            if (baselineObject is null) missing.Add("IBaseline2DObject");
            else
            {
                var curve = RequireComObject(ComDispatch.Call(baselineObject, "GetBaseline"), "baseline");
                try { baseline = ReadCurve(curve, samples); }
                finally { ComDispatch.Release(curve); }
            }
        }
        finally { ComDispatch.Release(baselineObject); }
        return new ObjectGeometry(ReadObjectSummary(modelObject), "mm", placement, levelId, levelElevation,
            placementElevation, offset, elevation, baseline, missing, ReadDependentIds(modelObject));
    }

    private static Placement3DValue ReadPlacement(object placement) => new(
        GeometryCom.GetPoint3(placement, "Origin"),
        GeometryCom.GetPoint3(placement, "AxisX"),
        GeometryCom.GetPoint3(placement, "AxisZ"));

    private static Curve2DValue ReadCurve(object curve, int samples, int depth = 0)
    {
        var start = GeometryCom.GetPoint2(curve, "GetBeginPoint");
        var end = GeometryCom.GetPoint2(curve, "GetEndPoint");
        var min = ComDispatch.Get<double>(curve, "MinParameter");
        var max = ComDispatch.Get<double>(curve, "MaxParameter");
        var points = Enumerable.Range(0, samples + 1).Select(index =>
            GeometryCom.GetPoint2(curve, "GetPointOn", min + (max - min) * index / samples)).ToArray();
        var type = ComDispatch.Get<int>(curve, "Curve2DType");
        IReadOnlyList<Curve2DValue>? segments = null;
        var poly = OptionalInterface(curve, "IPolyCurve2D");
        try
        {
            if (poly is not null)
            {
                var count = ComDispatch.Call<int>(poly, "GetSegmentCount");
                if (count > 256 || depth > 8) throw new NotSupportedException("Curve is too complex; at most 256 segments per polycurve and 8 nesting levels are supported.");
                var children = new List<Curve2DValue>();
                for (var i = 0; i < count; i++)
                {
                    var segment = RequireComObject(ComDispatch.Call(poly, "GetSegment", i), "curve segment");
                    try { children.Add(ReadCurve(segment, 2, depth + 1)); }
                    finally { ComDispatch.Release(segment); }
                }
                segments = children;
            }
        }
        finally { ComDispatch.Release(poly); }
        return new Curve2DValue(type, ComDispatch.Call<bool>(curve, "IsClosed"),
            ComDispatch.Call<double>(curve, "GetLength"), start, end, points, type != 1, segments);
    }

    private static object? OptionalInterface(object target, string name)
    {
        try { return ComDispatch.Call(target, "GetInterfaceByName", name); }
        catch (Exception exception) when (IsMissingInterface(exception)) { return null; }
    }

    private static bool IsMissingInterface(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocation
            ? IsMissingInterface(invocation.InnerException!)
            : exception is COMException or InvalidCastException && (uint)exception.HResult == 0x80004002;

}
