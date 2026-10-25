using RengaMcp.Infrastructure;
using RengaMcp.Models;

namespace RengaMcp.Adapters;

public sealed partial class DynamicComRengaAdapter
{
    private readonly RengaWindowUi _windowUi = new();

    public Task<IReadOnlyList<LevelInfo>> GetLevelsAsync(CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync<IReadOnlyList<LevelInfo>>(() =>
        {
            var project = RequireComObject(ComDispatch.Get(RequireApplicationWithProject(), "Project"), "project");
            object? model = null; object? objects = null;
            try
            {
                model = RequireComObject(ComDispatch.Get(project, "Model"), "model");
                objects = RequireComObject(ComDispatch.Call(model, "GetObjects"), "model objects");
                var levels = new List<LevelInfo>();
                for (var index = 0; index < ComDispatch.Get<int>(objects, "Count"); index++)
                {
                    var obj = RequireComObject(ComDispatch.Call(objects, "GetByIndex", index), "model object");
                    try
                    {
                        if (Guid.Parse(ComDispatch.Get<string>(obj, "ObjectTypeS")) == Guid.Parse(CreationCatalog.Types[0].TypeId))
                            levels.Add(ReadLevel(obj));
                    }
                    finally { ComDispatch.Release(obj); }
                }
                return levels.OrderBy(level => level.ElevationMm).ToArray();
            }
            finally { ComDispatch.Release(objects); ComDispatch.Release(model); ComDispatch.Release(project); }
        }, cancellationToken);

    private static LevelInfo ReadLevel(object obj)
    {
        var level = RequireComObject(ComDispatch.Call(obj, "GetInterfaceByName", "ILevel"), "level");
        object? placement = null; object? parameters = null; object? elevation = null;
        try
        {
            placement = RequireComObject(ComDispatch.Get(level, "Placement"), "level placement");
            parameters = RequireComObject(ComDispatch.Call(obj, "GetParameters"), "level parameters");
            elevation = RequireComObject(ComDispatch.Call(parameters, "GetS", "{440A20F8-42B8-4A5F-9000-39EF58E0302B}"), "level elevation");
            return new(ReadObjectSummary(obj), ComDispatch.Get<double>(level, "Elevation"),
                ComDispatch.Get<string>(level, "LevelName"), ReadPlacement(placement), !ComDispatch.Get<bool>(elevation, "IsReadOnly"));
        }
        finally { ComDispatch.Release(elevation); ComDispatch.Release(parameters); ComDispatch.Release(placement); ComDispatch.Release(level); }
    }

    public Task<MoveObjectsResult> MoveObjectsAsync(IReadOnlyList<string> uniqueIds, Point3DValue translationMm,
        bool preview = true, CancellationToken cancellationToken = default) => _worker.InvokeAsync(() =>
    {
        var ids = ValidateObjectIds(uniqueIds, false);
        ValidateTranslation(translationMm);
        var (snapshots, undo) = InEdit(preview, (_, _, objects) =>
        {
            var targets = new List<object>();
            try
            {
                var before = new List<ObjectGeometry>();
                // Resolve and validate the complete batch before moving any object.
                foreach (var id in ids)
                {
                    var obj = FindModelObject(objects, id); targets.Add(obj);
                    if (ComDispatch.Get<bool>(obj, "Pinned")) throw new InvalidOperationException("A requested object is pinned; the entire move was rolled back.");
                    RequireNoDependents(obj);
                    var geometry = ReadGeometry(objects, obj, 16);
                    if (geometry.Placement is null) throw new NotSupportedException("This object has no editable ILevelObject placement. Use native UI move for levels/other unsupported objects.");
                    before.Add(geometry);
                }
                var math = RequireComObject(ComDispatch.Get(RequireApplicationWithProject(), "Math"), "math");
                try
                {
                    for (var i = 0; i < targets.Count; i++)
                    {
                        var levelObject = RequireComObject(ComDispatch.Call(targets[i], "GetInterfaceByName", "ILevelObject"), "level object");
                        object? placement = null;
                        try
                        {
                            var old = before[i].Placement!;
                            placement = GeometryCom.CreatePlacement(math, old with { Origin = Translate(old.Origin, translationMm) });
                            ComDispatch.Call(levelObject, "SetPlacement", placement);
                            var actual = ReadGeometry(objects, targets[i], 2).Placement?.Origin;
                            var expected = Translate(old.Origin, translationMm);
                            if (actual is null || !SamePoint(actual, expected))
                                throw new NotSupportedException("Renga did not apply the requested placement; the entire move was rolled back. Use the native UI move command for this object type.");
                        }
                        finally { ComDispatch.Release(placement); ComDispatch.Release(levelObject); }
                    }
                }
                finally { ComDispatch.Release(math); }
                return (Before: before.ToArray(), After: targets.Select(obj => ReadGeometry(objects, obj, 16)).ToArray());
            }
            finally { foreach (var obj in targets) ComDispatch.Release(obj); }
        });
        var after = preview ? snapshots.After : ids.Select(id => WithModelObject(id.ToString("B"), (_, objects, obj) => ReadGeometry(objects, obj, 16))).ToArray();
        return new MoveObjectsResult(!preview, !preview && undo, translationMm, snapshots.Before, after);
    }, cancellationToken);

    internal static void ValidateTranslation(Point3DValue translation)
    {
        ArgumentNullException.ThrowIfNull(translation);
        if (!double.IsFinite(translation.X) || !double.IsFinite(translation.Y) || !double.IsFinite(translation.Z))
            throw new ArgumentException("Translation X/Y/Z must be finite millimetre values.");
    }
    private static Point3DValue Translate(Point3DValue point, Point3DValue translation)
    {
        var result = new Point3DValue(point.X + translation.X, point.Y + translation.Y, point.Z + translation.Z);
        ValidateTranslation(result); return result;
    }
    private static bool SamePoint(Point3DValue a, Point3DValue b) => Math.Abs(a.X-b.X) < 1e-6 && Math.Abs(a.Y-b.Y) < 1e-6 && Math.Abs(a.Z-b.Z) < 1e-6;

    public Task<WindowSnapshot> GetUiSnapshotAsync(CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            var (window, pid, context) = GetUiContext();
            return _windowUi.Capture(window, pid, context);
        }, cancellationToken);

    public Task<UiInputResult> SendUiInputAsync(UiInputRequest request, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            var (window, pid, context) = GetUiContext();
            var project = RequireComObject(ComDispatch.Get(RequireApplicationWithProject(), "Project"), "project");
            try
            {
                if (ComDispatch.Call<bool>(project, "HasActiveOperation"))
                    throw new InvalidOperationException("Finish the active API transaction before sending native UI input.");
            }
            finally { ComDispatch.Release(project); }
            return _windowUi.Input(window, pid, context, request);
        }, cancellationToken);

    private (IntPtr Window, int Pid, string Context) GetUiContext()
    {
        var app = RequireApplicationWithProject();
        var project = RequireComObject(ComDispatch.Get(app, "Project"), "project");
        object? model = null; object? view = null;
        try
        {
            model = RequireComObject(ComDispatch.Get(project, "Model"), "model");
            view = ComDispatch.Get(app, "ActiveView");
            var context = GeometryCom.GetModelId(model).ToString("B") + ":" + (view is null ? "none" : ComDispatch.Get<int>(view, "Id"));
            return (new IntPtr(ComDispatch.Call<int>(app, "GetMainWindowHandle")),
                _connectedInstance?.ProcessId ?? throw new InvalidOperationException("Renga process is unknown."), context);
        }
        finally { ComDispatch.Release(view); ComDispatch.Release(model); ComDispatch.Release(project); }
    }
}
