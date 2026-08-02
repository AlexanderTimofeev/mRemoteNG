using System.ComponentModel;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public enum RdpClientMode
    {
        [Description("Embedded mRemoteNG tab")]
        Embedded = 0,

        [Description("Native Windows Remote Desktop client (mstsc.exe)")]
        NativeMstsc = 1
    }
}
