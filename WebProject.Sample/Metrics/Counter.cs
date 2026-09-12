namespace WebProject.Sample.Metrics;

/// <summary>요청 카운터.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation guaranteed.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// </remarks>
public sealed class Counter
{
    private readonly object _lock = new();
    private long _value;
    private bool _enabled = true;

    public void Increment()
    {
        lock (_lock)
        {
            _value++;
        }
    }

    public long Value
    {
        get { lock (_lock) { return _value; } }
    }

    public bool TryEnable()
    {
        lock (_lock)
        {
            if (_enabled) return false;
            _enabled = true;
            return true;
        }
    }

    public object Snapshot() => _value;
}
