using Xunit;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Dispatcher;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using IronCity.Common;
using IronCity.Core;

namespace Rigbay.Module.Xtu.Tests;

/// <summary>
/// The module talks to the XTU service with its OWN copies of the contracts (Intel/Contracts.cs). No XTU is needed here: the tests
/// compare the copies with a recording made once from the real XTU 7.14.2.135, read-only, by tools/record-wire.ps1:
///  - wire/service-contract.txt: the WCF service description (actions, wrapper and part names and order) computed from Intel's interfaces;
///  - wire/type-samples.xml: the data contract serializer's output of Intel's own types for fixed values (write-path types included);
///  - wire/live-messages.xml: the real request and reply bodies the service exchanged for the read-only calls.
/// A mismatch would only show against the real service, never in the fake-based tests, which is why these exist.
/// </summary>
public class WireContractTests
{
    private static string Wire(string name) => Path.Combine(AppContext.BaseDirectory, "wire", name);

    private static readonly XNamespace Tempuri = "http://tempuri.org/";
    private static readonly MessageVersion Version = MessageVersion.Soap12WSAddressing10;

    // ---- canonical form: expanded names, attributes other than xmlns, trimmed text; prefixes and whitespace do not count ---------------
    private static string Canon(XElement e)
    {
        var sb = new StringBuilder();
        Write(e, sb);
        return sb.ToString();

        static void Write(XElement x, StringBuilder o)
        {
            o.Append('<').Append(x.Name.NamespaceName).Append('|').Append(x.Name.LocalName);
            foreach (var a in x.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
                o.Append(' ').Append(a.Name.ToString()).Append('=').Append(a.Value);
            o.Append('>');
            if (x.HasElements) foreach (var c in x.Elements()) Write(c, o);
            else o.Append(x.Value.Trim());
            o.Append("</>");
        }
    }

    private static XElement Serialise<T>(T value, string? root = null, string? ns = null)
    {
        var serializer = root is null ? new DataContractSerializer(typeof(T)) : new DataContractSerializer(typeof(T), root, ns!);
        using var ms = new MemoryStream();
        serializer.WriteObject(ms, value);
        ms.Position = 0;
        return XElement.Load(ms);
    }

    private static T Deserialise<T>(XElement x, string? root = null, string? ns = null)
    {
        var serializer = root is null ? new DataContractSerializer(typeof(T)) : new DataContractSerializer(typeof(T), root, ns!);
        using var reader = x.CreateReader();
        return (T)serializer.ReadObject(reader)!;
    }

    // ---- 1. the service description ------------------------------------------------------------------------------------------------

    private static string TypeName(Type t) => (t.IsByRef ? t.GetElementType()! : t).ToString();

    private static string Describe(Type iface, IEnumerable<string> ops)
    {
        var sb = new StringBuilder();
        var cd = ContractDescription.GetContract(iface);
        sb.AppendLine("contract " + cd.Name + " ns=" + cd.Namespace);
        foreach (var name in ops.OrderBy(x => x, StringComparer.Ordinal))
        {
            var op = cd.Operations.Find(name);
            sb.AppendLine("op " + op.Name + " oneway=" + op.IsOneWay + " faults=" + op.Faults.Count + " knowntypes=" + op.KnownTypes.Count);
            for (var i = 0; i < op.Messages.Count; i++)
            {
                var m = op.Messages[i];
                sb.AppendLine("  " + m.Direction + " action=" + m.Action + " wrapper=" + m.Body.WrapperName + " wrapperNs=" + m.Body.WrapperNamespace);
                if (m.Body.ReturnValue != null)
                    sb.AppendLine("    return " + m.Body.ReturnValue.Name + " ns=" + m.Body.ReturnValue.Namespace + " type=" + TypeName(m.Body.ReturnValue.Type));
                foreach (var p in m.Body.Parts)
                    sb.AppendLine("    part[" + p.Index + "] " + p.Name + " ns=" + p.Namespace + " type=" + TypeName(p.Type));
            }
        }
        return sb.ToString();
    }

    private static string[] Ops(Type iface) =>
        iface.GetMethods().Select(m => m.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void The_service_description_equals_the_one_recorded_from_Intels_interfaces()
    {
        var recorded = File.ReadAllText(Wire("service-contract.txt")).TrimStart('﻿').Replace("\r\n", "\n");
        // Intel's two contracts are session-allowed (the default) and have no callback contract; the client library has no property for
        // either, so the recorded header is checked against the attributes of the copies.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(recorded, @" session=Allowed callback=none\n").Count);
        recorded = System.Text.RegularExpressions.Regex.Replace(recorded, @" session=\w+ callback=\w+\n", "\n");
        foreach (var iface in new[] { typeof(IXtuTuning), typeof(IXtuMonitoring) })
        {
            var attr = (ServiceContractAttribute)Attribute.GetCustomAttribute(iface, typeof(ServiceContractAttribute))!;
            Assert.Equal(SessionMode.Allowed, attr.SessionMode);
            Assert.Null(attr.CallbackContract);
            Assert.Null(attr.Name);         // so the name is the interface name and the namespace is http://tempuri.org/
            Assert.Null(attr.Namespace);
        }
        // The recording lists the 7 + 4 mirrored operations; the copies declare exactly those.
        var mine = Describe(typeof(IXtuTuning), Ops(typeof(IXtuTuning))) + Describe(typeof(IXtuMonitoring), Ops(typeof(IXtuMonitoring)));
        Assert.Equal(recorded.TrimEnd(), mine.Replace("\r\n", "\n").TrimEnd());
    }

    // ---- 2. data contracts: Intel's output for fixed values, read and written by the copies ----------------------------------------------

    private static Dictionary<string, XElement> Samples()
    {
        var doc = XDocument.Load(Wire("type-samples.xml"));
        return doc.Root!.Elements("sample").ToDictionary(s => (string)s.Attribute("name")!, s => s.Elements().Single());
    }

    public static TheoryData<string> SampleNames() => new()
    {
        "XtuTuningControl", "XtuTuningControl (SupportedValues null)", "List<XtuTuningProposal>", "SdkClientToken", "TuningResult",
        "TuningResult (no codes)", "TuningResult (one control)", "List<XtuTuningProposalResult>", "TuningErrorCode", "Dictionary<uint,decimal>",
    };

    private static XElement RoundTrip(string name, XElement sample) => name switch
    {
        "XtuTuningControl" or "XtuTuningControl (SupportedValues null)" => Serialise(Deserialise<XtuTuningControl>(sample)),
        "List<XtuTuningProposal>" => Serialise(Deserialise<List<XtuTuningProposal>>(sample)),
        "SdkClientToken" => Serialise(Deserialise<SdkClientToken>(sample)),
        "TuningResult" or "TuningResult (no codes)" or "TuningResult (one control)" => Serialise(Deserialise<TuningResult>(sample)),
        "List<XtuTuningProposalResult>" => Serialise(Deserialise<List<XtuTuningProposalResult>>(sample)),
        "TuningErrorCode" => Serialise(Deserialise<TuningErrorCode>(sample)),
        "Dictionary<uint,decimal>" => Serialise(Deserialise<Dictionary<uint, decimal>>(sample)),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Intels_serialised_form_is_read_and_written_back_identically_by_the_copy(string name)
    {
        var sample = Samples()[name];
        Assert.Equal(Canon(sample), Canon(RoundTrip(name, sample)));
    }

    [Fact]
    public void Every_recorded_sample_is_covered()
    {
        Assert.Equal(Samples().Keys.OrderBy(x => x), SampleNames().Cast<object[]>().Select(r => (string)r[0]).OrderBy(x => x));
    }

    [Fact]
    public void The_copies_built_from_the_same_values_serialise_like_Intels_types()
    {
        var s = Samples();
        Assert.Equal(Canon(s["XtuTuningControl"]), Canon(Serialise(new XtuTuningControl
        {
            Id = 48, DefaultValue = 45.5m, ActiveValue = 55m, ProposedValue = 56.25m, BootValue = 44m, RequiresReboot = true,
            SupportedValues = [10m, 20.5m, 30m], ReadOnly = true, Enabled = false, RealtimeOnly = true, IsFavoredCore = true,
        })));
        Assert.Equal(Canon(s["XtuTuningControl (SupportedValues null)"]), Canon(Serialise(new XtuTuningControl { Id = 1, ActiveValue = 2m })));
        Assert.Equal(Canon(s["List<XtuTuningProposal>"]), Canon(Serialise(new List<XtuTuningProposal>
        {
            new() { Id = 49, Value = 1m }, new() { Id = 48, Value = 55.5m },
        })));
        Assert.Equal(Canon(s["SdkClientToken"]), Canon(Serialise(new SdkClientToken { ProcessId = 4242 })));
        Assert.Equal(Canon(s["TuningResult"]), Canon(Serialise(new TuningResult(TuningErrorCode.ReadOnlyControl,
            new Dictionary<uint, TuningErrorCode> { [47] = TuningErrorCode.Success, [48] = TuningErrorCode.ReadOnlyControl }))));
        Assert.Equal(Canon(s["TuningResult (no codes)"]), Canon(Serialise(new TuningResult(TuningErrorCode.Success))));
        Assert.Equal(Canon(s["TuningResult (one control)"]), Canon(Serialise(new TuningResult(TuningErrorCode.UndervoltProtection,
            new Dictionary<uint, TuningErrorCode> { [66] = TuningErrorCode.UndervoltProtection }))));
        Assert.Equal(Canon(s["List<XtuTuningProposalResult>"]), Canon(Serialise(new List<XtuTuningProposalResult>
        {
            new() { Id = 66, Value = 28m, Enabled = true, RebootRequired = false }, new() { Id = 47, Value = 60m, Enabled = false, RebootRequired = true },
        })));
        Assert.Equal(Canon(s["TuningErrorCode"]), Canon(Serialise(TuningErrorCode.IllegalVRAddress)));
        Assert.Equal(Canon(s["Dictionary<uint,decimal>"]), Canon(Serialise(new Dictionary<uint, decimal> { [7] = 1.25m, [3] = 40m })));
    }

    [Fact]
    public void The_error_code_enum_keeps_the_names_and_values_of_the_service()
    {
        var expected = new[]
        {
            "DidNotAttempt", "Success", "UnknownTuningFailure", "InvalidProposal", "ReadOnlyControl", "DisabledControl",
            "ControlDoesNotExist", "BlockedByISO", "UndervoltProtection", "UnsupportedValueRequested", "IllegalVRAddress", "TryAgain",
        };
        Assert.Equal(expected, Enum.GetNames<TuningErrorCode>());
        Assert.Equal(Enumerable.Range(0, 12), Enum.GetValues<TuningErrorCode>().Select(v => (int)v));
    }

    [Fact]
    public void The_client_token_carries_this_process_id()
    {
        Assert.Equal(Environment.ProcessId, SdkClientToken.Default.ProcessId);
    }

    // ---- 3. real messages of the read-only calls, through the copies' own WCF formatters ---------------------------------------------

    private sealed record Recorded(string Direction, string Action, XElement Body);

    private static List<Recorded> Live() =>
        XDocument.Load(Wire("live-messages.xml")).Root!.Elements("message")
            .Select(m => new Recorded((string)m.Attribute("direction")!, (string)m.Attribute("action")!, m.Elements().Single())).ToList();

    /// <summary>Builds the client runtime of a contract without connecting anywhere, to reach the operation formatters.</summary>
    private sealed class RuntimeCapture : IEndpointBehavior
    {
        public ClientRuntime? Runtime;
        public void AddBindingParameters(ServiceEndpoint endpoint, BindingParameterCollection bindingParameters) { }
        public void ApplyClientBehavior(ServiceEndpoint endpoint, ClientRuntime clientRuntime) => Runtime = clientRuntime;
        public void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher) { }
        public void Validate(ServiceEndpoint endpoint) { }
    }

    private static ClientRuntime RuntimeOf<T>(string pipe) where T : class
    {
        var capture = new RuntimeCapture();
        var factory = new ChannelFactory<T>(Intel.Pipes.Binding(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), new EndpointAddress(pipe));
        factory.Endpoint.EndpointBehaviors.Add(capture);
        factory.Open();                     // builds the runtime and the formatters; nothing connects before a channel is opened
        return capture.Runtime!;
    }

    private static ClientOperation Operation(ClientRuntime rt, string name) => rt.ClientOperations.Single(o => o.Name == name);

    private static XElement RequestBody(ClientRuntime rt, string op, params object?[] args)
    {
        using var message = Operation(rt, op).Formatter.SerializeRequest(Version, args!);
        using var reader = message.GetReaderAtBodyContents();
        return XElement.Load(reader.ReadSubtree());
    }

    private static object? Reply(ClientRuntime rt, string op, Recorded reply, object?[] outs)
    {
        using var message = Message.CreateMessage(Version, reply.Action, reply.Body.CreateReader());
        return Operation(rt, op).Formatter.DeserializeReply(message, outs!);
    }

    private const string TuningPipe = "net.pipe://localhost/ExtremeTuningUtility/Tuning";
    private const string MonitoringPipe = "net.pipe://localhost/ExtremeTuningUtility/Monitoring";

    [Fact]
    public void Requests_of_the_copies_equal_the_recorded_requests()
    {
        var live = Live().Where(m => m.Direction == "request").ToList();
        var tuning = RuntimeOf<IXtuTuning>(TuningPipe);
        var monitoring = RuntimeOf<IXtuMonitoring>(MonitoringPipe);

        var mine = new List<string>
        {
            Canon(RequestBody(tuning, "Connect")),
            Canon(RequestBody(tuning, "RefreshActiveValuesFromHardwareAccess")),
        };
        foreach (var id in new uint[] { 47, 48, 49, 66 }) mine.Add(Canon(RequestBody(tuning, "GetControl", id)));
        mine.Add(Canon(RequestBody(tuning, "GetDoRestoreUserValuesOnBoot")));
        mine.Add(Canon(RequestBody(monitoring, "Connect", 1234)));
        mine.Add(Canon(RequestBody(monitoring, "Start", 1234)));
        mine.Add(Canon(RequestBody(monitoring, "GetValues")));
        mine.Add(Canon(RequestBody(monitoring, "Stop", 1234)));

        Assert.Equal(live.Select(m => Canon(m.Body)), mine);
    }

    [Fact]
    public void Real_replies_are_understood_by_the_copies()
    {
        var live = Live().Where(m => m.Direction == "reply").ToList();
        var tuning = RuntimeOf<IXtuTuning>(TuningPipe);
        var monitoring = RuntimeOf<IXtuMonitoring>(MonitoringPipe);

        // Connect (tuning)
        Assert.Equal(true, Reply(tuning, "Connect", live[0], []));

        // GetControl 47, 48, 49, 66 (the four replies in the order of the recording)
        var controls = live.Where(m => m.Action.EndsWith("/GetControlResponse", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, controls.Count);
        var read = controls.Select(r => (XtuTuningControl)Reply(tuning, "GetControl", r, [])!).ToList();
        Assert.Equal(new uint[] { 47, 48, 49, 66 }, read.Select(c => c.Id));
        Assert.Equal(60m, read[0].ActiveValue);
        Assert.Equal(115m, read[0].DefaultValue);
        Assert.Equal(55m, read[1].ActiveValue);
        Assert.Equal(90m, read[1].DefaultValue);
        Assert.Equal(1m, read[2].ActiveValue);
        Assert.Equal(56m, read[3].ActiveValue);
        Assert.All(read, c => { Assert.True(c.Enabled); Assert.False(c.ReadOnly); Assert.NotNull(c.SupportedValues); Assert.NotEmpty(c.SupportedValues!); });
        Assert.Equal(new[] { 0m, 1m }, read[2].SupportedValues);

        Assert.Equal(true, Reply(tuning, "GetDoRestoreUserValuesOnBoot", live.Single(m => m.Action.EndsWith("GetDoRestoreUserValuesOnBootResponse", StringComparison.Ordinal)), []));

        // monitoring
        Assert.Equal(true, Reply(monitoring, "Connect", live.Single(m => m.Action.EndsWith("IXtuMonitoring/ConnectResponse", StringComparison.Ordinal)), []));
        var values = (Dictionary<uint, decimal>)Reply(monitoring, "GetValues", live.Single(m => m.Action.EndsWith("/GetValuesResponse", StringComparison.Ordinal)), [])!;
        Assert.Equal(51, values.Count);
        Assert.All(values.Values, v => Assert.True(v >= 0m || v < 0m));   // all parsed as decimals
    }

    [Fact]
    public void Real_control_replies_are_written_back_unchanged_by_the_copy()
    {
        foreach (var r in Live().Where(m => m.Direction == "reply" && m.Action.EndsWith("/GetControlResponse", StringComparison.Ordinal)))
        {
            var result = r.Body.Element(Tempuri + "GetControlResult")!;
            var control = Deserialise<XtuTuningControl>(result, "GetControlResult", Tempuri.NamespaceName);
            Assert.Equal(Canon(result), Canon(Serialise(control, "GetControlResult", Tempuri.NamespaceName)));
        }
    }

    // ---- 4. the write path has no real recording (that would need a write); it is composed from Intel's own serialised types ---------------

    [Fact]
    public void The_write_path_requests_are_composed_of_the_recorded_pieces_in_the_order_of_the_contract()
    {
        var s = Samples();
        var tuning = RuntimeOf<IXtuTuning>(TuningPipe);

        XElement Part(string name, XElement sample) => new(Tempuri + name, sample.Attributes().Where(a => !a.IsNamespaceDeclaration), sample.Elements(), sample.HasElements ? null : sample.Value);

        var proposals = new List<XtuTuningProposal> { new() { Id = 49, Value = 1m }, new() { Id = 48, Value = 55.5m } };
        var propose = RequestBody(tuning, "ProposeChange", proposals, new SdkClientToken { ProcessId = 4242 }, false);
        Assert.Equal(
            Canon(new XElement(Tempuri + "ProposeChange", Part("proposals", s["List<XtuTuningProposal>"]), Part("clientToken", s["SdkClientToken"]), new XElement(Tempuri + "isProfile", "false"))),
            Canon(propose));

        var apply = RequestBody(tuning, "ApplyChanges", false, 1, new SdkClientToken { ProcessId = 4242 });
        Assert.Equal(
            Canon(new XElement(Tempuri + "ApplyChanges", new XElement(Tempuri + "forceReboot", "false"), new XElement(Tempuri + "tuningDelay", "1"), Part("clientToken", s["SdkClientToken"]))),
            Canon(apply));

        var discard = RequestBody(tuning, "DiscardChanges", new SdkClientToken { ProcessId = 4242 });
        Assert.Equal(Canon(new XElement(Tempuri + "DiscardChanges", Part("clientToken", s["SdkClientToken"]))), Canon(discard));
    }

    [Fact]
    public void The_write_path_replies_are_read_from_the_recorded_pieces()
    {
        var s = Samples();
        var tuning = RuntimeOf<IXtuTuning>(TuningPipe);

        XElement Part(string name, XElement sample) => new(Tempuri + name, sample.Attributes().Where(a => !a.IsNamespaceDeclaration), sample.Elements(), sample.HasElements ? null : sample.Value);

        var response = new XElement(Tempuri + "ProposeChangeResponse",
            Part("ProposeChangeResult", s["TuningResult"]),
            Part("proposalDelta", s["List<XtuTuningProposalResult>"]),
            new XElement(Tempuri + "requiresReboot", "true"));
        var outs = new object?[2];       // the formatter takes the out parameters only: proposalDelta, requiresReboot
        var result = (TuningResult)Reply(tuning, "ProposeChange", new Recorded("reply", "http://tempuri.org/IXtuTuning/ProposeChangeResponse", response), outs)!;
        Assert.Equal(TuningErrorCode.ReadOnlyControl, result.GeneralCode);
        Assert.Equal(TuningErrorCode.Success, result.AllCodes![47]);
        Assert.Equal(TuningErrorCode.ReadOnlyControl, result.AllCodes[48]);
        Assert.Equal(2, ((List<XtuTuningProposalResult>)outs[0]!).Count);
        Assert.Equal(true, outs[1]);
    }
}
