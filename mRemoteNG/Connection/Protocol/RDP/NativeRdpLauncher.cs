using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using mRemoteNG.App;
using mRemoteNG.Messages;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public sealed class NativeRdpLauncher
    {
        private readonly TemporaryRdpFileStore _fileStore;

        public NativeRdpLauncher()
            : this(new TemporaryRdpFileStore())
        {
        }

        public NativeRdpLauncher(TemporaryRdpFileStore fileStore)
        {
            _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        }

        public bool Launch(ConnectionInfo connectionInfo, ConnectionInfo.Force force)
        {
            ArgumentNullException.ThrowIfNull(connectionInfo);
            string? rdpPath = null;
            try
            {
                Validate(connectionInfo, force);
                string executable = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
                if (!File.Exists(executable))
                    throw new FileNotFoundException("The native Windows Remote Desktop client was not found.", executable);

                bool integratedSecurity = connectionInfo.UseRestrictedAdmin || connectionInfo.UseRCG;
                bool suppressCredentialInjection =
                    force.HasFlag(ConnectionInfo.Force.NoCredentials) ||
                    connectionInfo.AlwaysPromptForCredentials;
                bool prompt = !integratedSecurity && suppressCredentialInjection;

                RdpResolvedCredentials resolvedConnectionCredentials =
                    !suppressCredentialInjection && !connectionInfo.UseRCG
                        ? RdpCredentialResolver.ResolveDestination(connectionInfo, force)
                        : RdpResolvedCredentials.Empty;

                RdpResolvedCredentials destinationCredentials = integratedSecurity
                    ? RdpResolvedCredentials.Empty
                    : prompt
                        ? BuildCredentialHint(connectionInfo.Username, connectionInfo.Domain)
                        : resolvedConnectionCredentials;

                RdpResolvedCredentials gatewayCredentials = suppressCredentialInjection || connectionInfo.UseRCG
                    ? RdpResolvedCredentials.Empty
                    : RdpCredentialResolver.ResolveGateway(
                        connectionInfo,
                        resolvedConnectionCredentials,
                        force);

                WriteCredentialIfAvailable(connectionInfo.Hostname, destinationCredentials);
                WriteGatewayCredentialIfAvailable(connectionInfo, resolvedConnectionCredentials, gatewayCredentials);

                bool includeGatewayAccessToken = !suppressCredentialInjection && !connectionInfo.UseRCG;
                rdpPath = _fileStore.Create(
                    RdpFileSerializer.Serialize(
                        connectionInfo,
                        destinationCredentials,
                        gatewayCredentials,
                        includeGatewayAccessToken));

                ProcessStartInfo startInfo = new(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.SystemDirectory
                };

                foreach (string argument in BuildArguments(rdpPath, connectionInfo, force, prompt))
                    startInfo.ArgumentList.Add(argument);

                using Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("mstsc.exe did not return a process instance.");

                TemporaryRdpFileStore.ScheduleDelete(rdpPath);
                Runtime.MessageCollector.AddMessage(
                    MessageClass.InformationMsg,
                    string.Format(CultureInfo.InvariantCulture, "Launched mstsc.exe for RDP connection '{0}'.", connectionInfo.Name));
                return true;
            }
            catch (Exception ex)
            {
                TemporaryRdpFileStore.TryDelete(rdpPath);
                Runtime.MessageCollector.AddExceptionMessage("Unable to open the native Windows RDP client.", ex);
                return false;
            }
        }

        internal static IReadOnlyList<string> BuildArguments(
            string rdpPath,
            ConnectionInfo connectionInfo,
            ConnectionInfo.Force force,
            bool prompt)
        {
            List<string> arguments = [rdpPath];

            bool useAdmin = force.HasFlag(ConnectionInfo.Force.UseConsoleSession) ||
                            (!force.HasFlag(ConnectionInfo.Force.DontUseConsoleSession) && connectionInfo.UseConsoleSession);
            if (useAdmin)
                arguments.Add("/admin");
            if (force.HasFlag(ConnectionInfo.Force.Fullscreen))
                arguments.Add("/f");
            if (connectionInfo.UseRestrictedAdmin)
                arguments.Add("/restrictedAdmin");
            if (connectionInfo.UseRCG)
                arguments.Add("/remoteGuard");
            if (prompt)
                arguments.Add("/prompt");

            return arguments;
        }

        public static string BuildCredentialTarget(string? hostname)
        {
            string host = (hostname ?? string.Empty).Trim().TrimStart('[').TrimEnd(']');
            return $"TERMSRV/{host}";
        }

        private static void WriteCredentialIfAvailable(
            string? hostname,
            RdpResolvedCredentials credentials)
        {
            if (!credentials.HasPassword)
                return;

            WindowsCredentialManager.Write(
                BuildCredentialTarget(hostname),
                RdpFileSerializer.BuildUsername(credentials.Username, credentials.Domain),
                credentials.Password);
        }

        private static void WriteGatewayCredentialIfAvailable(
            ConnectionInfo connectionInfo,
            RdpResolvedCredentials connectionCredentials,
            RdpResolvedCredentials gatewayCredentials)
        {
            if (!gatewayCredentials.HasPassword || string.IsNullOrWhiteSpace(connectionInfo.RDGatewayHostname))
                return;

            string destinationTarget = BuildCredentialTarget(connectionInfo.Hostname);
            string gatewayTarget = BuildCredentialTarget(connectionInfo.RDGatewayHostname);
            bool sameTarget = string.Equals(destinationTarget, gatewayTarget, StringComparison.OrdinalIgnoreCase);
            bool sameCredentials = gatewayCredentials.Equals(connectionCredentials);

            // Credential Manager can hold only one generic credential for a target. If an unusual
            // setup uses the destination host itself as a gateway with different credentials, keep
            // the destination credential and allow mstsc to prompt for the gateway credential.
            if (sameTarget && !sameCredentials)
                return;

            WindowsCredentialManager.Write(
                gatewayTarget,
                RdpFileSerializer.BuildUsername(gatewayCredentials.Username, gatewayCredentials.Domain),
                gatewayCredentials.Password);
        }

        private static RdpResolvedCredentials BuildCredentialHint(string? username, string? domain)
        {
            string normalizedUsername = username ?? string.Empty;
            string normalizedDomain = domain ?? string.Empty;
            if (string.IsNullOrEmpty(normalizedDomain))
            {
                (normalizedUsername, normalizedDomain) = RdpProtocol.ParseDomainFromUsername(normalizedUsername);
            }

            return new RdpResolvedCredentials(normalizedUsername, string.Empty, normalizedDomain);
        }

        private static void Validate(ConnectionInfo connectionInfo, ConnectionInfo.Force force)
        {
            if (string.IsNullOrWhiteSpace(connectionInfo.Hostname))
                throw new InvalidOperationException("A hostname is required for native RDP launch.");
            if (connectionInfo.Port is < 0 or > 65535)
                throw new InvalidOperationException($"The RDP port '{connectionInfo.Port}' is outside the valid range.");
            if (connectionInfo.UseRestrictedAdmin && connectionInfo.UseRCG)
                throw new InvalidOperationException("Restricted Admin and Remote Credential Guard cannot be enabled at the same time.");
            if (connectionInfo.UseRCG &&
                connectionInfo.RDGatewayUsageMethod != RDGatewayUsageMethod.Never &&
                !string.IsNullOrWhiteSpace(connectionInfo.RDGatewayHostname))
            {
                throw new NotSupportedException(
                    "Remote Credential Guard is supported only for direct RDP connections and cannot be used through RD Gateway.");
            }
            if (connectionInfo.UseRCG &&
                (!string.IsNullOrWhiteSpace(connectionInfo.LoadBalanceInfo) || connectionInfo.UseRedirectionServerName))
            {
                throw new NotSupportedException(
                    "Remote Credential Guard is not supported through an RD Connection Broker. Use a direct connection or disable Remote Credential Guard.");
            }
            if (force.HasFlag(ConnectionInfo.Force.ViewOnly))
                throw new NotSupportedException("View-only mode is not available in the native Windows RDP client. Use Embedded mode for this launch.");
            if (connectionInfo.UseVmId || connectionInfo.UseEnhancedMode)
                throw new NotSupportedException("Hyper-V VM ID and Enhanced Session connections require the embedded RDP control. Use Embedded mode for this connection.");
        }
    }
}
