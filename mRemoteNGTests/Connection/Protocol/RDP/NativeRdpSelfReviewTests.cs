using System;
using mRemoteNG.App;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;
using mRemoteNG.Properties;
using mRemoteNG.Security.SymmetricEncryption;
using NUnit.Framework;

namespace mRemoteNGTests.Connection.Protocol.RDP
{
    [TestFixture]
    [NonParallelizable]
    public class NativeRdpSelfReviewTests
    {
        [Test]
        public void ExplicitUsernameStillUsesConfiguredDefaultDomainAndPassword()
        {
            string oldMode = OptionsCredentialsPage.Default.EmptyCredentials;
            string oldUsername = OptionsCredentialsPage.Default.DefaultUsername;
            string oldPassword = OptionsCredentialsPage.Default.DefaultPassword;
            string oldDomain = OptionsCredentialsPage.Default.DefaultDomain;

            try
            {
                LegacyRijndaelCryptographyProvider cryptographyProvider = new();
                OptionsCredentialsPage.Default.EmptyCredentials = "custom";
                OptionsCredentialsPage.Default.DefaultUsername = "default-user";
                OptionsCredentialsPage.Default.DefaultPassword = cryptographyProvider.Encrypt(
                    "default-secret",
                    Runtime.EncryptionKey);
                OptionsCredentialsPage.Default.DefaultDomain = "DEFAULT-DOMAIN";

                ConnectionInfo connectionInfo = new()
                {
                    Username = "explicit-user",
                    Password = string.Empty,
                    Domain = string.Empty
                };

                RdpResolvedCredentials result = RdpCredentialResolver.ResolveDestination(
                    connectionInfo,
                    ConnectionInfo.Force.None);

                Assert.That(result.Username, Is.EqualTo("explicit-user"));
                Assert.That(result.Password, Is.EqualTo("default-secret"));
                Assert.That(result.Domain, Is.EqualTo("DEFAULT-DOMAIN"));
            }
            finally
            {
                OptionsCredentialsPage.Default.EmptyCredentials = oldMode;
                OptionsCredentialsPage.Default.DefaultUsername = oldUsername;
                OptionsCredentialsPage.Default.DefaultPassword = oldPassword;
                OptionsCredentialsPage.Default.DefaultDomain = oldDomain;
            }
        }

        [Test]
        public void SharedGatewayCredentialsUsePasswordSourceAndExplicitGatewayProfile()
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

            Assert.That(result, Does.Contain("gatewaycredentialssource:i:0"));
            Assert.That(result, Does.Contain("gatewayprofileusagemethod:i:1"));
            Assert.That(result, Does.Contain("promptcredentialonce:i:1"));
            Assert.That(result, Does.Not.Contain("gatewaycredentialssource:i:2"));
        }

        [Test]
        public void GatewayAccessTokenIsOnlyWrittenForTokenModeAndWhenCredentialsAreAllowed()
        {
            ConnectionInfo passwordMode = new()
            {
                Hostname = "rdp.example.test",
                RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
                RDGatewayHostname = "gateway.example.test",
                RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.Yes,
                RDGatewayAccessToken = "stale-token"
            };

            string passwordResult = RdpFileSerializer.Serialize(
                passwordMode,
                RdpResolvedCredentials.Empty,
                RdpResolvedCredentials.Empty);

            Assert.That(passwordResult, Does.Not.Contain("stale-token"));
            Assert.That(passwordResult, Does.Not.Contain("gatewayaccesstoken"));

            ConnectionInfo tokenMode = new()
            {
                Hostname = "rdp.example.test",
                RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
                RDGatewayHostname = "gateway.example.test",
                RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.AccessToken,
                RDGatewayAccessToken = "active-token"
            };

            string allowedResult = RdpFileSerializer.Serialize(
                tokenMode,
                RdpResolvedCredentials.Empty,
                RdpResolvedCredentials.Empty,
                includeGatewayAccessToken: true);
            string suppressedResult = RdpFileSerializer.Serialize(
                tokenMode,
                RdpResolvedCredentials.Empty,
                RdpResolvedCredentials.Empty,
                includeGatewayAccessToken: false);

            Assert.That(allowedResult, Does.Contain("gatewayaccesstoken:s:active-token"));
            Assert.That(suppressedResult, Does.Not.Contain("active-token"));
            Assert.That(suppressedResult, Does.Not.Contain("gatewayaccesstoken"));
        }

        [Test]
        public void StringValuesCannotInjectAdditionalRdpProperties()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                RDPStartProgram = "notepad.exe\r\nmalicious-property:i:1"
            };

            string result = RdpFileSerializer.Serialize(connectionInfo);

            Assert.That(result, Does.Not.Contain("\r\nmalicious-property:i:1\r\n"));
            Assert.That(result, Does.Contain("alternate shell:s:notepad.exe  malicious-property:i:1"));
        }

        [Test]
        public void RemoteCredentialGuardRejectsRdGateway()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                UseRCG = true,
                RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
                RDGatewayHostname = "gateway.example.test"
            };

            Assert.That(
                () => NativeRdpLauncher.Validate(connectionInfo, ConnectionInfo.Force.None),
                Throws.TypeOf<NotSupportedException>());
        }

        [Test]
        public void RemoteCredentialGuardRejectsConnectionBrokerSettings()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                UseRCG = true,
                LoadBalanceInfo = "tsv://MS Terminal Services Plugin.1.collection"
            };

            Assert.That(
                () => NativeRdpLauncher.Validate(connectionInfo, ConnectionInfo.Force.None),
                Throws.TypeOf<NotSupportedException>());
        }

        [Test]
        public void RestrictedAdminAllowsASeparateGateway()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                UseRestrictedAdmin = true,
                RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
                RDGatewayHostname = "gateway.example.test"
            };

            Assert.That(
                () => NativeRdpLauncher.Validate(connectionInfo, ConnectionInfo.Force.None),
                Throws.Nothing);
        }
    }
}
