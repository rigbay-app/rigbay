using System.ServiceModel;
using System.ServiceModel.Description;
using System.Text;

namespace Rigbay.Module.Xtu.Intel;

// The ONLY code that touches Intel's contract assemblies (XtuInterface.dll, XtuCommon.dll: namespaces IronCity.Core and
// IronCity.Common) and the WCF client. They are referenced in place from the Intel folder and loaded at run time through
// the host's probePaths rule; nothing of Intel's is copied or committed. The calls are the ones the standalone app and the
// C1 spike proved; the interface is internal to Intel and may change with an XTU update.

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

/// <summary>The tuning pipe. Reads freely; the three write-path methods are guarded by <see cref="WriteLatch"/> and the id allow-list.</summary>
internal sealed class IntelTuningPort : IXtuTuningPort
{
    private ChannelFactory<IronCity.Core.IXtuTuning>? _factory;
    private IronCity.Core.IXtuTuning? _channel;

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
        WriteLatch.ThrowIfOn();
        Ch.DiscardChanges(IronCity.Common.SdkClientToken.Default, out _);
    }

    public PortResult Propose(IReadOnlyList<Proposal> proposals)
    {
        WriteLatch.ThrowIfOn();
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
        WriteLatch.ThrowIfOn();
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
