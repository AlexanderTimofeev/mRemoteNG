using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;
using NUnit.Framework;

namespace mRemoteNGTests.Connection.Protocol.RDP
{
    [TestFixture]
    public class RdpFileSerializerDriveTests
    {
        [Test]
        public void AllDrivesUsesWildcard()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                RedirectDiskDrives = RDPDiskDrives.All
            };

            string result = RdpFileSerializer.Serialize(connectionInfo);

            Assert.That(result, Does.Contain("redirectdrives:i:1"));
            Assert.That(result, Does.Contain("drivestoredirect:s:*"));
        }

        [Test]
        public void CustomDrivesAreNormalizedForRdpFileSyntax()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                RedirectDiskDrives = RDPDiskDrives.Custom,
                RedirectDiskDrivesCustom = "c, E; c"
            };

            string result = RdpFileSerializer.Serialize(connectionInfo);

            Assert.That(result, Does.Contain("drivestoredirect:s:C:\\;E:\\;"));
        }

        [Test]
        public void NoDrivesDisablesDriveRedirection()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                RedirectDiskDrives = RDPDiskDrives.None
            };

            string result = RdpFileSerializer.Serialize(connectionInfo);

            Assert.That(result, Does.Contain("redirectdrives:i:0"));
            Assert.That(result, Does.Not.Contain("drivestoredirect"));
        }
    }
}
