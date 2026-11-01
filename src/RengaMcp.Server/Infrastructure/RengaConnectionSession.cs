using RengaMcp.Models;

namespace RengaMcp.Infrastructure;

// All methods run on the adapter's STA thread. Only the harmless HasProject probe
// can cause a reconnect. A failed model operation is never automatically replayed.
public sealed class RengaConnectionSession(
    Func<IReadOnlyList<RengaInstanceInfo>> listInstances,
    Func<int?, object> attach,
    Func<object, bool> hasProject,
    Action<object> release,
    Action? invalidated = null)
{
    public object? Application { get; private set; }
    public RengaInstanceInfo? Instance { get; private set; }
    public bool? HasProject { get; private set; }
    public bool AutoConnectEnabled { get; private set; } = true;
    public int? ExplicitProcessId { get; private set; }
    public string Message { get; private set; } = "Not yet attached. A project tool auto-connects when exactly one Renga is available.";

    public object EnsureConnected()
    {
        if (Application is not null)
        {
            try
            {
                HasProject = hasProject(Application);
                Message = HasProject == true ? "Connected to Renga; project is open." : "Connected to Renga; open the intended project.";
                return Application;
            }
            catch (Exception error) when (ComFailures.IsDisconnected(error)) { Drop(); }
        }
        if (!AutoConnectEnabled)
            throw new InvalidOperationException("Disconnected explicitly. Call renga_connect to enable connection again.");
        return AttachSelected(Select(listInstances(), ExplicitProcessId), ExplicitProcessId);
    }

    public object RequireProject()
    {
        var app = EnsureConnected();
        if (HasProject != true) throw new InvalidOperationException("Renga is connected but has no open project. Open the intended project in Renga; no application restart is required.");
        return app;
    }

    public ConnectionResult Connect(int? processId)
    {
        if (processId is < 1) throw new ArgumentOutOfRangeException(nameof(processId));
        if (Application is not null && (processId is null || processId == Instance?.ProcessId))
        {
            try
            {
                HasProject = hasProject(Application);
                ExplicitProcessId = processId; AutoConnectEnabled = true;
                Message = "Existing Renga connection is healthy.";
                return Result();
            }
            catch (Exception error) when (ComFailures.IsDisconnected(error)) { Drop(); }
        }
        // Validate a replacement before discarding an existing healthy connection.
        _ = AttachSelected(Select(listInstances(), processId), processId);
        return Result();
    }

    public void Refresh()
    {
        try { _ = EnsureConnected(); }
        catch (Exception error) when (error is InvalidOperationException || ComFailures.Unwrap(error) is System.Runtime.InteropServices.COMException)
        { Message = ComFailures.Describe(error); }
    }

    public void Disconnect()
    {
        Drop(); AutoConnectEnabled = false; ExplicitProcessId = null;
        Message = "Disconnected explicitly. Call renga_connect to reconnect.";
    }

    private object AttachSelected(RengaInstanceInfo selected, int? explicitPid)
    {
        var candidate = attach(selected.ProcessId);
        bool project;
        try { project = hasProject(candidate); }
        catch { release(candidate); throw; }
        Drop();
        Application = candidate; Instance = selected; HasProject = project;
        ExplicitProcessId = explicitPid; AutoConnectEnabled = true;
        Message = project ? "Connected to Renga; project is open." : "Connected to Renga; open the intended project.";
        return candidate;
    }

    private ConnectionResult Result() => new(true, Instance!.ProcessId, Instance.ApiVersion, HasProject == true, Message);

    private void Drop()
    {
        invalidated?.Invoke();
        var old = Application;
        Application = null; Instance = null; HasProject = null;
        if (old is not null) release(old);
    }

    private static RengaInstanceInfo Select(IReadOnlyList<RengaInstanceInfo> instances, int? processId)
    {
        if (processId is int pid)
            return instances.FirstOrDefault(instance => instance.ProcessId == pid)
                ?? throw new InvalidOperationException($"Selected Renga PID {pid} is no longer available. Call renga_list_instances and explicitly choose a process with renga_connect. No other process was selected automatically.");
        return instances.Count switch
        {
            0 => throw new InvalidOperationException("No Renga instance is registered. Start Renga and open a project. If Renga is already open, run Renga and the MCP client under the same Windows account and compatible elevation."),
            1 when instances[0].ProcessId is > 0 => instances[0],
            1 => throw new InvalidOperationException("Renga registration has no valid process ID; call renga_list_instances to inspect it."),
            _ => throw new InvalidOperationException("Multiple Renga instances are open. Call renga_connect with an explicit processId from renga_list_instances; no process was chosen arbitrarily.")
        };
    }
}
