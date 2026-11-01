namespace Rigbay.PredatorSense.Wmi;

/// <summary>Why a WMI call did not give an answer.</summary>
public enum AcerWmiFailure
{
    /// <summary>The call did not return in time (or an earlier stalled call still holds the line). The state of the machine is unknown.</summary>
    Timeout,
    /// <summary>The module's own allow-list refused the call. Nothing was sent.</summary>
    Refused,
    /// <summary>WMI raised an error (access denied, class missing, bad type).</summary>
    Failed,
}

/// <summary>An expected failure of a WMI call. The logic above catches it and turns it into "not available".</summary>
public sealed class AcerWmiException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AcerWmiException(AcerWmiFailure failure, string message, Exception? inner = null) : base(message, inner) => Failure = failure;

    /// <summary>The kind of failure.</summary>
    public AcerWmiFailure Failure { get; }
}

/// <summary>
/// The two things this module ever says to the laptop's firmware, and nothing else: read one sensor, set the fan mode.
/// The module logic only uses this interface; <see cref="AcerWmi"/> is the real implementation over System.Management and
/// the tests use a fake. Both methods throw <see cref="AcerWmiException"/> for an expected failure.
/// </summary>
public interface IAcerWmi
{
    /// <summary>
    /// <c>AcerGamingFunction.GetGamingSysInfo</c> with input <c>1 | (index &lt;&lt; 8)</c>. Only the indexes in
    /// <see cref="WmiAllowList.ReadIndexes"/> are accepted. Returns the raw answer: status in bits 0 to 7, value in bits 8 to 23.
    /// </summary>
    /// <param name="index">The sensor index.</param>
    ulong ReadSysInfo(int index);

    /// <summary>
    /// <c>AcerGamingFunction.SetGamingFanBehavior</c> with exactly <paramref name="value"/>. Only the two values in
    /// <see cref="WmiAllowList"/> (Auto and Maximum) are accepted. Returns the raw answer; its low byte is the status (0 = accepted).
    /// This WRITES to the machine.
    /// </summary>
    /// <param name="value">The 64-bit input.</param>
    /// <param name="timeout">How long to wait for the answer; null = the implementation's default (15 s). Shutdown passes a short one.</param>
    uint SetFanBehavior(ulong value, TimeSpan? timeout = null);
}

/// <summary>What the probe asks about the machine (read-only, no WMI method is called).</summary>
public interface IMachineInfo
{
    /// <summary>True when the Windows service <c>PSSvc</c> is installed (it may be stopped).</summary>
    bool ServiceExists(string serviceName);

    /// <summary>True when the WMI class exists in the namespace (schema only, no instance, no call).</summary>
    bool WmiClassExists(string wmiNamespace, string className);

    /// <summary>Manufacturer and model as the firmware reports them, or null when unknown. Used only to show the model.</summary>
    string? ModelName();
}
