using System.ServiceModel;

// The module's OWN copies of the few service and data contracts it speaks to the Intel XTU service over WCF named pipes.
// Only shapes and names are here, written from a list of member names, namespaces and attribute values read from the metadata of
// the installed XTU 7.14.2.135 (tools/dump-contracts.ps1 prints them). No Intel file is referenced, copied or shipped, and no code of
// Intel's is used: these are plain interfaces and structs.
//
// The wire format is decided by the CLR namespace and name of each type (the data contract namespace is derived from it), by the
// member names and their alphabetical order, and by the interface and method names (the SOAP actions). So the namespaces
// IronCity.Core and IronCity.Common, the type names, the member names and the parameter names below MUST NOT be "tidied".
// Rigbay.Module.Xtu.Tests/WireContractTests.cs compares them with a sample recorded once from the real service (read-only).
//
// Only the members the module calls are declared; the real interfaces have more (29 and 12 operations). A client contract with fewer
// operations talks to the same endpoint because each call is matched by its action.
// Nothing outside Intel/ may use these types or WCF. The data types are public only because the data contract serializer refuses
// a type that is not public (and has no [DataContract]); the two service interfaces stay internal.

namespace IronCity.Core
{
    /// <summary>The tuning pipe: 7 of the 29 operations.</summary>
    [ServiceContract]
    internal interface IXtuTuning
    {
        [OperationContract] bool Connect();
        [OperationContract] void RefreshActiveValuesFromHardwareAccess();
        [OperationContract] XtuTuningControl GetControl(uint id);
        [OperationContract] bool GetDoRestoreUserValuesOnBoot();

        // Write path (reached only through IntelTuningPort, behind the write latch and the four-id allow-list).
        [OperationContract] void DiscardChanges(IronCity.Common.SdkClientToken clientToken, out List<XtuTuningProposalResult> changeList);
        [OperationContract]
        IronCity.Common.TuningResult ProposeChange(List<XtuTuningProposal> proposals, IronCity.Common.SdkClientToken clientToken,
            out List<XtuTuningProposalResult> proposalDelta, out bool requiresReboot, bool isProfile);
        [OperationContract] IronCity.Common.TuningResult ApplyChanges(bool forceReboot, int tuningDelay, IronCity.Common.SdkClientToken clientToken);
    }

    /// <summary>The monitoring pipe: 4 of the 12 operations.</summary>
    [ServiceContract]
    internal interface IXtuMonitoring
    {
        [OperationContract] bool Connect(int processId);
        [OperationContract] void Start(int processId);
        [OperationContract] void Stop(int processId);
        [OperationContract] Dictionary<uint, decimal> GetValues();
    }

    // The three structs below carry no [DataContract]: the service serialises them by their public properties (the data contract
    // serializer sorts them by name), so every property the service sends is declared, even those the module never reads.

    public struct XtuTuningControl
    {
        public uint Id { get; set; }
        public decimal DefaultValue { get; set; }
        public decimal ActiveValue { get; set; }
        public decimal ProposedValue { get; set; }
        public decimal BootValue { get; set; }
        public bool RequiresReboot { get; set; }
        public List<decimal>? SupportedValues { get; set; }
        public bool ReadOnly { get; set; }
        public bool Enabled { get; set; }
        public bool RealtimeOnly { get; set; }
        public bool IsFavoredCore { get; set; }
    }

    public struct XtuTuningProposal
    {
        public uint Id { get; set; }
        public decimal Value { get; set; }
    }

    /// <summary>Element type of the two out lists of the write path; the module discards them but the reply must still deserialise.</summary>
    public struct XtuTuningProposalResult
    {
        public uint Id { get; set; }
        public decimal Value { get; set; }
        public bool Enabled { get; set; }
        public bool RebootRequired { get; set; }
    }
}

namespace IronCity.Common
{
    /// <summary>Sent with every write-path call. One public field; the service identifies the client by process id.</summary>
    public struct SdkClientToken
    {
        public int ProcessId;

        /// <summary>The token for this process (the process id of the caller).</summary>
        public static SdkClientToken Default => new SdkClientToken { ProcessId = Environment.ProcessId };
    }

    /// <summary>
    /// The result of a write-path call. [Serializable] with fields (not a data contract): the serializer writes the two private fields,
    /// the dictionary field <c>__codes</c> and the compiler's backing field of <see cref="GeneralCode"/>. Keep both names exactly.
    /// </summary>
    [Serializable]
    public struct TuningResult
    {
        private Dictionary<uint, TuningErrorCode>? __codes;

        public TuningResult(TuningErrorCode generalCode, Dictionary<uint, TuningErrorCode>? codes = null)
        {
            GeneralCode = generalCode;
            __codes = codes ?? new Dictionary<uint, TuningErrorCode>();
        }

        public TuningErrorCode GeneralCode { get; set; }

        public IReadOnlyDictionary<uint, TuningErrorCode>? AllCodes => __codes;
    }

    /// <summary>Travels by name (an enum without data-contract attributes). Keep the order and the values.</summary>
    public enum TuningErrorCode
    {
        DidNotAttempt = 0,
        Success = 1,
        UnknownTuningFailure = 2,
        InvalidProposal = 3,
        ReadOnlyControl = 4,
        DisabledControl = 5,
        ControlDoesNotExist = 6,
        BlockedByISO = 7,
        UndervoltProtection = 8,
        UnsupportedValueRequested = 9,
        IllegalVRAddress = 10,
        TryAgain = 11,
    }
}
