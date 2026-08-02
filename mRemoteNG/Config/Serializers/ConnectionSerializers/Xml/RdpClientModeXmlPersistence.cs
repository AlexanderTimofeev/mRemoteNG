using System;
using System.Xml.Linq;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;

namespace mRemoteNG.Config.Serializers.ConnectionSerializers.Xml
{
    internal static class RdpClientModeXmlPersistence
    {
        public static void Apply(ConnectionInfo connectionInfo, XElement element)
        {
            ArgumentNullException.ThrowIfNull(connectionInfo);
            ArgumentNullException.ThrowIfNull(element);
            string? value = element.Attribute("RdpClientMode")?.Value;
            connectionInfo.RdpClientMode = Enum.TryParse(value, true, out RdpClientMode mode)
                ? mode
                : RdpClientMode.Embedded;
        }
    }
}
