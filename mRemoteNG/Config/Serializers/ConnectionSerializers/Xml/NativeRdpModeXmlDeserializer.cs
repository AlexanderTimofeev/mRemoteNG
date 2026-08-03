using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security;
using System.Xml;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;
using mRemoteNG.Container;
using mRemoteNG.Tools;
using mRemoteNG.Tree;

namespace mRemoteNG.Config.Serializers.ConnectionSerializers.Xml
{
    /// <summary>
    /// Compatibility wrapper for the legacy XML deserializer. It lets the existing implementation
    /// perform authentication and full-file decryption, then applies the optional RdpClientMode
    /// attribute to the resulting connection tree.
    /// </summary>
    internal sealed class NativeRdpModeXmlDeserializer(Func<Optional<SecureString>> authenticationRequestor = null)
    {
        private readonly XmlConnectionsDeserializer _inner = new(authenticationRequestor);

        public ConnectionTreeModel Deserialize(string xml, bool import = false)
        {
            ConnectionTreeModel model = _inner.Deserialize(xml, import);
            ApplyNativeRdpModes(model);
            return model;
        }

        private void ApplyNativeRdpModes(ConnectionTreeModel model)
        {
            if (model == null) return;

            FieldInfo documentField = typeof(XmlConnectionsDeserializer).GetField(
                "_xmlDocument",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (documentField?.GetValue(_inner) is not XmlDocument document)
                return;

            Dictionary<string, ConnectionInfo> connectionsById = new(StringComparer.OrdinalIgnoreCase);
            foreach (ConnectionInfo root in model.RootNodes)
                IndexConnections(root, connectionsById);

            XmlNodeList nodes = document.SelectNodes("//Node[@Id]");
            if (nodes == null) return;

            foreach (XmlNode node in nodes)
            {
                string id = node.Attributes?["Id"]?.Value;
                string value = node.Attributes?["RdpClientMode"]?.Value;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value))
                    continue;

                if (connectionsById.TryGetValue(id, out ConnectionInfo connectionInfo) &&
                    Enum.TryParse(value, true, out RdpClientMode mode))
                {
                    connectionInfo.RdpClientMode = mode;
                }
            }
        }

        private static void IndexConnections(
            ConnectionInfo connectionInfo,
            IDictionary<string, ConnectionInfo> connectionsById)
        {
            connectionsById[connectionInfo.ConstantID] = connectionInfo;
            if (connectionInfo is not ContainerInfo container) return;

            foreach (ConnectionInfo child in container.Children)
                IndexConnections(child, connectionsById);
        }
    }
}
