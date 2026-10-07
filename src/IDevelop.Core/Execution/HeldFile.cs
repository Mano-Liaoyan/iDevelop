namespace IDevelop.Execution;

internal sealed class HeldFile : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _handle;
    private int _uses;
    private bool _released;

    private HeldFile(FileStream handle) { _handle = handle; }

    public static HeldFile? TryOpen(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException) { return null; }
    }

    public bool Held
    {
        get { lock (_gate) return _handle is not null; }
    }

    public IDisposable? Use()
    {
        lock (_gate)
        {
            if (_released || _handle is null) return null;
            _uses++;
            return new Scope(this);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _released = true;
            Finish();
        }
    }

    private void Finish()
    {
        if (!_released || _uses != 0) return;
        _handle?.Dispose();
        _handle = null;
    }

    private sealed class Scope(HeldFile owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                owner._uses--;
                owner.Finish();
            }
        }
    }
}
