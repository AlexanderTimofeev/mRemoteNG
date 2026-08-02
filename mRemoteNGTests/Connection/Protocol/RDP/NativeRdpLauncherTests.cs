using System.Linq;
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
            Assert.That(restrictedArguments.Any(x => x.Contains("password", System.StringComparison.OrdinalIgnoreCase)), Is.False);
            Assert.That(remoteGuardArguments.Any(x => x.Contains("password", System.StringComparison.OrdinalIgnoreCase)), Is.False);
        }

        [Test]
        public void BuildArgumentsAddsPromptOnlyWhenRequested()
        {
            ConnectionInfo connectionInfo = new();

            var arguments = NativeRdpLauncher.BuildArguments(
                "session.rdp", connectionInfo, ConnectionInfo.Force.None, prompt: true);

            Assert.That(arguments, Does.Contain("/prompt"));
        }
    }
}
