// Offline check of the WRITE-path types, with no call to the XTU service (so nothing is written, nothing is even read).
// The same source is compiled twice: as a .NET Framework 4.8 exe (csc, what Intel's own client uses) and into the
// .NET 10 module. Each side serialises the types that ProposeChange / ApplyChanges / DiscardChanges send and receive with
// DataContractSerializer; the XML must be identical, and each side must be able to read the other side's XML.
// C# 5 syntax on purpose, so it also builds with the OS csc.exe.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;

namespace XtuModule
{
    public static class WireProbe
    {
        private static string Ser(object o, Type t)
        {
            StringBuilder sb = new StringBuilder();
            using (XmlWriter w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true }))
            {
                new DataContractSerializer(t).WriteObject(w, o);
            }
            return sb.ToString();
        }

        private static object De(string xml, Type t)
        {
            using (XmlReader r = XmlReader.Create(new StringReader(xml)))
            {
                return new DataContractSerializer(t).ReadObject(r);
            }
        }

        // One line per sample: name TAB xml
        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            IronCity.Common.SdkClientToken tok = IronCity.Common.SdkClientToken.Default;
            sb.AppendLine("token-default\t" + Ser(tok, typeof(IronCity.Common.SdkClientToken)));
            IronCity.Common.SdkClientToken tok2 = new IronCity.Common.SdkClientToken();
            tok2.ProcessId = 4242;
            sb.AppendLine("token-4242\t" + Ser(tok2, typeof(IronCity.Common.SdkClientToken)));

            List<IronCity.Core.XtuTuningProposal> props = new List<IronCity.Core.XtuTuningProposal>();
            IronCity.Core.XtuTuningProposal a = new IronCity.Core.XtuTuningProposal(); a.Id = 47; a.Value = 60m;
            IronCity.Core.XtuTuningProposal b = new IronCity.Core.XtuTuningProposal(); b.Id = 48; b.Value = 55.5m;
            props.Add(a); props.Add(b);
            sb.AppendLine("proposals\t" + Ser(props, typeof(List<IronCity.Core.XtuTuningProposal>)));

            IronCity.Common.TuningResult ok = new IronCity.Common.TuningResult(IronCity.Common.TuningErrorCode.Success);
            sb.AppendLine("result-success\t" + Ser(ok, typeof(IronCity.Common.TuningResult)));
            IronCity.Common.TuningResult one = new IronCity.Common.TuningResult(48u, IronCity.Common.TuningErrorCode.ReadOnlyControl);
            sb.AppendLine("result-one-code\t" + Ser(one, typeof(IronCity.Common.TuningResult)));
            Dictionary<uint, IronCity.Common.TuningErrorCode> map = new Dictionary<uint, IronCity.Common.TuningErrorCode>();
            map[47] = IronCity.Common.TuningErrorCode.Success;
            map[48] = IronCity.Common.TuningErrorCode.UnsupportedValueRequested;
            IronCity.Common.TuningResult many = new IronCity.Common.TuningResult(map);
            sb.AppendLine("result-two-codes\t" + Ser(many, typeof(IronCity.Common.TuningResult)));

            List<IronCity.Core.XtuTuningProposalResult> delta = new List<IronCity.Core.XtuTuningProposalResult>();
            IronCity.Core.XtuTuningProposalResult d = new IronCity.Core.XtuTuningProposalResult();
            d.Id = 48; d.Value = 55m; d.Enabled = true; d.RebootRequired = false;
            delta.Add(d);
            sb.AppendLine("proposal-delta\t" + Ser(delta, typeof(List<IronCity.Core.XtuTuningProposalResult>)));
            return sb.ToString();
        }

        // Reads a file written by Dump() (from either runtime) and prints what was understood.
        public static string Load(string text)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = line.IndexOf('\t');
                string name = line.Substring(0, tab), xml = line.Substring(tab + 1);
                try
                {
                    if (name.StartsWith("token"))
                    {
                        IronCity.Common.SdkClientToken t = (IronCity.Common.SdkClientToken)De(xml, typeof(IronCity.Common.SdkClientToken));
                        sb.AppendLine(name + ": ProcessId=" + t.ProcessId);
                    }
                    else if (name == "proposals")
                    {
                        List<IronCity.Core.XtuTuningProposal> l = (List<IronCity.Core.XtuTuningProposal>)De(xml, typeof(List<IronCity.Core.XtuTuningProposal>));
                        List<string> parts = new List<string>();
                        foreach (IronCity.Core.XtuTuningProposal p in l) parts.Add(p.Id + "=" + p.Value);
                        sb.AppendLine(name + ": " + string.Join(", ", parts.ToArray()));
                    }
                    else if (name.StartsWith("result"))
                    {
                        IronCity.Common.TuningResult r = (IronCity.Common.TuningResult)De(xml, typeof(IronCity.Common.TuningResult));
                        List<string> parts = new List<string>();
                        if (r.AllCodes != null) foreach (KeyValuePair<uint, IronCity.Common.TuningErrorCode> kv in r.AllCodes) parts.Add(kv.Key + ":" + kv.Value);
                        sb.AppendLine(name + ": General=" + r.GeneralCode + " All=[" + string.Join(", ", parts.ToArray()) + "]");
                    }
                    else if (name == "proposal-delta")
                    {
                        List<IronCity.Core.XtuTuningProposalResult> l = (List<IronCity.Core.XtuTuningProposalResult>)De(xml, typeof(List<IronCity.Core.XtuTuningProposalResult>));
                        List<string> parts = new List<string>();
                        foreach (IronCity.Core.XtuTuningProposalResult p in l) parts.Add(p.Id + "=" + p.Value + " enabled=" + p.Enabled + " reboot=" + p.RebootRequired);
                        sb.AppendLine(name + ": " + string.Join(", ", parts.ToArray()));
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine(name + ": READ FAILED " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            return sb.ToString();
        }
    }
}
