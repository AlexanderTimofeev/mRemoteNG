using System;
using System.Linq;
using System.Security;
using mRemoteNG.Config;
using mRemoteNG.Config.Serializers.ConnectionSerializers.Sql;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol;
using mRemoteNG.Connection.Protocol.RDP;
using mRemoteNG.Security.SymmetricEncryption;
using mRemoteNG.Tree;
using NUnit.Framework;

namespace mRemoteNGTests.Config.Serializers.ConnectionSerializers.Sql
{
    [TestFixture]
    public class NativeRdpDataTableSchemaTests
    {
        [Test]
        public void ExpectedSchemaContainsRdpClientMode()
        {
            var schema = DataTableSerializer.GetExpectedSchema();
            Assert.That(schema.Columns.Contains("RdpClientMode"), Is.True);
            Assert.That(schema.Columns["RdpClientMode"]!.DataType, Is.EqualTo(typeof(string)));
        }

        [Test]
        public void NativeModeSurvivesSqlDataTableRoundTrip()
        {
            using SecureString key = new();
            LegacyRijndaelCryptographyProvider cryptographyProvider = new();
            DataTableSerializer serializer = new(new SaveFilter(), cryptographyProvider, key);
            ConnectionInfo source = new("native-rdp-test")
            {
                Name = "Native RDP",
                Hostname = "rdp.example.test",
                Protocol = ProtocolType.RDP,
                RdpClientMode = RdpClientMode.NativeMstsc
            };

            var table = serializer.Serialize(source);
            Assert.That(table.Rows[0]["RdpClientMode"], Is.EqualTo(RdpClientMode.NativeMstsc.ToString()));

            DataTableDeserializer deserializer = new(cryptographyProvider, key);
            ConnectionTreeModel result = deserializer.Deserialize(table);
            ConnectionInfo restored = result.GetRecursiveChildList().Single(
                x => string.Equals(x.ConstantID, source.ConstantID, StringComparison.Ordinal));

            Assert.That(restored.RdpClientMode, Is.EqualTo(RdpClientMode.NativeMstsc));
        }

        [Test]
        public void MissingSqlColumnDefaultsToEmbeddedMode()
        {
            using SecureString key = new();
            LegacyRijndaelCryptographyProvider cryptographyProvider = new();
            DataTableSerializer serializer = new(new SaveFilter(), cryptographyProvider, key);
            ConnectionInfo source = new("legacy-rdp-test")
            {
                Name = "Legacy RDP",
                Hostname = "rdp.example.test",
                Protocol = ProtocolType.RDP,
                RdpClientMode = RdpClientMode.NativeMstsc
            };

            var legacyTable = serializer.Serialize(source);
            legacyTable.Columns.Remove("RdpClientMode");

            DataTableDeserializer deserializer = new(cryptographyProvider, key);
            ConnectionTreeModel result = deserializer.Deserialize(legacyTable);
            ConnectionInfo restored = result.GetRecursiveChildList().Single(
                x => string.Equals(x.ConstantID, source.ConstantID, StringComparison.Ordinal));

            Assert.That(restored.RdpClientMode, Is.EqualTo(RdpClientMode.Embedded));
        }
    }
}
