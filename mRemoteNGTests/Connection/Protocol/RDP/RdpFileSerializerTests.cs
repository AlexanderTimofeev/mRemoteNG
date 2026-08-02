using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;
using NUnit.Framework;

namespace mRemoteNGTests.Connection.Protocol.RDP
{
    [TestFixture]
    public class RdpFileSerializerTests
    {
        [Test]
        public void ExistingConnectionsDefaultToEmbeddedMode()
        {
            ConnectionInfo connectionInfo = new();
            Assert.That(connectionInfo.RdpClientMode, Is.EqualTo(RdpClientMode.Embedded));
        }

        [Test]
        public void SerializeWritesAddressPortAndUsernameButNeverPassword()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                Port = 3391,
                Domain = "CONTOSO",
                Username = "alice",
                Password = "secret-value"
            };

            string result = RdpFileSerializer.Serialize(connectionInfo);

            Assert.That(result, Does.Contain("full address:s:rdp.example.test:3391"));
            Assert.That(result, Does.Contain("server port:i:3391"));
            Assert.That(result, Does.Contain("username:s:CONTOSO\\alice"));
            Assert.That(result, Does.Not.Contain("secret-value"));
            Assert.That(result, Does.Not.Contain("password 51"));
        }

        [Test]
        public void SerializeMapsPredefinedResolution()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                Resolution = RDPResolutions.Res1920x1080
            };

            string result = RdpFileSerializer.Serialize(connectionInfo);

            Assert.That(result, Does.Contain("desktopwidth:i:1920"));
            Assert.That(result, Does.Contain("desktopheight:i:1080"));
        }

        [Test]
        public void SerializeMapsCustomResolution()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                Resolution = RDPResolutions.Custom,
                ResolutionWidth = 1720,
                ResolutionHeight = 980
            };

            string result = RdpFileSerializer.Serialize(connectionInfo);

            Assert.That(result, Does.Contain("desktopwidth:i:1720"));
            Assert.That(result, Does.Contain("desktopheight:i:980"));
        }

        [Test]
        public void SerializeEnablesSmartSizing()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                Resolution = RDPResolutions.SmartSize
            };

            string result = RdpFileSerializer.Serialize(connectionInfo);

            Assert.That(result, Does.Contain("smart sizing:i:1"));
        }

        [TestCase(RDPDesktopScaleFactor.Scale100, 100)]
        [TestCase(RDPDesktopScaleFactor.Scale125, 125)]
        [TestCase(RDPDesktopScaleFactor.Scale150, 150)]
        [TestCase(RDPDesktopScaleFactor.Scale200, 200)]
        public void SerializeMapsDesktopScaleFactorToPercentage(RDPDesktopScaleFactor scaleFactor, int expected)
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                DesktopScaleFactor = scaleFactor
            };

            string result = RdpFileSerializer.Serialize(connectionInfo);

            Assert.That(result, Does.Contain($"desktopscalefactor:i:{expected}"));
        }

        [Test]
        public void SerializeOmitsDesktopScaleFactorWhenAuto()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                DesktopScaleFactor = RDPDesktopScaleFactor.Auto
            };

            string result = RdpFileSerializer.Serialize(connectionInfo);

            Assert.That(result, Does.Not.Contain("desktopscalefactor"));
        }

        [Test]
        public void NativeModeCanBeStoredOnConnection()
        {
            ConnectionInfo connectionInfo = new() { RdpClientMode = RdpClientMode.NativeMstsc };
            Assert.That(connectionInfo.RdpClientMode, Is.EqualTo(RdpClientMode.NativeMstsc));
        }
    }
}
