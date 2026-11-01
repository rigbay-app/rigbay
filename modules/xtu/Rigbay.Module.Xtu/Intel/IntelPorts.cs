using System.ServiceModel;
using System.ServiceModel.Description;
using System.Text;

namespace Rigbay.Module.Xtu.Intel;

// The ONLY code that touches the XTU service contracts (the module's own copies in Contracts.cs: namespaces IronCity.Core and
// IronCity.Common) and the WCF client. No Intel assembly is referenced or loaded. The calls are the ones the standalone app and
// the C1 spike proved; the service's interface is internal to Intel and may change with an XTU update (a change shows at run
// time, as a failed call, and not at build time).

internal static class Pipes
{
    public const string Tuning = "net.pipe://localhost/ExtremeTuningUtility/Tuning";
    public const string Monitoring = "net.pipe://localhost/ExtremeTuningUtility/Monitoring";

    /// <summary>The binding with the quotas XTU's replies need (the defaults are too small), as in the standalone app.</summary>
    public static NetNamedPipeBinding Binding(TimeSpan send, TimeSpan receive)
    {
        var b = new NetNamedPipeBinding
        {
            OpenTimeout = TimeSpan.FromSeconds(5),
            SendTimeout = send,
            ReceiveTimeout = receive,
            MaxReceivedMessageSize = 67108864,
            MaxBufferSize = 67108864,
        };
        b.ReaderQuotas.MaxArrayLength = 67108864;
        b.ReaderQuotas.MaxStringContentLength = 67108864;
        b.ReaderQuotas.MaxDepth = 64;
        return b;
    }

    public static void RaiseGraphQuota(ServiceEndpoint endpoint)
    {
        foreach (var op in endpoint.Contract.Operations)
        {
            var dc = op.Behaviors.Find<DataContractSerializerOperationBehavior>();
            if (dc != null) dc.MaxItemsInObjectGraph = int.MaxValue;
        }
    }
}

/// <summary>
/// Is an XTU pipe endpoint listening? Opens a channel and closes it at once; no operation is called, nothing is registered.
/// "Missing" only when the endpoint is not found for a reason other than access: from a process that is not elevated the
/// service refuses the pipe, and that says nothing about the machine, so it is "Unknown".
/// </summary>
internal static class PipeCheck
{
    public static PipeState State(string uri)
    {
        ChannelFactory<IronCity.Core.IXtuMonitoring>? factory = null;
        try
        {
            // Either contract will do: the address is checked when the connection opens, the action only per message.
            factory = new ChannelFactory<IronCity.Core.IXtuMonitoring>(
                Pipes.Binding(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)), new EndpointAddress(uri));
            factory.Endpoint.Binding.OpenTimeout = TimeSpan.FromSeconds(2);
            var channel = (ICommunicationObject)factory.CreateChannel();
            channel.Open();
            channel.Abort();
            return PipeState.Listening;
        }
        catch (EndpointNotFoundException ex) when (!DeniedAccess(ex)) { return PipeState.Missing; }
        catch (Exception) { return PipeState.Unknown; }
        finally { try { factory?.Abort(); } catch (Exception) { } }
    }

    private static bool DeniedAccess(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is UnauthorizedAccessException or AddressAccessDeniedException) return true;
        return false;
    }
}

/// <summary>The tuning pipe. Reads freely; the three write-path methods are guarded by <see cref="WriteLatch"/> and the id allow-list.</summary>
internal sealed class IntelTuningPort : IXtuTuningPort
{
    private ChannelFactory<IronCity.Core.IXtuTuning>? _factory;
    private IronCity.Core.IXtuTuning? _channel;
    private readonly WriteLatch _latch;

    public IntelTuningPort(WriteLatch latch) => _latch = latch;

    public bool IsOpen => _channel != null;

    public void Connect()
    {
        Close();
        var factory = new ChannelFactory<IronCity.Core.IXtuTuning>(
            Pipes.Binding(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15)), new EndpointAddress(Pipes.Tuning));
        Pipes.RaiseGraphQuota(factory.Endpoint);
        var channel = factory.CreateChannel();
        _factory = factory;
        _channel = channel;
        try
        {
            if (!channel.Connect()) throw new XtuException("err.refused");
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void Close()
    {
        try { if (_channel != null) ((ICommunicationObject)_channel).Abort(); } catch (Exception) { }
        try { _factory?.Abort(); } catch (Exception) { }
        _channel = null;
        _factory = null;
    }

    public void Dispose() => Close();

    private IronCity.Core.IXtuTuning Ch => _channel ?? throw new XtuException("err.notconnected");

    public void Refresh() => Ch.RefreshActiveValuesFromHardwareAccess();

    public ControlInfo GetControl(uint id)
    {
        if (!XtuIds.IsAllowed(id)) throw new XtuException("err.id-not-allowed", id);
        var c = Ch.GetControl(id);
        return new ControlInfo(id, c.ActiveValue, c.DefaultValue, c.ReadOnly, c.Enabled, c.SupportedValues?.ToList());
    }

    public bool GetRestoreOnBoot() => Ch.GetDoRestoreUserValuesOnBoot();

    public void Discard()
    {
        _latch.ThrowIfOn();
        Ch.DiscardChanges(IronCity.Common.SdkClientToken.Default, out _);
    }

    public PortResult Propose(IReadOnlyList<Proposal> proposals)
    {
        _latch.ThrowIfOn();
        foreach (var p in proposals)
            if (!XtuIds.IsAllowed(p.Id)) throw new XtuException("err.id-not-allowed", p.Id);

        var list = new List<IronCity.Core.XtuTuningProposal>(proposals.Count);
        foreach (var p in proposals)
            list.Add(new IronCity.Core.XtuTuningProposal { Id = p.Id, Value = p.Value });

        var result = Ch.ProposeChange(list, IronCity.Common.SdkClientToken.Default, out _, out var requiresReboot, false);
        return new PortResult(result.GeneralCode == IronCity.Common.TuningErrorCode.Success && !requiresReboot, Describe(result), requiresReboot);
    }

    public PortResult ApplyPending()
    {
        _latch.ThrowIfOn();
        var result = Ch.ApplyChanges(false, 1, IronCity.Common.SdkClientToken.Default);
        return new PortResult(result.GeneralCode == IronCity.Common.TuningErrorCode.Success, Describe(result));
    }

    private static string Describe(IronCity.Common.TuningResult r)
    {
        var sb = new StringBuilder(r.GeneralCode.ToString());
        if (r.AllCodes != null)
            foreach (var kv in r.AllCodes)
                sb.Append("; control ").Append(kv.Key).Append(" = ").Append(kv.Value);
        return sb.ToString();
    }
}

/// <summary>
/// The monitoring pipe. A client registers with its process id (Connect + Start) and unregisters on exit (Stop). These are
/// not tuning writes. The polling period is left alone because XTU's own window shares it.
/// </summary>
internal sealed class IntelMonitorPort : IXtuMonitorPort
{
    private ChannelFactory<IronCity.Core.IXtuMonitoring>? _factory;
    private IronCity.Core.IXtuMonitoring? _channel;
    private readonly int _pid = Environment.ProcessId;

    public IReadOnlyDictionary<uint, decimal> GetValues(out bool justOpened)
    {
        justOpened = false;
        if (_channel == null)
        {
            Open();
            justOpened = true;
        }
        return _channel!.GetValues();
    }

    private void Open()
    {
        var factory = new ChannelFactory<IronCity.Core.IXtuMonitoring>(
            Pipes.Binding(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), new EndpointAddress(Pipes.Monitoring));
        Pipes.RaiseGraphQuota(factory.Endpoint);
        var channel = factory.CreateChannel();
        _factory = factory;
        _channel = channel;
        try
        {
            if (!channel.Connect(_pid)) throw new XtuException("err.refused");
            channel.Start(_pid);
        }
        catch
        {
            Close(stop: false);
            throw;
        }
    }

    public void Reset() => Close(stop: false);

    private void Close(bool stop)
    {
        try { if (stop && _channel != null) _channel.Stop(_pid); } catch (Exception) { }
        try { if (_channel != null) ((ICommunicationObject)_channel).Abort(); } catch (Exception) { }
        try { _factory?.Abort(); } catch (Exception) { }
        _channel = null;
        _factory = null;
    }

    public void Dispose() => Close(stop: true);
}
