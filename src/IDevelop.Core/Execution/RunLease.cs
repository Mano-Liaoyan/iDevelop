using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// Lock order is coordinator permit, task lease, repository mutation lock, then journal write lock. Never acquire an
/// earlier lock while holding a later one.
/// </summary>
internal sealed class RunLease : IDisposable
{
    private sealed class Ownership(HeldFile file)
    {
        public readonly object Gate = new();
        public readonly HeldFile File = file;
        public RunLease? Current;
    }

    private readonly Ownership _ownership;
    private int _uses;
    private bool _released;
    private RunLease? _successor;

    private RunLease(string project, TaskId task, CoordinatorPermit permit, Ownership ownership)
    {
        Project = project;
        Task = task;
        Permit = permit;
        _ownership = ownership;
    }

    public string Project { get; }
    public TaskId Task { get; }
    public CoordinatorPermit Permit { get; }
    public bool Held
    {
        get { lock (_ownership.Gate) return _ownership.Current == this && _ownership.File.Held && Permit.Held; }
    }

    internal static RunLease? TryTake(CoordinatorPermit permit, TaskId task)
    {
        var file = HeldFile.TryOpen(StandaloneLease.LockPath(permit.Project, task));
        if (file is null) return null;
        var ownership = new Ownership(file);
        var lease = new RunLease(permit.Project, task, permit, ownership);
        ownership.Current = lease;
        return lease;
    }

    public IDisposable? Use()
    {
        var permit = Permit.Use();
        if (permit is null) return null;
        lock (_ownership.Gate)
        {
            if (!_released && _successor is null && _ownership.Current == this && _ownership.File.Use() is { } file)
            {
                _uses++;
                return new Scope(this, permit, file);
            }
        }
        permit.Dispose();
        return null;
    }

    public RunLease Transfer()
    {
        lock (_ownership.Gate)
        {
            if (!Held || _released || _successor is not null) throw new InvalidOperationException("The task lease is not held.");
            _successor = new(Project, Task, Permit, _ownership);
            Finish();
            return _successor;
        }
    }

    public void Dispose()
    {
        lock (_ownership.Gate)
        {
            _released = true;
            Finish();
        }
    }

    private void Finish()
    {
        if (_uses != 0 || _ownership.Current != this) return;
        if (_successor is { } next)
        {
            _ownership.Current = next;
            next.Finish();
        }
        else if (_released) _ownership.File.Dispose();
    }

    private sealed class Scope(RunLease owner, IDisposable permit, IDisposable file) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (owner._ownership.Gate)
            {
                if (_disposed) return;
                _disposed = true;
                owner._uses--;
                file.Dispose();
                owner.Finish();
            }
            permit.Dispose();
        }
    }
}

internal abstract record LeaseTake
{
    private LeaseTake() { }
    internal sealed record Taken(RunLease Lease) : LeaseTake;
    internal sealed record Busy : LeaseTake;
}
