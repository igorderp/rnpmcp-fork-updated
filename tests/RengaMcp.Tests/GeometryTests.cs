using RengaMcp.Adapters;
using RengaMcp.Models;

namespace RengaMcp.Tests;

public sealed class GeometryTests
{
    private const string ObjectId = "{22222222-2222-2222-2222-222222222222}";
    private static Placement3DValue Placement(double x = 0) => new(new(x, 0, 0), new(1, 0, 0), new(0, 0, 1));

    [Fact]
    public void RejectsNonFinitePlacement()
    {
        Assert.Throws<ArgumentException>(() => GeometryValidation.Placement(Placement(double.NaN)));
        Assert.Throws<ArgumentException>(() => GeometryValidation.Placement(Placement(double.PositiveInfinity)));
    }

    [Fact]
    public void RejectsSkewedOrUnnormalizedAxes()
    {
        Assert.Throws<ArgumentException>(() => GeometryValidation.Placement(new(new(0,0,0), new(2,0,0), new(0,0,1))));
        Assert.Throws<ArgumentException>(() => GeometryValidation.Placement(new(new(0,0,0), new(1,0,0), new(1,0,0))));
    }

    [Fact]
    public void AcceptsRotatedOrthonormalPlacement() =>
        GeometryValidation.Placement(new(new(1200,600,760), new(0,1,0), new(0,0,1)));

    [Fact]
    public void RejectsBowTieContour() => Assert.Throws<ArgumentException>(() =>
        GeometryValidation.Baseline([new(0,0), new(10,10), new(0,10), new(10,0)], true));

    [Fact]
    public void RejectsNonAdjacentTouchAndOverlappingEdges()
    {
        Assert.Throws<ArgumentException>(() => GeometryValidation.Baseline([new(0,0),new(10,0),new(10,10),new(5,0),new(0,10)], true));
        Assert.Throws<ArgumentException>(() => GeometryValidation.Baseline([new(0,0),new(10,0),new(10,5),new(3,0),new(8,0)], false));
    }

    [Fact]
    public void RejectsZeroEdgesAndRepeatedClosingVertex()
    {
        Assert.Throws<ArgumentException>(() => GeometryValidation.Baseline([new(0,0),new(0,0)], false));
        Assert.Throws<ArgumentException>(() => GeometryValidation.Baseline([new(0,0),new(10,0),new(10,10),new(0,0)], true));
        Assert.Throws<ArgumentException>(() => GeometryValidation.Baseline([new(0,0),new(10,0),new(5,0)], false));
    }

    [Fact]
    public void RejectsDegenerateAndUnboundedContours()
    {
        Assert.Throws<ArgumentException>(() => GeometryValidation.Baseline([new(0,0),new(1,0),new(2,0)], true));
        Assert.Throws<ArgumentException>(() => GeometryValidation.Baseline([new(0,0),new(double.NaN,2)], false));
        Assert.Throws<ArgumentException>(() => GeometryValidation.Baseline(Enumerable.Repeat(new Point2DValue(0,0), 257).ToArray(), true));
    }

    [Fact]
    public void AcceptsConcaveLandingInBothDirections()
    {
        Point2DValue[] points = [new(0,0),new(4,0),new(4,2),new(2,2),new(2,4),new(0,4)];
        GeometryValidation.Baseline(points, true);
        GeometryValidation.Baseline(points.Reverse().ToArray(), true);
    }

    [Fact]
    public void DuplicateParameterIdsAreRejectedRegardlessOfFormatting() => Assert.Throws<ArgumentException>(() =>
        GeometryValidation.Assignments([new(ObjectId,"100"), new(Guid.Parse(ObjectId).ToString("D"),"200")]));

    [Fact]
    public async Task GeometryRequiresAConnection()
    {
        using var adapter = new MockRengaAdapter();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await adapter.GetObjectGeometryAsync(ObjectId));
    }

    [Fact]
    public async Task PlacementPreviewPreservesOriginalAndCommitPersists()
    {
        using var adapter = new MockRengaAdapter();
        await adapter.ConnectAsync(null);
        var original = await adapter.GetObjectGeometryAsync(ObjectId);
        var preview = await adapter.SetObjectPlacementAsync(ObjectId, Placement(1500));
        Assert.False(preview.Committed);
        Assert.Equal(1500, preview.After.Placement!.Origin.X);
        Assert.Equal(original, await adapter.GetObjectGeometryAsync(ObjectId));
        var commit = await adapter.SetObjectPlacementAsync(ObjectId, Placement(1500), false);
        Assert.True(commit.UndoRecorded);
        Assert.Equal(commit.After, await adapter.GetObjectGeometryAsync(ObjectId));
    }

    [Fact]
    public async Task CreationPreviewDoesNotChangeInventoryAndCommitDoes()
    {
        using var adapter = new MockRengaAdapter();
        await adapter.ConnectAsync(null);
        var before = await adapter.QueryObjectsAsync(new(null,null,0,500));
        var request = new CreateGeometryRequest(CreationCatalog.Types.First(t=>t.Name=="Stair").TypeId, 1, null, null,
            Placement(1000), [new(0,0),new(1500,0)], false, [], true);
        var preview = await adapter.CreateObjectAtAsync(request);
        Assert.False(preview.Committed);
        Assert.Equal(before.TotalMatched, (await adapter.QueryObjectsAsync(new(null,null,0,500))).TotalMatched);
        var result = await adapter.CreateObjectAtAsync(request with { Preview = false });
        Assert.True(result.Committed);
        Assert.Equal(before.TotalMatched + 1, (await adapter.QueryObjectsAsync(new(null,null,0,500))).TotalMatched);
        Assert.Equal(1500, (await adapter.GetObjectGeometryAsync(result.After.ModelObject.UniqueId)).Baseline!.Length);
    }

    [Fact]
    public async Task DeletionRequiresAnExactPreviewSetAndPreservesStateOnMismatch()
    {
        using var adapter = new MockRengaAdapter();
        await adapter.ConnectAsync(null);
        var original = await adapter.GetObjectGeometryAsync(ObjectId);
        var preview = await adapter.DeleteObjectsAsync([ObjectId], null);
        Assert.False(preview.Committed);
        Assert.Single(preview.DeletedObjects);
        Assert.Equal(original, await adapter.GetObjectGeometryAsync(ObjectId));
        await Assert.ThrowsAsync<ArgumentException>(async () => await adapter.DeleteObjectsAsync([ObjectId], null, false));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await adapter.DeleteObjectsAsync([ObjectId], ["{11111111-1111-1111-1111-111111111111}"], false));
        Assert.Equal(original, await adapter.GetObjectGeometryAsync(ObjectId));
        var deleted = await adapter.DeleteObjectsAsync([ObjectId], preview.DeletedObjects.Select(o=>o.UniqueId).ToArray(), false);
        Assert.True(deleted.Committed);
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await adapter.GetObjectGeometryAsync(ObjectId));
    }

    [Fact]
    public async Task UnknownSelectionDoesNotPartiallyReplaceExistingSelection()
    {
        using var adapter = new MockRengaAdapter();
        await adapter.ConnectAsync(null);
        var original = await adapter.SelectObjectsAsync([ObjectId]);
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await adapter.SelectObjectsAsync([ObjectId, "{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}"]));
        Assert.Equal(original, await adapter.GetSelectionAsync());
    }
}
