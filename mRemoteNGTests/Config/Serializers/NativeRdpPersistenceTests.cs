using System.Linq;
using System.Security;
using mRemoteNG.Config.Serializers.ConnectionSerializers.Sql;
using mRemoteNG.Config.Serializers.ConnectionSerializers.Xml;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;
using mRemoteNG.Security;
using mRemoteNG.Security.SymmetricEncryption;
using mRemoteNG.Tools;
using NUnit.Framework;

namespace mRemoteNGTests.Config.Serializers;

[TestFixture]
public class NativeRdpPersistenceTests
{
    private const string Password = "mR3m";

    private const string RootAttributes =
        """
        Name="Connections" Export="False" EncryptionEngine="AES" BlockCipherMode="GCM"
        KdfIterations="1000" FullFileEncryption="False"
        Protected="8LmIO3+MWBY0zTmfjfOEdCGxhTAwnlohb1veTGNZFt6lAYvY2UOzWyjVzkx6V93smpbP0ZOuexN15u7rvwJEjawC"
        ConfVersion="2.8"
        """;

    [Test]
    public void XmlDeserializerReadsNativeMode()
    {
        string xml =
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections {RootAttributes}>
                <Node Id="native-rdp" Name="Native" Type="Connection"
                      RdpClientMode="NativeMstsc" />
            </Connections>
            """;

        XmlConnectionsDeserializer deserializer = new(() => Password.ConvertToSecureString());
        ConnectionInfo connection = deserializer.Deserialize(xml).RootNodes[0].Children.First();

        Assert.That(connection.RdpClientMode, Is.EqualTo(RdpClientMode.NativeMstsc));
    }

    [Test]
    public void XmlDeserializerDefaultsMissingModeToEmbedded()
    {
        string xml =
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections {RootAttributes}>
                <Node Id="embedded-rdp" Name="Embedded" Type="Connection" />
            </Connections>
            """;

        XmlConnectionsDeserializer deserializer = new(() => Password.ConvertToSecureString());
        ConnectionInfo connection = deserializer.Deserialize(xml).RootNodes[0].Children.First();

        Assert.That(connection.RdpClientMode, Is.EqualTo(RdpClientMode.Embedded));
    }

    [Test]
    public void SqlSerializerUsesSchemaVersion31AndPersistsMode()
    {
        DataTableSerializer serializer = new(
            new SaveFilter(),
            new LegacyRijndaelCryptographyProvider(),
            new SecureString());
        ConnectionInfo connection = new()
        {
            RdpClientMode = RdpClientMode.NativeMstsc
        };

        var table = serializer.Serialize(connection);

        Assert.That(serializer.Version, Is.EqualTo(new System.Version(3, 1)));
        Assert.That(table.Columns.Contains("RdpClientMode"), Is.True);
        Assert.That(table.Rows[0]["RdpClientMode"].ToString(), Is.EqualTo("NativeMstsc"));
    }
}
