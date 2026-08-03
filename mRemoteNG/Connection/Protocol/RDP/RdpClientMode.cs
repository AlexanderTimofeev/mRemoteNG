using System.ComponentModel;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public enum RdpClientMode
    {
        [Description("Embedded mRemoteNG tab")]
        Embedded = 0,

        [Description("mstsc (Native Windows Remote Desktop client)")]
        NativeMstsc = 1
    }
}
