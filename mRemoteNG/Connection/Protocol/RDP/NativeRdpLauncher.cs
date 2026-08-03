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
        private const int CryptENotFound = unchecked((int)0x80092004);
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
            string certificateSha256Hash = ResolveSigningThumbprint();
            if (string.IsNullOrEmpty(certificateSha256Hash))
                return false;

            try
            {
                SignRdpFile(rdpPath, certificateSha256Hash);
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

            Runtime.MessageCollector.AddExceptionStackTrace(logMessage, exception);

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

        private static void SignRdpFile(string rdpPath, string certificateSha256Hash)
        {
            if (certificateSha256Hash.Length != 64)
            {
                throw new InvalidOperationException(
                    $"The configured RDP signing certificate SHA-256 hash has {certificateSha256Hash.Length} hexadecimal characters; expected 64. " +
                    "Use certificate.GetCertHashString(HashAlgorithmName.SHA256), not certificate.Thumbprint.");
            }

            string certificateSha1Thumbprint = ValidateSigningCertificate(certificateSha256Hash);
            string signerExecutable = Path.Combine(Environment.SystemDirectory, "rdpsign.exe");
            if (!File.Exists(signerExecutable))
                throw new FileNotFoundException("rdpsign.exe was not found, so the temporary RDP file could not be signed.", signerExecutable);

            RdpsignResult primaryResult = RunRdpsign(
                signerExecutable,
                certificateSha256Hash,
                rdpPath);
            if (primaryResult.ExitCode == 0)
                return;

            if (primaryResult.ExitCode == CryptENotFound &&
                !string.IsNullOrWhiteSpace(certificateSha1Thumbprint))
            {
                RdpsignResult compatibilityResult = RunRdpsign(
                    signerExecutable,
                    certificateSha1Thumbprint,
                    rdpPath);
                if (compatibilityResult.ExitCode == 0)
                {
                    Runtime.MessageCollector.AddMessage(
                        MessageClass.WarningMsg,
                        "rdpsign.exe could not locate the certificate by its SHA-256 hash and succeeded using the certificate SHA-1 thumbprint compatibility fallback.");
                    return;
                }

                throw CreateRdpsignFailure(
                    primaryResult,
                    compatibilityResult,
                    certificateSha256Hash,
                    certificateSha1Thumbprint,
                    rdpPath);
            }

            throw CreateRdpsignFailure(
                primaryResult,
                null,
                certificateSha256Hash,
                certificateSha1Thumbprint,
                rdpPath);
        }

        private static RdpsignResult RunRdpsign(
            string signerExecutable,
            string certificateHash,
            string rdpPath)
        {
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
            signStartInfo.ArgumentList.Add("/sha256");
            signStartInfo.ArgumentList.Add(certificateHash);
            signStartInfo.ArgumentList.Add("/v");
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
                }

                throw new TimeoutException("Signing the temporary RDP file timed out after 30 seconds.");
            }

            string output = string.Join(
                Environment.NewLine,
                new[] { standardError, standardOutput }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()));

            return new RdpsignResult(signer.ExitCode, output);
        }

        private static InvalidOperationException CreateRdpsignFailure(
            RdpsignResult primaryResult,
            RdpsignResult? compatibilityResult,
            string certificateSha256Hash,
            string certificateSha1Thumbprint,
            string rdpPath)
        {
            StringBuilder details = new();
            details.Append("rdpsign.exe failed with exit code ")
                .Append(FormatExitCode(primaryResult.ExitCode))
                .Append(" while using the configured SHA-256 certificate hash.")
                .AppendLine()
                .Append("SHA-256 hash: ")
                .AppendLine(certificateSha256Hash)
                .Append("RDP file: ")
                .AppendLine(rdpPath);

            if (!string.IsNullOrWhiteSpace(primaryResult.Output))
            {
                details.AppendLine("Primary rdpsign output:")
                    .AppendLine(primaryResult.Output);
            }

            if (compatibilityResult.HasValue)
            {
                details.Append("Compatibility fallback using SHA-1 thumbprint ")
                    .Append(certificateSha1Thumbprint)
                    .Append(" failed with exit code ")
                    .Append(FormatExitCode(compatibilityResult.Value.ExitCode))
                    .AppendLine(".");

                if (!string.IsNullOrWhiteSpace(compatibilityResult.Value.Output))
                {
                    details.AppendLine("Compatibility rdpsign output:")
                        .AppendLine(compatibilityResult.Value.Output);
                }
            }

            return new InvalidOperationException(details.ToString().TrimEnd());
        }

        private static string FormatExitCode(int exitCode) =>
            $"0x{unchecked((uint)exitCode):X8} ({exitCode})";

        private static string ValidateSigningCertificate(string certificateSha256Hash)
        {
            bool certificateFound = false;
            bool certificateWithPrivateKeyFound = false;
            bool certificateIsCurrentlyValid = false;
            string certificateSha1Thumbprint = string.Empty;

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
                    string storedCertificateSha256Hash = NormalizeThumbprint(
                        certificate.GetCertHashString(HashAlgorithmName.SHA256));

                    if (!string.Equals(
                            storedCertificateSha256Hash,
                            certificateSha256Hash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    certificateFound = true;
                    certificateSha1Thumbprint = NormalizeThumbprint(certificate.Thumbprint);
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
                    "No certificate matching the configured RDP signing SHA-256 hash was found in CurrentUser\\My or LocalMachine\\My. " +
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

            return certificateSha1Thumbprint;
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

        private readonly record struct RdpsignResult(int ExitCode, string Output);
    }
}
