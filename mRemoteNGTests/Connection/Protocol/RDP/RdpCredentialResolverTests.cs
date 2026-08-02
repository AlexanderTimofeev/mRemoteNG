using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;
using NUnit.Framework;

namespace mRemoteNGTests.Connection.Protocol.RDP
{
    [TestFixture]
    public class RdpCredentialResolverTests
    {
        [Test]
        public void ResolveDestinationSplitsDomainPrefix()
        {
            ConnectionInfo connectionInfo = new()
            {
                Username = "CONTOSO\\alice",
                Password = "secret",
                Domain = string.Empty
            };

            RdpResolvedCredentials result = RdpCredentialResolver.ResolveDestination(
                connectionInfo,
                ConnectionInfo.Force.None);

            Assert.That(result.Username, Is.EqualTo("alice"));
            Assert.That(result.Domain, Is.EqualTo("CONTOSO"));
            Assert.That(result.Password, Is.EqualTo("secret"));
        }

        [Test]
        public void ResolveGatewayReusesDestinationCredentialsWhenConfigured()
        {
            ConnectionInfo connectionInfo = new()
            {
                RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
                RDGatewayHostname = "gateway.example.test",
                RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.Yes
            };
            RdpResolvedCredentials destination = new("alice", "secret", "CONTOSO");

            RdpResolvedCredentials result = RdpCredentialResolver.ResolveGateway(
                connectionInfo,
                destination,
                ConnectionInfo.Force.None);

            Assert.That(result, Is.EqualTo(destination));
        }

        [Test]
        public void NoCredentialsForceReturnsEmptyCredentials()
        {
            ConnectionInfo connectionInfo = new()
            {
                Username = "alice",
                Password = "secret"
            };

            RdpResolvedCredentials result = RdpCredentialResolver.ResolveDestination(
                connectionInfo,
                ConnectionInfo.Force.NoCredentials);

            Assert.That(result, Is.EqualTo(RdpResolvedCredentials.Empty));
        }
    }
}
