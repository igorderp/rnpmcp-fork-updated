using RengaMcp.Infrastructure;
using RengaMcp.Models;

namespace RengaMcp.Adapters;

public sealed partial class DynamicComRengaAdapter
{
    public Task<ViewState> GetViewStateAsync(CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(ReadViewState, cancellationToken);

    private ViewState ReadViewState()
    {
        var application = RequireApplicationWithProject();
        var view = ComDispatch.Get(application, "ActiveView");
        try
        {
            return new ViewState(ComDispatch.Call<int>(application, "GetMainWindowHandle"),
                view is null ? null : ComDispatch.Get<int>(view, "Id"),
                view is null ? null : ComDispatch.Get<int>(view, "Type"), null);
        }
        finally { ComDispatch.Release(view); }
    }

    public Task<ViewState> OpenViewAsync(int? levelId = null, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            _windowUi.Invalidate();
            var application = RequireApplicationWithProject();
            var project = RequireComObject(ComDispatch.Get(application, "Project"), "project");
            object? entities = null; object? entity = null; object? view = null;
            try
            {
                int id;
                if (levelId is int level)
                {
                    if (level < 1) throw new ArgumentOutOfRangeException(nameof(levelId));
                    var model = RequireComObject(ComDispatch.Get(project, "Model"), "model");
                    try { entities = RequireComObject(ComDispatch.Call(model, "GetObjects"), "objects"); }
                    finally { ComDispatch.Release(model); }
                    entity = RequireComObject(ComDispatch.Call(entities, "GetById", level), "level");
                    if (Guid.Parse(ComDispatch.Get<string>(entity, "ObjectTypeS")) != Guid.Parse(CreationCatalog.Types[0].TypeId))
                        throw new ArgumentException("levelId must identify a Level object.", nameof(levelId));
                    id = level;
                }
                else
                {
                    // Building entity local ID opens the project's 3D view.
                    entity = RequireComObject(ComDispatch.Get(project, "BuildingInfo"), "building info");
                    id = ComDispatch.Get<int>(entity, "Id");
                }
                view = RequireComObject(ComDispatch.Call(application, "OpenViewByEntity", id), "view");
                return ReadViewState();
            }
            finally { ComDispatch.Release(view); ComDispatch.Release(entity); ComDispatch.Release(entities); ComDispatch.Release(project); }
        }, cancellationToken);

    public Task<SelectionResult> GetSelectionAsync(CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(ReadSelection, cancellationToken);

    private SelectionResult ReadSelection()
    {
        var application = RequireApplicationWithProject();
        var selection = RequireComObject(ComDispatch.Get(application, "Selection"), "selection");
        var project = RequireComObject(ComDispatch.Get(application, "Project"), "project");
        var model = RequireComObject(ComDispatch.Get(project, "Model"), "model");
        var objects = RequireComObject(ComDispatch.Call(model, "GetObjects"), "objects");
        try
        {
            var ids = (ComDispatch.Call(selection, "GetSelectedObjects") as Array)?.Cast<object>().Select(Convert.ToInt32) ?? [];
            var result = new List<RengaObjectSummary>();
            foreach (var id in ids)
            {
                var obj = ComDispatch.Call(objects, "GetById", id);
                try { if (obj is not null) result.Add(ReadObjectSummary(obj)); }
                finally { ComDispatch.Release(obj); }
            }
            return new SelectionResult(result);
        }
        finally { ComDispatch.Release(objects); ComDispatch.Release(model); ComDispatch.Release(project); ComDispatch.Release(selection); }
    }

    public Task<SelectionResult> SelectObjectsAsync(IReadOnlyList<string> uniqueIds, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            _windowUi.Invalidate();
            var ids = ValidateObjectIds(uniqueIds, true);
            var application = RequireApplicationWithProject();
            var project = RequireComObject(ComDispatch.Get(application, "Project"), "project");
            var model = RequireComObject(ComDispatch.Get(project, "Model"), "model");
            var objects = RequireComObject(ComDispatch.Call(model, "GetObjects"), "objects");
            var selection = RequireComObject(ComDispatch.Get(application, "Selection"), "selection");
            try
            {
                var localIds = ids.Select(id =>
                {
                    var obj = FindModelObject(objects, id);
                    try { return ComDispatch.Get<int>(obj, "Id"); }
                    finally { ComDispatch.Release(obj); }
                }).ToArray();
                ComDispatch.Call(selection, "SetSelectedObjects", (object)localIds);
            }
            finally { ComDispatch.Release(selection); ComDispatch.Release(objects); ComDispatch.Release(model); ComDispatch.Release(project); }
            return ReadSelection();
        }, cancellationToken);

    public Task<byte[]> GetScreenshotAsync(int width = 1600, int height = 1000, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            if (width is < 64 or > 4096 || height is < 64 or > 4096 || (long)width * height > 8_000_000)
                throw new ArgumentOutOfRangeException(nameof(width), "Use dimensions 64..4096, with at most 8 million pixels.");
            var view = RequireComObject(ComDispatch.Get(RequireApplicationWithProject(), "ActiveView"), "active view");
            object? settings = null; object? image = null;
            try
            {
                settings = GeometryCom.CreateScreenshotSettings(view);
                ComDispatch.Set(settings, "Width", width); ComDispatch.Set(settings, "Height", height);
                image = GeometryCom.MakeScreenshot(view, settings);
                var data = ComDispatch.Call(image, "SaveToData", 2) as byte[]
                    ?? throw new InvalidOperationException("Renga did not return PNG bytes.");
                if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(new byte[] {137, 80, 78, 71, 13, 10, 26, 10}))
                    throw new InvalidOperationException("Renga returned an invalid PNG image.");
                if (data.Length > 10 * 1024 * 1024) throw new InvalidOperationException("Screenshot exceeds the 10 MiB response limit; reduce dimensions.");
                return data;
            }
            finally { ComDispatch.Release(image); ComDispatch.Release(settings); ComDispatch.Release(view); }
        }, cancellationToken);
}
