using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>
/// A value the host owns and a module only observes: the module can read it and subscribe to <see cref="Changed"/>, but a
/// set from the module is ignored. The host changes it with <see cref="Set"/>.
/// </summary>
internal sealed class HostOwnedValue<T> : IObservableValue<T>
{
    private readonly object _gate = new();
    private T _value;

    public HostOwnedValue(T initial) => _value = initial;

    public T Value
    {
        get { lock (_gate) return _value; }
        set { /* the module cannot set it */ }
    }

    public event Action? Changed;

    /// <summary>Stores the value and raises <see cref="Changed"/> (synchronously, on the caller's thread) when it differs.</summary>
    public void Set(T value)
    {
        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(_value, value)) return;
            _value = value;
        }
        Changed?.Invoke();
    }
}
