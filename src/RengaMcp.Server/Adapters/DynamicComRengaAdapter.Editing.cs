using RengaMcp.Infrastructure;
using RengaMcp.Models;

namespace RengaMcp.Adapters;

public sealed partial class DynamicComRengaAdapter
{
    public Task<GeometryWriteResult> CreateObjectAtAsync(CreateGeometryRequest request, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() => CreateObjectAtCore(request), cancellationToken);

    public Task<GeometryWriteResult> SetObjectPlacementAsync(string uniqueId, Placement3DValue placement, bool preview = true, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            GeometryValidation.Placement(placement);
            return EditGeometry(uniqueId, preview, modelObject =>
            {
                RequireNoDependents(modelObject);
                var levelObject = RequireComObject(ComDispatch.Call(modelObject, "GetInterfaceByName", "ILevelObject"), "ILevelObject");
                var math = RequireComObject(ComDispatch.Get(RequireApplicationWithProject(), "Math"), "math");
                object? p = null;
                try
                {
                    p = GeometryCom.CreatePlacement(math, placement);
                    ComDispatch.Call(levelObject, "SetPlacement", p);
                }
                finally { ComDispatch.Release(p); ComDispatch.Release(math); ComDispatch.Release(levelObject); }
            });
        }, cancellationToken);

    public Task<GeometryWriteResult> SetObjectBaselineAsync(string uniqueId, IReadOnlyList<Point2DValue> points, bool closed, bool preview = true, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            GeometryValidation.Baseline(points, closed);
            return EditGeometry(uniqueId, preview, modelObject =>
            {
                RequireNoDependents(modelObject);
                SetBaseline(modelObject, points, closed);
            });
        }, cancellationToken);

    public Task<GeometryWriteResult> SetParametersAsync(string uniqueId, IReadOnlyList<ParameterAssignment> parameters, bool preview = true, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            GeometryValidation.Assignments(parameters);
            return EditGeometry(uniqueId, preview, modelObject => WriteAssignments(modelObject, parameters)) with
            {
                RequestedParameters = parameters.ToArray(),
                ParameterReadbackRequired = parameters.Count > 0,
                Message = preview
                    ? "Setter/validation preview succeeded and was rolled back. Renga may defer parameter evaluation until Apply; this does not verify final parameter values."
                    : "Parameters applied in one undoable operation. Read renga_get_object to verify evaluated values."
            };
        }, cancellationToken);

    private GeometryWriteResult CreateObjectAtCore(CreateGeometryRequest request)
    {
        GeometryValidation.Placement(request.Placement);
        GeometryValidation.Assignments(request.Parameters);
        if (request.BaselinePoints is not null) GeometryValidation.Baseline(request.BaselinePoints, request.CloseBaseline);
        if (!Guid.TryParse(request.TypeId, out var typeId)) throw new ArgumentException("TypeId must be a GUID.", nameof(request));
        var type = CreationCatalog.Types.FirstOrDefault(t => Guid.Parse(t.TypeId) == typeId)
            ?? throw new ArgumentException("Use a type from renga_creation_types.", nameof(request));
        if (request.HostObjectId is < 1 || request.StyleId is < 0 || request.CategoryId is < 0)
            throw new ArgumentException("Host IDs must be positive; style/category IDs cannot be negative.", nameof(request));
        var (snapshot, undo) = InEdit(request.Preview, (project, model, objects) =>
        {
            ValidateCreationHost(objects, type, request.HostObjectId);
            if (request.StyleId is > 0)
            {
                var key = type.Name switch { "PlumbingFixture" => "plumbing_fixture", "LightingFixture" => "lighting_fixture", "WiringAccessory" => "wiring_accessory", _ => type.Name.ToLowerInvariant() };
                if (!CreationCatalog.StyleCollections.TryGetValue(key, out var collection))
                    throw new ArgumentException($"Type {type.Name} does not accept a style ID.", nameof(request));
                ValidateStyleIds(project, collection, [request.StyleId.Value]);
            }
            var arguments = RequireComObject(ComDispatch.Call(model, "CreateNewEntityArgs"), "new entity arguments");
            object? created = null;
            try
            {
                ComDispatch.Set(arguments, "TypeIdS", typeId.ToString("B"));
                if (request.HostObjectId is int host) ComDispatch.Set(arguments, "HostObjectId", host);
                if (request.StyleId is int style) ComDispatch.Set(arguments, "StyleId", style);
                if (request.CategoryId is int category) ComDispatch.Set(arguments, "CategoryId", category);
                GeometryCom.SetCreationPlacement(arguments, request.Placement);
                created = ComDispatch.Call(model, "CreateObject", arguments);
                if (created is null)
                    throw new InvalidOperationException($"Renga did not create the object: {ComDispatch.GetOrDefault(RequireApplicationWithProject(), "LastError", "Unknown creation error.")}");
                // Renga can anchor a new object's Z to the explicit host level,
                // even though INewEntityArgs describes Placement3D as global.
                // Preserve the requested global elevation through its Offset.
                if (request.BaselinePoints is not null) SetBaseline(created, request.BaselinePoints, request.CloseBaseline);
                var initial = ReadGeometry(objects, created, 2);
                if (initial.LevelElevation is double levelElevation && initial.ElevationAboveLevel is double elevation && initial.VerticalOffset is double offset)
                {
                    var correction = request.Placement.Origin.Z - levelElevation - elevation;
                    if (Math.Abs(correction) > 1e-7)
                        WriteAssignments(created, [new("{337FD89B-763E-46DC-B1A9-7AECB253ADBF}", (offset + correction).ToString("R", System.Globalization.CultureInfo.InvariantCulture))]);
                }
                WriteAssignments(created, request.Parameters);
                return ReadGeometry(objects, created, 16);
            }
            finally { ComDispatch.Release(created); ComDispatch.Release(arguments); }
        });
        var after = request.Preview ? snapshot : WithModelObject(snapshot.ModelObject.UniqueId, (_, objects, obj) => ReadGeometry(objects, obj, 16));
        return new GeometryWriteResult(!request.Preview, !request.Preview && undo,
            request.Preview ? "Preview succeeded; all changes were rolled back. Preview IDs are not durable; parameter evaluation may be deferred until Apply." : "Object created; geometry re-read after apply.",
            null, after, request.Parameters.ToArray(), request.Parameters.Count > 0);
    }

    private GeometryWriteResult EditGeometry(string uniqueId, bool preview, Action<object> action)
    {
        if (!Guid.TryParse(uniqueId, out var id)) throw new ArgumentException("uniqueId must be a GUID.", nameof(uniqueId));
        var (snapshots, undo) = InEdit(preview, (_, _, objects) =>
        {
            var modelObject = FindModelObject(objects, id);
            try
            {
                if (ComDispatch.Get<bool>(modelObject, "Pinned")) throw new InvalidOperationException("Object is pinned. Unpin it explicitly before editing geometry.");
                var before = ReadGeometry(objects, modelObject, 16);
                action(modelObject);
                return (Before: before, After: ReadGeometry(objects, modelObject, 16));
            }
            finally { ComDispatch.Release(modelObject); }
        });
        var after = preview ? snapshots.After : WithModelObject(uniqueId, (_, objects, obj) => ReadGeometry(objects, obj, 16));
        return new GeometryWriteResult(!preview, !preview && undo,
            preview ? "Preview succeeded; all changes were rolled back." : "Changes applied; geometry re-read after apply.", snapshots.Before, after);
    }

    // One coherent operation, with an unconditional rollback on validation or COM failure.
    private (T Result, bool UndoRecorded) InEdit<T>(bool preview, Func<object, object, object, T> action)
    {
        var project = RequireComObject(ComDispatch.Get(RequireApplicationWithProject(), "Project"), "project");
        object? model = null; object? objects = null; object? operation = null;
        var started = false; var finished = false;
        try
        {
            if (ComDispatch.Call<bool>(project, "HasActiveOperation"))
                throw new InvalidOperationException("Renga already has an active edit operation. Finish it before writing.");
            model = RequireComObject(ComDispatch.Get(project, "Model"), "model");
            objects = RequireComObject(ComDispatch.Call(model, "GetObjects"), "model objects");
            operation = GeometryCom.CreateUndoOperation(project, model);
            ComDispatch.Call(operation, "Start"); started = true;
            var result = action(project, model, objects);
            ComDispatch.Call(operation, preview ? "Rollback" : "Apply"); finished = true;
            return (result, true);
        }
        finally
        {
            if (started && !finished && operation is not null) TryRollback(operation);
            ComDispatch.Release(operation); ComDispatch.Release(objects); ComDispatch.Release(model); ComDispatch.Release(project);
        }
    }

    private void SetBaseline(object modelObject, IReadOnlyList<Point2DValue> points, bool closed)
    {
        var baselineObject = RequireComObject(ComDispatch.Call(modelObject, "GetInterfaceByName", "IBaseline2DObject"), "IBaseline2DObject");
        var math = RequireComObject(ComDispatch.Get(RequireApplicationWithProject(), "Math"), "math");
        var segments = new List<object>(); object? composite = null;
        try
        {
            var edges = closed ? points.Count : points.Count - 1;
            for (var i = 0; i < edges; i++) segments.Add(GeometryCom.CreateLine2(math, points[i], points[(i + 1) % points.Count]));
            composite = segments.Count == 1 ? null : GeometryCom.CreateComposite2(math, segments);
            ComDispatch.Call(baselineObject, "SetBaseline", composite ?? segments[0]);
        }
        finally
        {
            ComDispatch.Release(composite);
            foreach (var segment in segments) ComDispatch.Release(segment);
            ComDispatch.Release(math); ComDispatch.Release(baselineObject);
        }
    }

    private static void WriteAssignments(object modelObject, IReadOnlyList<ParameterAssignment> assignments)
    {
        if (assignments.Count == 0) return;
        var parameters = RequireComObject(ComDispatch.Call(modelObject, "GetParameters"), "parameters");
        try
        {
            foreach (var assignment in assignments)
            {
                var parameter = RequireComObject(ComDispatch.Call(parameters, "GetS", Guid.Parse(assignment.ParameterId).ToString("B")), "parameter");
                try
                {
                    if (ComDispatch.Get<bool>(parameter, "IsReadOnly")) throw new InvalidOperationException($"Parameter {assignment.ParameterId} is read-only.");
                    var valueType = ComDispatch.Get<int>(parameter, "ValueType");
                    var value = ParseParameterValue(assignment.Value, valueType);
                    if (value is double number && !double.IsFinite(number)) throw new ArgumentException("Parameter values must be finite.", nameof(assignments));
                    WriteParameterValue(parameter, valueType, value);
                    // Renga evaluates parameter changes on Apply. Reading a new
                    // object's pending parameter can return stale data or fail.
                }
                finally { ComDispatch.Release(parameter); }
            }
        }
        finally { ComDispatch.Release(parameters); }
    }

    private static void ValidateCreationHost(object objects, CreationType type, int? hostId)
    {
        if (hostId is null)
        {
            if (type.RequiresHost) throw new ArgumentException($"{type.Name} requires an explicit host ID.", nameof(hostId));
            return;
        }
        var host = RequireComObject(ComDispatch.Call(objects, "GetById", hostId.Value), "host object");
        try
        {
            var hostType = Guid.Parse(ComDispatch.Get<string>(host, "ObjectTypeS"));
            var allowedNames = type.Name switch
            {
                "Window" or "Door" => new[] { "Wall" },
                "Opening" => ["Floor", "Roof"],
                "LightingFixture" or "WiringAccessory" => ["Wall", "Column", "Beam", "Floor"],
                _ => ["Level"]
            };
            if (!allowedNames.Any(name => Guid.Parse(CreationCatalog.Types.First(t => t.Name == name).TypeId) == hostType))
                throw new ArgumentException($"Invalid host for {type.Name}; expected {string.Join(", ", allowedNames)}.", nameof(hostId));
        }
        finally { ComDispatch.Release(host); }
    }

    private static int[]? ReadDependentIds(object modelObject)
    {
        var host = OptionalInterface(modelObject, "IHostObject");
        if (host is null) return null;
        try
        {
            return (ComDispatch.Call(host, "GetDependentObjectIds") as Array)?.Cast<object>().Select(Convert.ToInt32).ToArray() ?? [];
        }
        finally { ComDispatch.Release(host); }
    }

    private static void RequireNoDependents(object modelObject)
    {
        if (ReadDependentIds(modelObject) is { Length: > 0 } ids)
            throw new InvalidOperationException($"Object has dependents ({string.Join(", ", ids)}); Renga does not support moving or changing its baseline in place.");
    }

    public Task<DeleteObjectsResult> DeleteObjectsAsync(IReadOnlyList<string> uniqueIds, IReadOnlyList<string>? expectedDeletedUniqueIds, bool preview = true, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            var ids = ValidateObjectIds(uniqueIds, false);
            var expected = expectedDeletedUniqueIds is null ? null : ValidateObjectIds(expectedDeletedUniqueIds, false).ToHashSet();
            if (!preview && expected is null) throw new ArgumentException("Commit requires the exact expectedDeletedUniqueIds set returned by preview.", nameof(expectedDeletedUniqueIds));
            var (deleted, undo) = InEdit(preview, (_, model, objects) =>
            {
                foreach (var id in ids)
                {
                    var obj = FindModelObject(objects, id);
                    try { if (ComDispatch.Get<bool>(obj, "Pinned")) throw new InvalidOperationException("Cannot delete a pinned object."); }
                    finally { ComDispatch.Release(obj); }
                }
                var before = SnapshotObjects(model);
                foreach (var id in ids) ComDispatch.Call(model, "DeleteObjectByUniqueIdS", id.ToString("B"));
                var after = SnapshotObjects(model);
                var removed = before.Where(pair => !after.ContainsKey(pair.Key)).Select(pair => pair.Value).ToArray();
                var actual = removed.Select(o => Guid.Parse(o.UniqueId)).ToHashSet();
                if (!ids.All(actual.Contains)) throw new InvalidOperationException("Renga did not delete every requested object.");
                if (expected is not null && !expected.SetEquals(actual))
                    throw new InvalidOperationException("Deletion cascade differs from expectedDeletedUniqueIds. All changes rolled back; preview again.");
                return removed;
            });
            return new DeleteObjectsResult(!preview, !preview && undo, deleted);
        }, cancellationToken);

    private static Guid[] ValidateObjectIds(IReadOnlyList<string> values, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 500 || (!allowEmpty && values.Count == 0)) throw new ArgumentException("Provide 1..500 object GUIDs.", nameof(values));
        var ids = values.Select(value => Guid.TryParse(value, out var id) ? id : throw new ArgumentException("Object IDs must be GUIDs.", nameof(values))).ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new ArgumentException("Object IDs must be distinct.", nameof(values));
        return ids;
    }
}
