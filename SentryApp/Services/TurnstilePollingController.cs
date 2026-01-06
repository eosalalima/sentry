namespace SentryApp.Services;

public sealed class TurnstilePollingController
{
    private readonly object _sync = new();
    private bool _isActive;

    public event Action<bool>? StatusChanged;

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _isActive;
            }
        }
    }

    public bool TryStart()
    {
        bool changed;
        lock (_sync)
        {
            if (_isActive)
                return false;

            _isActive = true;
            changed = true;
        }

        if (changed)
            StatusChanged?.Invoke(true);

        return true;
    }

    public bool TryStop()
    {
        bool changed;
        lock (_sync)
        {
            if (!_isActive)
                return false;

            _isActive = false;
            changed = true;
        }

        if (changed)
            StatusChanged?.Invoke(false);

        return true;
    }
}
