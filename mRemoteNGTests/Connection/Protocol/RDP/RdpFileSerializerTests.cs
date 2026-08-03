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
        public void SerializeUsesResolvedCredentialHintsInsteadOfStoredPlaceholders()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                Username = "stored-user",
                Domain = "STORED"
            };
            RdpResolvedCredentials resolved = new("resolved-user", "resolved-secret", "RESOLVED");

            string result = RdpFileSerializer.Serialize(
                connectionInfo,
                resolved,
                RdpResolvedCredentials.Empty);

            Assert.That(result, Does.Contain("username:s:RESOLVED\\resolved-user"));
            Assert.That(result, Does.Contain("domain:s:RESOLVED"));
            Assert.That(result, Does.Not.Contain("stored-user"));
            Assert.That(result, Does.Not.Contain("resolved-secret"));
        }

        [Test]
        public void SerializeWritesGatewayHintAndPromptOnceForSharedCredentials()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
                RDGatewayHostname = "gateway.example.test",
                RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.Yes
            };
            RdpResolvedCredentials credentials = new("alice", "secret", "CONTOSO");

            string result = RdpFileSerializer.Serialize(connectionInfo, credentials, credentials);

            Assert.That(result, Does.Contain("gatewayhostname:s:gateway.example.test"));
            Assert.That(result, Does.Contain("gatewayusername:s:CONTOSO\\alice"));
            Assert.That(result, Does.Contain("promptcredentialonce:i:1"));
            Assert.That(result, Does.Not.Contain("secret"));
        }

        [Test]
        public void SerializeDoesNotReuseSignatureForRegeneratedFile()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                RDPSignScope = "Full Address,GatewayHostname",
                RDPSignature = "invalid-after-regeneration"
            };

            string result = RdpFileSerializer.Serialize(connectionInfo);
            string lowerResult = result.ToLowerInvariant();

            Assert.That(lowerResult, Does.Not.Contain("signscope"));
            Assert.That(lowerResult, Does.Not.Contain("signature"));
            Assert.That(result, Does.Not.Contain("invalid-after-regeneration"));
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
