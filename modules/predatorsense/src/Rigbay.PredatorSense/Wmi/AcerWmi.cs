using System.Globalization;
using System.Management;

namespace Rigbay.PredatorSense.Wmi;

/// <summary>
/// The real <see cref="IAcerWmi"/> over System.Management. Needs an elevated process (instance access under root\WMI is
/// denied to a normal user). Rules, from FINDINGS 12.5 and 13: every call runs on its own task with a timeout (a WMI-ACPI
/// call can stall); only one call is ever in flight, and the line stays taken until a stalled call really returns, so calls
/// never pile up behind it (a later call fails after a short wait instead); reads send a UInt32, the write sends a UInt64
/// after the schema type was checked; the allow-list is checked here again. Nothing connects until the first call.
/// </summary>
public sealed class AcerWmi : IAcerWmi, IDisposable
{
    private readonly SemaphoreSlim _line = new(1, 1);
    private readonly object _instanceLock = new();
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _writeTimeout;
    private ManagementObject? _instance;
    private bool _writeSchemaChecked;

    /// <summary>Creates the object; no connection is made.</summary>
    public AcerWmi(TimeSpan? readTimeout = null, TimeSpan? writeTimeout = null)
    {
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(3);
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <inheritdoc />
    public ulong ReadSysInfo(int index)
    {
        WmiAllowList.RequireRead(index);
        uint input = WmiAllowList.ReadInput(index);
        return Run("read " + index, _readTimeout, TimeSpan.FromMilliseconds(250), () =>
        {
            var inst = Instance();
            using var p = inst.GetMethodParameters(WmiAllowList.ReadMethod);
            p["gmInput"] = input;
            using var o = inst.InvokeMethod(WmiAllowList.ReadMethod, p, null);
            return Convert.ToUInt64(o["gmOutput"], CultureInfo.InvariantCulture);
        });
    }

    /// <inheritdoc />
    public uint SetFanBehavior(ulong value, TimeSpan? timeout = null)
    {
        WmiAllowList.RequireFanWrite(value);
        var limit = timeout ?? _writeTimeout;
        ulong raw = Run("fan write", limit, TimeSpan.FromSeconds(Math.Min(2, limit.TotalSeconds)), () =>
        {
            var inst = Instance();
            using var p = inst.GetMethodParameters(WmiAllowList.FanMethod);
            if (!_writeSchemaChecked)
            {
                // FINDINGS 13.2: the input is UInt64 in the schema. If it is not, send nothing.
                if (p.Properties["gmInput"].Type != CimType.UInt64)
                    throw new AcerWmiException(AcerWmiFailure.Refused, "gmInput of SetGamingFanBehavior is not UInt64 on this machine.");
                _writeSchemaChecked = true;
            }
            p["gmInput"] = value;
            using var o = inst.InvokeMethod(WmiAllowList.FanMethod, p, null);
            return Convert.ToUInt64(o["gmOutput"], CultureInfo.InvariantCulture);
        });
        return unchecked((uint)raw);
    }

    /// <summary>Releases the cached WMI object.</summary>
    public void Dispose()
    {
        lock (_instanceLock)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }

    // Only called from inside Run, so never concurrently.
    private ManagementObject Instance()
    {
        lock (_instanceLock)
        {
            if (_instance is not null) return _instance;
            using var cls = new ManagementClass(WmiAllowList.Namespace, WmiAllowList.GamingClass, null);
            foreach (ManagementObject o in cls.GetInstances())
            {
                _instance = o;
                return o;
            }
        }
        throw new AcerWmiException(AcerWmiFailure.Failed, "No instance of " + WmiAllowList.GamingClass + ".");
    }

    private T Run<T>(string what, TimeSpan timeout, TimeSpan waitForLine, Func<T> work)
    {
        if (!_line.Wait(waitForLine))
            throw new AcerWmiException(AcerWmiFailure.Timeout, $"An earlier WMI call has not returned yet ({what} was not sent).");

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _ = Task.Run(() =>
            {
                try { done.SetResult(work()); }
                catch (Exception ex)
                {
                    Dispose();   // the next call looks the instance up again
                    done.SetException(ex);
                }
                finally { _line.Release(); }   // only now is the line free again, even after a timeout
            });
        }
        catch
        {
            _line.Release();
            throw;
        }

        try
        {
            if (!done.Task.Wait(timeout))
                throw new AcerWmiException(AcerWmiFailure.Timeout, $"WMI call ({what}) did not return within {timeout.TotalSeconds:0.#} s.");
            return done.Task.Result;
        }
        catch (AggregateException ex) when (ex.InnerException is AcerWmiException inner) { throw inner; }
        catch (AggregateException ex) { throw new AcerWmiException(AcerWmiFailure.Failed, $"WMI call ({what}) failed: {ex.InnerException?.Message}", ex.InnerException); }
    }
}
