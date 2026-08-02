using mRemoteNG.Config.Serializers.ConnectionSerializers.Sql;
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
    }
}
