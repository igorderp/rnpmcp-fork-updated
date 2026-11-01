using System.Reflection;
using System.Runtime.InteropServices;
using RengaMcp.Infrastructure;
using RengaMcp.Models;

namespace RengaMcp.Tests;

#pragma warning disable CA2201 // Deliberately simulate native COM HRESULTs in recovery tests.
public sealed class ConnectionRecoveryTests
{
    [Fact]
    public void FirstProjectReadConnectsAndRepeatedConnectReusesProxy()
    {
        var fixture = new Fixture();
        var app = fixture.Session.RequireProject();
        Assert.Same(fixture.App, app);
        Assert.Equal(10, fixture.Session.Connect(null).ProcessId);
        Assert.Equal(1, fixture.AttachCount);
        Assert.Empty(fixture.Released);
    }

    [Fact]
    public void StaleProxyReconnectsBeforeOperationAndInvalidatesUiSnapshot()
    {
        var fixture = new Fixture();
        var old = fixture.Session.RequireProject();
        fixture.Broken = old;
        fixture.App = new object();
        Assert.Same(fixture.App, fixture.Session.RequireProject());
        Assert.Equal(2, fixture.AttachCount);
        Assert.Equal(new[] { old }, fixture.Released);
        Assert.Equal(3, fixture.Invalidations); // first attachment, lost proxy, replacement
        // Some RCWs are already released, so releasing a stale wrapper can itself fail.
        fixture.Broken = fixture.App; fixture.App = new object(); fixture.ReleaseInvalid = true;
        Assert.Same(fixture.App, fixture.Session.RequireProject());
    }

    [Fact]
    public void AmbiguousInstancesAreNeverChosenAutomatically()
    {
        var fixture = new Fixture();
        fixture.Instances.Add(new(20, "2.49", "second"));
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Session.RequireProject());
        Assert.Contains("Multiple Renga", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.AttachCount);
        Assert.Equal(20, fixture.Session.Connect(20).ProcessId);
    }

    [Fact]
    public void ExplicitPidNeverSwitchesToDifferentRengaAfterRestart()
    {
        var fixture = new Fixture();
        fixture.Session.Connect(10);
        fixture.Broken = fixture.App;
        fixture.Instances.Clear(); fixture.Instances.Add(new(20, "2.49", "new"));
        fixture.Session.Refresh();
        Assert.Null(fixture.Session.Application);
        Assert.Contains("PID 10", fixture.Session.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.AttachCount);
        fixture.App = new object();
        Assert.Equal(20, fixture.Session.Connect(20).ProcessId);
    }

    [Fact]
    public void AutoSelectedInstanceCanRecoverNewSoleProcess()
    {
        var fixture = new Fixture(); fixture.Session.RequireProject();
        fixture.Broken = fixture.App; fixture.App = new object();
        fixture.Instances.Clear(); fixture.Instances.Add(new(20, "2.49", "new"));
        fixture.Session.Refresh();
        Assert.Equal(20, fixture.Session.Instance?.ProcessId);
        Assert.Equal(2, fixture.AttachCount);
    }

    [Fact]
    public void InvalidReplacementDoesNotDiscardHealthyConnection()
    {
        var fixture = new Fixture(); var original = fixture.Session.RequireProject();
        Assert.Throws<InvalidOperationException>(() => fixture.Session.Connect(999));
        Assert.Same(original, fixture.Session.Application);
        fixture.Instances.Add(new(20, "2.49", "second")); fixture.FailAttach = true;
        Assert.Throws<InvalidOperationException>(() => fixture.Session.Connect(20));
        Assert.Same(original, fixture.Session.Application);
        Assert.Empty(fixture.Released);
    }

    [Fact]
    public void BusyRengaIsNotDisconnectedOrReattached()
    {
        var fixture = new Fixture(); var original = fixture.Session.RequireProject();
        fixture.Busy = true; fixture.Session.Refresh();
        Assert.Same(original, fixture.Session.Application);
        Assert.Equal(1, fixture.AttachCount);
        Assert.Contains("busy", fixture.Session.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Released);
        fixture.Busy = false;
        Assert.Same(original, fixture.Session.RequireProject());
    }

    [Fact]
    public void NoOpenProjectDoesNotLoseApplicationAndExplicitDisconnectStaysOff()
    {
        var fixture = new Fixture { ProjectOpen = false };
        Assert.Throws<InvalidOperationException>(() => fixture.Session.RequireProject());
        Assert.Same(fixture.App, fixture.Session.Application);
        fixture.ProjectOpen = true; Assert.Same(fixture.App, fixture.Session.RequireProject());
        fixture.Session.Disconnect(); fixture.Session.Refresh();
        Assert.Null(fixture.Session.Application);
        Assert.False(fixture.Session.AutoConnectEnabled);
        Assert.Equal(1, fixture.AttachCount);
        Assert.Throws<InvalidOperationException>(() => fixture.Session.RequireProject());
        Assert.True(fixture.Session.Connect(null).Connected);
    }

    [Fact]
    public void ExpectedComFailuresAreUnwrappedWithoutTreatingGenericErrorsAsDisconnects()
    {
        var disconnected = new TargetInvocationException(new COMException("lost", unchecked((int)0x80010108)));
        Assert.True(ComFailures.IsDisconnected(disconnected));
        Assert.Contains("0x80010108", ComFailures.Describe(disconnected), StringComparison.Ordinal);
        var busy = new AggregateException(new COMException("busy", unchecked((int)0x8001010a)));
        Assert.True(ComFailures.IsBusy(busy));
        Assert.False(ComFailures.IsDisconnected(busy));
        Assert.False(ComFailures.IsDisconnected(new COMException("unexpected", unchecked((int)0x80004005))));
    }

    private sealed class Fixture
    {
        public List<RengaInstanceInfo> Instances { get; } = [new(10, "2.49", "first")];
        public object App { get; set; } = new();
        public object? Broken { get; set; }
        public bool ProjectOpen { get; set; } = true;
        public bool Busy { get; set; }
        public bool FailAttach { get; set; }
        public bool ReleaseInvalid { get; set; }
        public int AttachCount { get; private set; }
        public int Invalidations { get; private set; }
        public List<object> Released { get; } = [];
        public RengaConnectionSession Session { get; }

        public Fixture() => Session = new(() => Instances, _ =>
        {
            if (FailAttach) throw new InvalidOperationException("Attachment failed.");
            AttachCount++; return App;
        }, app =>
        {
            if (ReferenceEquals(app, Broken)) throw new TargetInvocationException(new COMException("lost", unchecked((int)0x80010108)));
            if (Busy) throw new COMException("busy", unchecked((int)0x8001010a));
            return ProjectOpen;
        }, app =>
        {
            Released.Add(app);
            if (ReleaseInvalid) throw new InvalidComObjectException("Already released.");
        }, () => Invalidations++);
    }
}
