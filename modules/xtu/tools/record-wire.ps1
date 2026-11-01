# Records the WIRE FORMAT of the Intel XTU service once, READ-ONLY, so the module's own contract copies can be checked against it
# without XTU (Rigbay.Module.Xtu.Tests/WireContractTests.cs). Needs XTU installed and an ELEVATED PowerShell 5.1 (the pipes are admin only).
#
# What it does (nothing is written to XTU: there is no ProposeChange, ApplyChanges or DiscardChanges call anywhere in this file):
#   1. live-messages.xml     real request and reply message bodies of the read-only calls the module makes: tuning Connect,
#                            RefreshActiveValuesFromHardwareAccess, GetControl (47, 48, 49, 66), GetDoRestoreUserValuesOnBoot;
#                            monitoring Connect, Start, GetValues, Stop (the same registration the module does with its process id).
#   2. service-contract.txt  the WCF service description (actions, wrapper and part names, order) of the operations the module mirrors,
#                            computed from Intel's interfaces, no service call.
#   3. type-samples.xml      the data contract serializer's output for fixed sample values of the types the module mirrors
#                            (including the write-path types, which cannot be recorded from a reply without a write), no service call.
# Intel's assemblies are used in place, only to compile this throw-away helper against and to produce the sample; none is copied.
# The output holds element names, namespaces and numbers; it is checked for machine identifiers before it is committed.
#
# Usage:  powershell -ExecutionPolicy Bypass -File record-wire.ps1 -OutDir <folder> -XtuDir <the Drivers folder of the XTU install>   (elevated)
# (XtuDir: the folder that holds XtuInterface.dll and XtuCommon.dll, under the Intel folder in Program Files; the module never needs it to build)
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$XtuDir
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

$src = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Dispatcher;
using System.Text;
using System.Xml;
using IronCity.Common;
using IronCity.Core;

public class WireRecorder : IClientMessageInspector, IEndpointBehavior
{
    public readonly StringBuilder Log = new StringBuilder();

    static string Body(Message m)
    {
        var sb = new StringBuilder();
        using (var xw = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
        using (var r = m.GetReaderAtBodyContents()) xw.WriteNode(r, true);
        return sb.ToString();
    }

    public object BeforeSendRequest(ref Message request, IClientChannel channel)
    {
        var buf = request.CreateBufferedCopy(int.MaxValue);
        var copy = buf.CreateMessage();
        Log.AppendLine("<message direction=\"request\" action=\"" + copy.Headers.Action + "\">");
        Log.AppendLine(Body(copy));
        Log.AppendLine("</message>");
        request = buf.CreateMessage();
        return null;
    }

    public void AfterReceiveReply(ref Message reply, object correlationState)
    {
        var buf = reply.CreateBufferedCopy(int.MaxValue);
        var copy = buf.CreateMessage();
        Log.AppendLine("<message direction=\"reply\" action=\"" + copy.Headers.Action + "\">");
        Log.AppendLine(Body(copy));
        Log.AppendLine("</message>");
        reply = buf.CreateMessage();
    }

    public void AddBindingParameters(ServiceEndpoint endpoint, BindingParameterCollection bindingParameters) { }
    public void ApplyClientBehavior(ServiceEndpoint endpoint, ClientRuntime clientRuntime) { clientRuntime.ClientMessageInspectors.Add(this); }
    public void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher) { }
    public void Validate(ServiceEndpoint endpoint) { }

    static NetNamedPipeBinding Binding()
    {
        var b = new NetNamedPipeBinding
        {
            OpenTimeout = TimeSpan.FromSeconds(5),
            SendTimeout = TimeSpan.FromSeconds(15),
            ReceiveTimeout = TimeSpan.FromSeconds(15),
            MaxReceivedMessageSize = 67108864,
            MaxBufferSize = 67108864,
        };
        b.ReaderQuotas.MaxArrayLength = 67108864;
        b.ReaderQuotas.MaxStringContentLength = 67108864;
        b.ReaderQuotas.MaxDepth = 64;
        return b;
    }

    static ChannelFactory<T> Factory<T>(string uri, WireRecorder rec)
    {
        var f = new ChannelFactory<T>(Binding(), new EndpointAddress(uri));
        foreach (var op in f.Endpoint.Contract.Operations)
        {
            var dc = op.Behaviors.Find<DataContractSerializerOperationBehavior>();
            if (dc != null) dc.MaxItemsInObjectGraph = int.MaxValue;
        }
        f.Endpoint.Behaviors.Add(rec);
        return f;
    }

    // ---- 1. the live read-only calls (the write-path operations of the interface are never called in this file) ------------------
    public static string RecordLive()
    {
        var rec = new WireRecorder();
        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        var tf = Factory<IXtuTuning>("net.pipe://localhost/ExtremeTuningUtility/Tuning", rec);
        var tuning = tf.CreateChannel();
        var mf = Factory<IXtuMonitoring>("net.pipe://localhost/ExtremeTuningUtility/Monitoring", rec);
        var mon = mf.CreateChannel();
        var summary = new StringBuilder();
        try
        {
            summary.AppendLine("tuning Connect = " + tuning.Connect());
            tuning.RefreshActiveValuesFromHardwareAccess();
            foreach (uint id in new uint[] { 47, 48, 49, 66 })
            {
                var c = tuning.GetControl(id);
                summary.AppendLine("GetControl " + id + ": active=" + c.ActiveValue + " default=" + c.DefaultValue + " readonly=" + c.ReadOnly + " enabled=" + c.Enabled);
            }
            summary.AppendLine("GetDoRestoreUserValuesOnBoot = " + tuning.GetDoRestoreUserValuesOnBoot());
            summary.AppendLine("monitoring Connect = " + mon.Connect(pid));
            mon.Start(pid);
            System.Threading.Thread.Sleep(1500);
            var v = mon.GetValues();
            summary.AppendLine("GetValues: " + v.Count + " values");
            mon.Stop(pid);
        }
        finally
        {
            try { ((ICommunicationObject)tuning).Abort(); } catch (Exception) { }
            try { ((ICommunicationObject)mon).Abort(); } catch (Exception) { }
            tf.Abort(); mf.Abort();
        }
        File.WriteAllText(OutDir + "\\live-summary.txt", summary.ToString());
        return "<messages>\r\n" + rec.Log + "</messages>\r\n";
    }

    public static string OutDir = ".";

    // ---- 2. the service description of the mirrored operations (computed from the interfaces, no call) ---------------------------
    static string T(Type t)
    {
        if (t.IsByRef) t = t.GetElementType();
        return t.ToString();
    }

    public static string DescribeContract(Type iface, string[] ops)
    {
        var sb = new StringBuilder();
        var cd = ContractDescription.GetContract(iface);
        sb.AppendLine("contract " + cd.Name + " ns=" + cd.Namespace + " session=" + cd.SessionMode + " callback=" + (cd.CallbackContractType == null ? "none" : cd.CallbackContractType.Name));
        foreach (var name in ops.OrderBy(x => x, StringComparer.Ordinal))
        {
            var op = cd.Operations.Find(name);
            sb.AppendLine("op " + op.Name + " oneway=" + op.IsOneWay + " faults=" + op.Faults.Count + " knowntypes=" + op.KnownTypes.Count);
            for (int i = 0; i < op.Messages.Count; i++)
            {
                var m = op.Messages[i];
                sb.AppendLine("  " + m.Direction + " action=" + m.Action + " wrapper=" + m.Body.WrapperName + " wrapperNs=" + m.Body.WrapperNamespace);
                if (m.Body.ReturnValue != null)
                    sb.AppendLine("    return " + m.Body.ReturnValue.Name + " ns=" + m.Body.ReturnValue.Namespace + " type=" + T(m.Body.ReturnValue.Type));
                foreach (var p in m.Body.Parts)
                    sb.AppendLine("    part[" + p.Index + "] " + p.Name + " ns=" + p.Namespace + " type=" + T(p.Type));
            }
        }
        return sb.ToString();
    }

    public static string Contracts()
    {
        return DescribeContract(typeof(IXtuTuning), new[] { "Connect", "RefreshActiveValuesFromHardwareAccess", "GetControl", "GetDoRestoreUserValuesOnBoot", "DiscardChanges", "ProposeChange", "ApplyChanges" })
             + DescribeContract(typeof(IXtuMonitoring), new[] { "Connect", "Start", "Stop", "GetValues" });
    }

    // ---- 3. data contract serializer output for fixed sample values ---------------------------------------------------------------
    static string Ser<TT>(string title, TT value)
    {
        var sb = new StringBuilder();
        using (var xw = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
            new DataContractSerializer(typeof(TT)).WriteObject(xw, value);
        return "<sample name=\"" + System.Security.SecurityElement.Escape(title) + "\">\r\n" + sb + "\r\n</sample>\r\n";
    }

    public static string Samples()
    {
        var sb = new StringBuilder("<samples>\r\n");
        var control = new XtuTuningControl { Id = 48, DefaultValue = 45.5m, ActiveValue = 55m, ProposedValue = 56.25m, BootValue = 44m, RequiresReboot = true,
            SupportedValues = new List<decimal> { 10m, 20.5m, 30m }, ReadOnly = true, Enabled = false, RealtimeOnly = true, IsFavoredCore = true };
        sb.Append(Ser("XtuTuningControl", control));
        sb.Append(Ser("XtuTuningControl (SupportedValues null)", new XtuTuningControl { Id = 1, ActiveValue = 2m }));
        sb.Append(Ser("List<XtuTuningProposal>", new List<XtuTuningProposal> { new XtuTuningProposal { Id = 49, Value = 1m }, new XtuTuningProposal { Id = 48, Value = 55.5m } }));
        sb.Append(Ser("SdkClientToken", new SdkClientToken { ProcessId = 4242 }));
        var codes = new Dictionary<uint, TuningErrorCode> { { 47, TuningErrorCode.Success }, { 48, TuningErrorCode.ReadOnlyControl } };
        var tr = new TuningResult(codes);
        sb.Append(Ser("TuningResult", tr));
        sb.Append(Ser("TuningResult (no codes)", new TuningResult(TuningErrorCode.Success)));
        sb.Append(Ser("TuningResult (one control)", new TuningResult(66u, TuningErrorCode.UndervoltProtection)));
        sb.Append(Ser("List<XtuTuningProposalResult>", new List<XtuTuningProposalResult> { new XtuTuningProposalResult { Id = 66, Value = 28m, Enabled = true, RebootRequired = false }, new XtuTuningProposalResult { Id = 47, Value = 60m, Enabled = false, RebootRequired = true } }));
        sb.Append(Ser("TuningErrorCode", TuningErrorCode.IllegalVRAddress));
        sb.Append(Ser("Dictionary<uint,decimal>", new Dictionary<uint, decimal> { { 7, 1.25m }, { 3, 40m } }));
        sb.Append("</samples>\r\n");
        return sb.ToString();
    }
}
'@

$refs = @((Join-Path $XtuDir 'XtuInterface.dll'), (Join-Path $XtuDir 'XtuCommon.dll'), 'System.ServiceModel', 'System.Runtime.Serialization', 'System.Xml', 'System.Core')
[void][System.Reflection.Assembly]::LoadFrom($refs[0]); [void][System.Reflection.Assembly]::LoadFrom($refs[1])
Add-Type -TypeDefinition $src -ReferencedAssemblies $refs
[WireRecorder]::OutDir = $OutDir

Set-Content -Path (Join-Path $OutDir 'service-contract.txt') -Value ([WireRecorder]::Contracts()) -Encoding UTF8
Set-Content -Path (Join-Path $OutDir 'type-samples.xml') -Value ([WireRecorder]::Samples()) -Encoding UTF8
$live = [WireRecorder]::RecordLive()
Set-Content -Path (Join-Path $OutDir 'live-messages.xml') -Value $live -Encoding UTF8
"recorded into $OutDir"
Get-ChildItem $OutDir | Select-Object Name, Length

# 4. Make the recording small and free of anything about this run: long SupportedValues lists keep their first 4 and last 2 entries (a
#    comment says how many were cut), and the process id the module sends in the monitoring calls becomes 1234. Nothing else is touched.
$file = Join-Path $OutDir 'live-messages.xml'
$doc = New-Object System.Xml.XmlDocument
$doc.Load($file)
foreach ($node in @($doc.SelectNodes("//*[local-name()='SupportedValues']"))) {
    $items = @($node.ChildNodes | Where-Object { $_.NodeType -eq 'Element' })
    if ($items.Count -le 6) { continue }
    for ($i = 4; $i -lt $items.Count - 2; $i++) { [void]$node.RemoveChild($items[$i]) }
    $message = $node.ParentNode.ParentNode.ParentNode   # message > Response > Result > SupportedValues
    [void]$message.InsertBefore($doc.CreateComment(" SupportedValues cut from $($items.Count) to 6 entries (first 4, last 2) "), $message.FirstChild)
}
foreach ($node in @($doc.SelectNodes("//*[local-name()='processId']"))) { $node.InnerText = '1234' }
$ws = New-Object System.Xml.XmlWriterSettings; $ws.Indent = $true; $ws.Encoding = New-Object System.Text.UTF8Encoding($false)
$w = [System.Xml.XmlWriter]::Create($file, $ws); $doc.Save($w); $w.Close()
"curated: $file"
