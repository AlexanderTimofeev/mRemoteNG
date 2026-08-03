using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using mRemoteNG.App;
using mRemoteNG.Messages;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public sealed class NativeRdpLauncher
    {
        private const string SigningThumbprintEnvironmentVariable = "MREMOTENG_RDP_SIGN_CERT_THUMBPRINT";
        private const string SigningThumbprintFileName = "native-rdp-signing-thumbprint.txt";
        private static readonly TimeSpan SigningTimeout = TimeSpan.FromSeconds(30);
        private static int _signingFailureNotificationShown;

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
                    RdpFileSerializer.GetOptionalBool(connectionInfo, "AlwaysPromptForCredentials");
                bool prompt = !integratedSecurity && suppressCredentialInjection;
                bool gatewayUsesConnectionCredentials =
                    HasConfiguredGateway(connectionInfo) &&
                    connectionInfo.RDGatewayUseConnectionCredentials == RDGatewayUseConnectionCredentials.Yes;
                bool shouldResolveConnectionCredentials =
                    !suppressCredentialInjection &&
                    (!integratedSecurity || (connectionInfo.UseRestrictedAdmin && gatewayUsesConnectionCredentials));

                RdpResolvedCredentials resolvedConnectionCredentials = shouldResolveConnectionCredentials
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
                WriteGatewayCredentialIfAvailable(connectionInfo, destinationCredentials, gatewayCredentials);

                bool includeGatewayAccessToken = !suppressCredentialInjection && !connectionInfo.UseRCG;
                rdpPath = _fileStore.Create(
                    RdpFileSerializer.Serialize(
                        connectionInfo,
                        destinationCredentials,
                        gatewayCredentials,
                        includeGatewayAccessToken),
                    connectionInfo.Name);

                bool signed = TrySignRdpFileIfConfigured(rdpPath);

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
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Launched mstsc.exe for RDP connection '{0}'{1}.",
                        connectionInfo.Name,
                        signed ? " using a signed RDP file" : string.Empty));
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

        internal static string GetSigningThumbprintFilePath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "mRemoteNG",
            SigningThumbprintFileName);

        internal static string ResolveSigningThumbprint()
        {
            string? configuredValue = Environment.GetEnvironmentVariable(SigningThumbprintEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                string thumbprintFilePath = GetSigningThumbprintFilePath();
                if (File.Exists(thumbprintFilePath))
                    configuredValue = File.ReadAllText(thumbprintFilePath);
            }

            return NormalizeThumbprint(configuredValue);
        }

        internal static string NormalizeThumbprint(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return new string(value
                .Where(Uri.IsHexDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        }

        private static bool TrySignRdpFileIfConfigured(string rdpPath)
        {
            string thumbprint = ResolveSigningThumbprint();
            if (string.IsNullOrEmpty(thumbprint))
                return false;

            try
            {
                SignRdpFile(rdpPath, thumbprint);
                return true;
            }
            catch (Exception ex)
            {
                ReportSigningFailure(ex);
                return false;
            }
        }

        private static void ReportSigningFailure(Exception exception)
        {
            const string logMessage =
                "Unable to sign the temporary RDP file. mstsc will continue with an unsigned file.";

            // Keep full diagnostic details for every failed signing attempt.
            Runtime.MessageCollector.AddExceptionStackTrace(logMessage, exception);

            // A persistent certificate problem would otherwise show the same modal warning for every connection.
            // Notify the user once per application session, while continuing to log every failure.
            if (Interlocked.Exchange(ref _signingFailureNotificationShown, 1) != 0)
                return;

            string notification =
                "The temporary RDP file could not be signed.\r\n\r\n" +
                "mstsc will continue with an unsigned file, so Windows may display its security confirmation.\r\n\r\n" +
                "Reason: " + GetUserFriendlySigningFailure(exception) + "\r\n\r\n" +
                "The complete diagnostic output was written to the mRemoteNG log.";

            MessageBox.Show(
                notification,
                "mRemoteNG - RDP file signing failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private static string GetUserFriendlySigningFailure(Exception exception)
        {
            string message = exception.Message?.Trim() ?? "Unknown signing error.";
            int firstLineEnd = message.IndexOfAny(['\r', '\n']);
            if (firstLineEnd >= 0)
                message = message[..firstLineEnd].Trim();

            const int maxLength = 320;
            return message.Length <= maxLength
                ? message
                : message[..maxLength].TrimEnd() + "...";
        }

        private static void SignRdpFile(string rdpPath, string thumbprint)
        {
            string hashArgument = thumbprint.Length switch
            {
                40 => "/sha1",
                64 => "/sha256",
                _ => throw new InvalidOperationException(
                    $"The configured RDP signing certificate thumbprint has {thumbprint.Length} hexadecimal characters; expected 40 for SHA-1 or 64 for SHA-256.")
            };

            ValidateSigningCertificate(thumbprint);

            string signerExecutable = Path.Combine(Environment.SystemDirectory, "rdpsign.exe");
            if (!File.Exists(signerExecutable))
                throw new FileNotFoundException("rdpsign.exe was not found, so the temporary RDP file could not be signed.", signerExecutable);

            Encoding outputEncoding = GetRdpsignOutputEncoding();
            ProcessStartInfo signStartInfo = new(signerExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = outputEncoding,
                StandardErrorEncoding = outputEncoding,
                WorkingDirectory = Environment.SystemDirectory
            };
            signStartInfo.ArgumentList.Add(hashArgument);
            signStartInfo.ArgumentList.Add(thumbprint);
            signStartInfo.ArgumentList.Add("/q");
            signStartInfo.ArgumentList.Add(rdpPath);

            using Process signer = Process.Start(signStartInfo)
                ?? throw new InvalidOperationException("rdpsign.exe did not return a process instance.");

            string standardOutput = signer.StandardOutput.ReadToEnd();
            string standardError = signer.StandardError.ReadToEnd();
            if (!signer.WaitForExit((int)SigningTimeout.TotalMilliseconds))
            {
                try
                {
                    signer.Kill();
                }
                catch
                {
                    // Best effort. The process will terminate with the application if necessary.
                }

                throw new TimeoutException("Signing the temporary RDP file timed out after 30 seconds.");
            }

            if (signer.ExitCode != 0)
            {
                string details = string.Join(
                    Environment.NewLine,
                    new[] { standardError, standardOutput }
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value.Trim()));
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(details)
                        ? $"rdpsign.exe failed with exit code {signer.ExitCode}."
                        : $"rdpsign.exe failed with exit code {signer.ExitCode}.{Environment.NewLine}{details}");
            }
        }

        private static void ValidateSigningCertificate(string thumbprint)
        {
            bool certificateFound = false;
            bool certificateWithPrivateKeyFound = false;
            bool certificateIsCurrentlyValid = false;

            foreach (StoreLocation storeLocation in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                using X509Store store = new(StoreName.My, storeLocation);
                try
                {
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                }
                catch (CryptographicException)
                {
                    continue;
                }

                foreach (X509Certificate2 certificate in store.Certificates)
                {
                    string certificateThumbprint = thumbprint.Length == 64
                        ? NormalizeThumbprint(certificate.GetCertHashString(HashAlgorithmName.SHA256))
                        : NormalizeThumbprint(certificate.Thumbprint);

                    if (!string.Equals(certificateThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                        continue;

                    certificateFound = true;
                    if (!certificate.HasPrivateKey)
                        continue;

                    certificateWithPrivateKeyFound = true;
                    DateTime now = DateTime.Now;
                    if (now < certificate.NotBefore || now > certificate.NotAfter)
                        continue;

                    certificateIsCurrentlyValid = true;
                    break;
                }

                if (certificateIsCurrentlyValid)
                    break;
            }

            if (!certificateFound)
            {
                throw new InvalidOperationException(
                    "No certificate matching the configured RDP signing thumbprint was found in CurrentUser\\My or LocalMachine\\My. " +
                    "Verify native-rdp-signing-thumbprint.txt and the certificate installation.");
            }

            if (!certificateWithPrivateKeyFound)
            {
                throw new InvalidOperationException(
                    "The configured RDP signing certificate was found, but its private key is not available to the current user.");
            }

            if (!certificateIsCurrentlyValid)
            {
                throw new InvalidOperationException(
                    "The configured RDP signing certificate is expired or not yet valid.");
            }
        }

        private static Encoding GetRdpsignOutputEncoding()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        private static bool HasConfiguredGateway(ConnectionInfo connectionInfo) =>
            connectionInfo.RDGatewayUsageMethod != RDGatewayUsageMethod.Never &&
            !string.IsNullOrWhiteSpace(connectionInfo.RDGatewayHostname);

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
            RdpResolvedCredentials writtenDestinationCredentials,
            RdpResolvedCredentials gatewayCredentials)
        {
            if (!gatewayCredentials.HasPassword || string.IsNullOrWhiteSpace(connectionInfo.RDGatewayHostname))
                return;

            string destinationTarget = BuildCredentialTarget(connectionInfo.Hostname);
            string gatewayTarget = BuildCredentialTarget(connectionInfo.RDGatewayHostname);
            bool sameTarget = string.Equals(destinationTarget, gatewayTarget, StringComparison.OrdinalIgnoreCase);
            bool sameCredentials = gatewayCredentials.Equals(writtenDestinationCredentials);

            if (sameTarget && writtenDestinationCredentials.HasPassword && !sameCredentials)
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
                (normalizedUsername, normalizedDomain) = RdpFileSerializer.ParseDomainFromUsername(normalizedUsername);
            }

            return new RdpResolvedCredentials(normalizedUsername, string.Empty, normalizedDomain);
        }

        internal static void Validate(ConnectionInfo connectionInfo, ConnectionInfo.Force force)
        {
            if (string.IsNullOrWhiteSpace(connectionInfo.Hostname))
                throw new InvalidOperationException("A hostname is required for native RDP launch.");
            if (connectionInfo.Port is < 0 or > 65535)
                throw new InvalidOperationException($"The RDP port '{connectionInfo.Port}' is outside the valid range.");
            if (connectionInfo.UseRestrictedAdmin && connectionInfo.UseRCG)
                throw new InvalidOperationException("Restricted Admin and Remote Credential Guard cannot be enabled at the same time.");
            if (connectionInfo.UseRCG && HasConfiguredGateway(connectionInfo))
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
