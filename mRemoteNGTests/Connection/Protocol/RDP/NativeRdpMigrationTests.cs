using System;
using System.Linq;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;
using NUnit.Framework;

namespace mRemoteNGTests.Connection.Protocol.RDP;

[TestFixture]
public class NativeRdpMigrationTests
{
    [Test]
    public void ClonePreservesNativeClientMode()
    {
        ConnectionInfo source = new()
        {
            RdpClientMode = RdpClientMode.NativeMstsc
        };

        ConnectionInfo clone = source.Clone();

        Assert.That(clone.RdpClientMode, Is.EqualTo(RdpClientMode.NativeMstsc));
    }

    [TestCase("server.example.test", 3389, "server.example.test")]
    [TestCase("server.example.test", 3390, "server.example.test:3390")]
    [TestCase("2001:db8::10", 3390, "[2001:db8::10]:3390")]
    public void FullAddressHandlesDefaultPortCustomPortAndIpv6(string host, int port, string expected)
    {
        Assert.That(RdpFileSerializer.BuildFullAddress(host, port), Is.EqualTo(expected));
    }

    [Test]
    public void GeneratedRdpFileDoesNotContainPasswordAndSanitizesLineBreaks()
    {
        ConnectionInfo connection = new()
        {
            Hostname = "rdp.example.test",
            RDPStartProgram = "notepad.exe\r\nmalicious-property:i:1"
        };
        RdpResolvedCredentials credentials = new("alice", "super-secret", "CONTOSO");

        string content = RdpFileSerializer.Serialize(connection, credentials, RdpResolvedCredentials.Empty);

        Assert.That(content, Does.Contain("username:s:CONTOSO\\alice"));
        Assert.That(content, Does.Not.Contain("super-secret"));
        Assert.That(content, Does.Not.Contain("\r\nmalicious-property:i:1\r\n"));
        Assert.That(content, Does.Contain("alternate shell:s:notepad.exe  malicious-property:i:1"));
    }

    [Test]
    public void GatewayUsesExplicitPasswordProfile()
    {
        ConnectionInfo connection = new()
        {
            Hostname = "rdp.example.test",
            RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
            RDGatewayHostname = "gateway.example.test",
            RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.Yes
        };
        RdpResolvedCredentials credentials = new("alice", "secret", "CONTOSO");

        string content = RdpFileSerializer.Serialize(connection, credentials, credentials);

        Assert.That(content, Does.Contain("gatewayprofileusagemethod:i:1"));
        Assert.That(content, Does.Contain("gatewaycredentialssource:i:0"));
        Assert.That(content, Does.Contain("promptcredentialonce:i:1"));
    }

    [Test]
    public void ForceFlagsBecomeNativeMstscArguments()
    {
        ConnectionInfo connection = new()
        {
            UseRestrictedAdmin = true
        };

        var arguments = NativeRdpLauncher.BuildArguments(
            "temporary.rdp",
            connection,
            ConnectionInfo.Force.Fullscreen | ConnectionInfo.Force.UseConsoleSession,
            prompt: false);

        Assert.That(arguments, Is.EqualTo(new[] { "temporary.rdp", "/admin", "/f", "/restrictedAdmin" }));
    }

    [Test]
    public void RemoteCredentialGuardRejectsGateway()
    {
        ConnectionInfo connection = new()
        {
            Hostname = "rdp.example.test",
            UseRCG = true,
            RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
            RDGatewayHostname = "gateway.example.test"
        };

        Assert.That(
            () => NativeRdpLauncher.Validate(connection, ConnectionInfo.Force.None),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void CredentialTargetUsesNormalizedTermsrvName()
    {
        Assert.That(
            NativeRdpLauncher.BuildCredentialTarget("[2001:db8::10]"),
            Is.EqualTo("TERMSRV/2001:db8::10"));
    }
}
