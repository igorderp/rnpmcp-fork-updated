using System.Buffers.Binary;
using System.IO.Compression;
using RengaMcp.Adapters;
using RengaMcp.Infrastructure;
using RengaMcp.Models;

namespace RengaMcp.Tests;

public sealed class UiAndMoveTests
{
    private const string Wall = "{22222222-2222-2222-2222-222222222222}";
    private const string Level = "{11111111-1111-1111-1111-111111111111}";
    private static readonly DateTimeOffset Now = new(2026,1,1,0,0,0,TimeSpan.Zero);
    private static WindowSnapshot Snapshot() => new("one-use", 123, 456, "Renga", 800, 600, "physical client pixels", false, Now, "project:view", []);

    [Theory]
    [InlineData("copy")][InlineData("paste")][InlineData("move")][InlineData("create_copy")][InlineData("enter")]
    public void NativeCommandsDefaultToPlanOnly(string key)
    {
        var request = new UiInputRequest("one-use", "key", Key:key);
        Assert.True(request.Preview);
        UiInputValidation.Validate(request, Snapshot(), "project:view", Now);
    }

    [Fact]
    public void RejectsStaleSnapshotProjectViewAndExpiredInputs()
    {
        var input = new UiInputRequest("one-use", "key", Key:"paste");
        Assert.Throws<InvalidOperationException>(() => UiInputValidation.Validate(input with { SnapshotId="old" }, Snapshot(), "project:view", Now));
        Assert.Throws<InvalidOperationException>(() => UiInputValidation.Validate(input, Snapshot(), "other-project:view", Now));
        Assert.Throws<InvalidOperationException>(() => UiInputValidation.Validate(input, Snapshot(), "project:view", Now.AddSeconds(61)));
        Assert.Throws<InvalidOperationException>(() => UiInputValidation.Validate(input, Snapshot(), "project:view", Now.AddSeconds(-1)));
    }

    [Theory]
    [InlineData(-1,0)][InlineData(800,0)][InlineData(0,-1)][InlineData(0,600)]
    public void RejectsClickOutsideTheObservedImage(int x,int y) =>
        Assert.Throws<ArgumentException>(() => UiInputValidation.Validate(new("one-use","click",X:x,Y:y), Snapshot(),"project:view",Now));

    [Fact]
    public void AcceptsControlClickForNativeLevelCopy()
    {
        UiInputValidation.Validate(new("one-use","click",X:20,Y:30,Modifier:"ctrl"),Snapshot(),"project:view",Now);
        Assert.Throws<ArgumentException>(() => UiInputValidation.Validate(new("one-use","key",Key:"copy",Modifier:"ctrl"),Snapshot(),"project:view",Now));
    }

    [Theory]
    [InlineData("Alt+F4")][InlineData("win+r")][InlineData("save")][InlineData("delete")]
    public void RejectsArbitraryHotkeys(string key) =>
        Assert.Throws<ArgumentException>(() => UiInputValidation.Validate(new("one-use","key",Key:key), Snapshot(),"project:view",Now));

    [Theory]
    [InlineData("42\n")][InlineData("42\r")][InlineData("\t")][InlineData("")]
    public void TextCannotInjectConfirmationOrNavigationKeys(string text) =>
        Assert.Throws<ArgumentException>(() => UiInputValidation.Validate(new("one-use","text",Text:text), Snapshot(),"project:view",Now));

    [Fact]
    public void AcceptsUnicodeAndLocaleSpecificHeightText() =>
        UiInputValidation.Validate(new("one-use","text",Text:"3,000"),Snapshot(),"project:view",Now);

    [Fact]
    public async Task BatchMovePreviewsRestoreAndCommitsPreserveAxes()
    {
        using var adapter = new MockRengaAdapter(); await adapter.ConnectAsync(null);
        var before = await adapter.GetObjectGeometryAsync(Wall);
        var preview = await adapter.MoveObjectsAsync([Wall],new(1200,-250,3000));
        Assert.False(preview.Committed);
        Assert.Equal(new Point3DValue(1200,-250,3000),preview.After[0].Placement!.Origin);
        Assert.Equal(before,await adapter.GetObjectGeometryAsync(Wall));
        var commit = await adapter.MoveObjectsAsync([Wall],new(1200,-250,3000),false);
        Assert.True(commit.UndoRecorded);
        Assert.Equal(before.Placement!.AxisX,commit.After[0].Placement!.AxisX);
        Assert.Equal(commit.After[0],await adapter.GetObjectGeometryAsync(Wall));
    }

    [Fact]
    public async Task InvalidMoveBatchLeavesEveryObjectUntouched()
    {
        using var adapter = new MockRengaAdapter(); await adapter.ConnectAsync(null);
        var before = await adapter.GetObjectGeometryAsync(Wall);
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.MoveObjectsAsync([Wall,Level],new(10,20,30),false));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => adapter.MoveObjectsAsync([Wall,Guid.NewGuid().ToString()],new(10,20,30),false));
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.MoveObjectsAsync([Wall,Wall],new(10,20,30),false));
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.MoveObjectsAsync([Wall],new(double.NaN,0,0),false));
        Assert.Equal(before,await adapter.GetObjectGeometryAsync(Wall));
    }

    [Fact]
    public async Task MockHasNoDesktopAndReportsReadOnlyLevelElevation()
    {
        using var adapter = new MockRengaAdapter(); await adapter.ConnectAsync(null);
        Assert.False(Assert.Single(await adapter.GetLevelsAsync()).ElevationWritableThroughApi);
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.GetUiSnapshotAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.SendUiInputAsync(new("any","key",Key:"paste",Preview:false)));
    }

    [Fact]
    public void PngHasCorrectDimensionsRgbPixelsAndCrcs()
    {
        // DIB pixels: red, green. DIB alpha is ignored, not encoded as transparency.
        var png = PngEncoder.EncodeBgra(2,1,[0,0,255,0,0,255,0,0]);
        Assert.Equal(new byte[]{137,80,78,71,13,10,26,10},png[..8]);
        using var payload = new MemoryStream();
        for (int pos=8; pos<png.Length;)
        {
            var length=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos,4));
            var type=System.Text.Encoding.ASCII.GetString(png,pos+4,4);
            uint crc=0xffffffff;
            foreach(var b in png.AsSpan(pos+4,length+4))
            { crc^=b; for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)==0?0:0xedb88320); }
            Assert.Equal(~crc,BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos+8+length,4)));
            if(type=="IHDR") {Assert.Equal(2,BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos+8,4)));Assert.Equal(1,BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos+12,4)));}
            if(type=="IDAT")payload.Write(png,pos+8,length);
            pos+=length+12;
        }
        payload.Position=0;
        using var zlib=new ZLibStream(payload,CompressionMode.Decompress);
        using var decoded=new MemoryStream(); zlib.CopyTo(decoded);
        Assert.Equal(new byte[]{0,255,0,0,0,255,0},decoded.ToArray());
    }
}
