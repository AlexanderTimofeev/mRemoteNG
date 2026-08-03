using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;
using NUnit.Framework;

namespace mRemoteNGTests.Connection.Protocol.RDP
{
    [TestFixture]
    public class NativeRdpLauncherTests
    {
        [Test]
        public void BuildArgumentsHonorsFullscreenAndConsoleForceFlags()
        {
            ConnectionInfo connectionInfo = new()
            {
                UseConsoleSession = false
            };

            var arguments = NativeRdpLauncher.BuildArguments(
                "session.rdp",
                connectionInfo,
                ConnectionInfo.Force.Fullscreen | ConnectionInfo.Force.UseConsoleSession,
                prompt: false);

            Assert.That(arguments[0], Is.EqualTo("session.rdp"));
            Assert.That(arguments, Does.Contain("/f"));
            Assert.That(arguments, Does.Contain("/admin"));
        }

        [Test]
        public void BuildArgumentsCanSuppressConfiguredConsoleSession()
        {
            ConnectionInfo connectionInfo = new()
            {
                UseConsoleSession = true
            };

            var arguments = NativeRdpLauncher.BuildArguments(
                "session.rdp",
                connectionInfo,
                ConnectionInfo.Force.DontUseConsoleSession,
                prompt: false);

            Assert.That(arguments, Does.Not.Contain("/admin"));
        }

        [Test]
        public void BuildArgumentsAddsCredentiallessSecuritySwitches()
        {
            ConnectionInfo restrictedAdmin = new() { UseRestrictedAdmin = true };
            ConnectionInfo remoteGuard = new() { UseRCG = true };

            var restrictedArguments = NativeRdpLauncher.BuildArguments(
                "restricted.rdp", restrictedAdmin, ConnectionInfo.Force.None, prompt: false);
            var remoteGuardArguments = NativeRdpLauncher.BuildArguments(
                "guard.rdp", remoteGuard, ConnectionInfo.Force.None, prompt: false);

            Assert.That(restrictedArguments, Does.Contain("/restrictedAdmin"));
            Assert.That(remoteGuardArguments, Does.Contain("/remoteGuard"));
            Assert.That(restrictedArguments.Any(x => x.Contains("password", StringComparison.OrdinalIgnoreCase)), Is.False);
            Assert.That(remoteGuardArguments.Any(x => x.Contains("password", StringComparison.OrdinalIgnoreCase)), Is.False);
        }

        [Test]
        public void BuildArgumentsAddsPromptOnlyWhenRequested()
        {
            ConnectionInfo connectionInfo = new();

            var arguments = NativeRdpLauncher.BuildArguments(
                "session.rdp", connectionInfo, ConnectionInfo.Force.None, prompt: true);

            Assert.That(arguments, Does.Contain("/prompt"));
        }

        [Test]
        public void SigningHashNormalizationRemovesSeparatorsAndUppercasesHex()
        {
            string normalized = NativeRdpFileSigner.NormalizeHash("75 20-e6:c9");

            Assert.That(normalized, Is.EqualTo("7520E6C9"));
        }

        [Test]
        public void ManagedSignerAddsScopeAlternateAddressAndSignature()
        {
            using RSA key = RSA.Create(2048);
            CertificateRequest request = new(
                "CN=mRemoteNG managed signing test",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));

            using X509Certificate2 certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1));

            const string source =
                "full address:s:test.example.local\r\n" +
                "server port:i:3389\r\n" +
                "redirectclipboard:i:1\r\n";

            string signed = NativeRdpFileSigner.SignContent(source, certificate);

            Assert.That(signed, Does.Contain("alternate full address:s:test.example.local\r\n"));
            Assert.That(signed, Does.Contain(
                "signscope:s:Full Address,Alternate Full Address,Server Port,RedirectClipboard\r\n"));
            Assert.That(signed, Does.Match(@"signature:s:[A-Za-z0-9+/= ]+\r\n$"));
        }
    }
}
