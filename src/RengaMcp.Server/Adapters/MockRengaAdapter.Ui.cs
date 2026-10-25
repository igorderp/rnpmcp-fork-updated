using RengaMcp.Models;

namespace RengaMcp.Adapters;

public sealed partial class MockRengaAdapter
{
    public Task<IReadOnlyList<LevelInfo>> GetLevelsAsync(CancellationToken cancellationToken = default)
    {
        RequireConnection();
        return Task.FromResult<IReadOnlyList<LevelInfo>>(_geometry.Values
            .Where(g => Guid.Parse(g.ModelObject.TypeId) == Guid.Parse(CreationCatalog.Types[0].TypeId))
            .Select(g => new LevelInfo(g.ModelObject, g.OwnLevel?.ElevationMm ?? g.Placement?.Origin.Z ?? 0, g.ModelObject.Name,
                g.OwnLevel?.Placement ?? g.Placement ?? new(new(0,0,0),new(1,0,0),new(0,0,1)), false)).OrderBy(g => g.ElevationMm).ToArray());
    }

    public Task<MoveObjectsResult> MoveObjectsAsync(IReadOnlyList<string> uniqueIds, Point3DValue translationMm,
        bool preview = true, CancellationToken cancellationToken = default)
    {
        RequireConnection();
        DynamicComRengaAdapter.ValidateTranslation(translationMm);
        if (uniqueIds.Count is < 1 or > 500) throw new ArgumentException("Provide 1..500 distinct IDs.");
        if (uniqueIds.Select(Guid.Parse).Distinct().Count() != uniqueIds.Count) throw new ArgumentException("IDs must be distinct.");
        var before = uniqueIds.Select(GetMockGeometry).ToArray();
        if (before.Any(g => g.ModelObject.Pinned || g.DependentObjectIds is { Count: > 0 }))
            throw new InvalidOperationException("Pinned objects or hosts with dependents cannot be moved through ILevelObject.");
        if (before.Any(g => g.Placement is null || Guid.Parse(g.ModelObject.TypeId) == Guid.Parse(CreationCatalog.Types[0].TypeId)))
            throw new NotSupportedException("Use native UI move for levels or unsupported objects.");
        var after = before.Select(g =>
        {
            var p = g.Placement!; var o = p.Origin;
            var target = new Point3DValue(o.X + translationMm.X, o.Y + translationMm.Y, o.Z + translationMm.Z);
            DynamicComRengaAdapter.ValidateTranslation(target);
            return g with { Placement = p with { Origin = target } };
        }).ToArray();
        if (!preview) foreach (var g in after) _geometry[Guid.Parse(g.ModelObject.UniqueId)] = g;
        return Task.FromResult(new MoveObjectsResult(!preview, !preview, translationMm, before, after));
    }

    // Mock must never pretend to have captured or driven the user's desktop.
    public Task<WindowSnapshot> GetUiSnapshotAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Native UI tools require a live Renga window. Mock has no desktop.");
    public Task<UiInputResult> SendUiInputAsync(UiInputRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Native UI tools require a live Renga window. Mock sends no input.");
}
