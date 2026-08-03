using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using mRemoteNG.App;
using mRemoteNG.Messages;

namespace mRemoteNG.Connection.Protocol.RDP
{
    internal static class NativeRdpFileSigner
    {
        private const string SigningHashEnvironmentVariable = "MREMOTENG_RDP_SIGN_CERT_THUMBPRINT";
        private const string SigningHashFileName = "native-rdp-signing-thumbprint.txt";
        private const string DiagnosticsLogFileName = "native-rdp-signing.log";
        private const long MaximumDiagnosticsLogSize = 2 * 1024 * 1024;
        private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

        private static readonly object DiagnosticsLogLock = new();
        private static int _signingFailureNotificationShown;

        // RDP settings covered by the publisher signature. The display names must match
        // the names expected by mstsc in the signscope line.
        private static readonly IReadOnlyList<(string Prefix, string ScopeName)> SecureSettings =
        [
            ("full address:s:", "Full Address"),
            ("alternate full address:s:", "Alternate Full Address"),
            ("pcb:s:", "PCB"),
            ("use redirection server name:i:", "Use Redirection Server Name"),
            ("server port:i:", "Server Port"),
            ("negotiate security layer:i:", "Negotiate Security Layer"),
            ("enablecredsspsupport:i:", "EnableCredSspSupport"),
            ("disableconnectionsharing:i:", "DisableConnectionSharing"),
            ("autoreconnection enabled:i:", "AutoReconnection Enabled"),
            ("gatewayhostname:s:", "GatewayHostname"),
            ("gatewayusagemethod:i:", "GatewayUsageMethod"),
            ("gatewayprofileusagemethod:i:", "GatewayProfileUsageMethod"),
            ("gatewaycredentialssource:i:", "GatewayCredentialsSource"),
            ("support url:s:", "Support URL"),
            ("promptcredentialonce:i:", "PromptCredentialOnce"),
            ("require pre-authentication:i:", "Require pre-authentication"),
            ("pre-authentication server address:s:", "Pre-authentication server address"),
            ("alternate shell:s:", "Alternate Shell"),
            ("shell working directory:s:", "Shell Working Directory"),
            ("remoteapplicationprogram:s:", "RemoteApplicationProgram"),
            ("remoteapplicationexpandworkingdir:s:", "RemoteApplicationExpandWorkingdir"),
            ("remoteapplicationmode:i:", "RemoteApplicationMode"),
            ("remoteapplicationguid:s:", "RemoteApplicationGuid"),
            ("remoteapplicationname:s:", "RemoteApplicationName"),
            ("remoteapplicationicon:s:", "RemoteApplicationIcon"),
            ("remoteapplicationfile:s:", "RemoteApplicationFile"),
            ("remoteapplicationfileextensions:s:", "RemoteApplicationFileExtensions"),
            ("remoteapplicationcmdline:s:", "RemoteApplicationCmdLine"),
            ("remoteapplicationexpandcmdline:s:", "RemoteApplicationExpandCmdLine"),
            ("prompt for credentials:i:", "Prompt For Credentials"),
            ("authentication level:i:", "Authentication Level"),
            ("audiomode:i:", "AudioMode"),
            ("redirectdrives:i:", "RedirectDrives"),
            ("redirectprinters:i:", "RedirectPrinters"),
            ("redirectcomports:i:", "RedirectCOMPorts"),
            ("redirectsmartcards:i:", "RedirectSmartCards"),
            ("redirectposdevices:i:", "RedirectPOSDevices"),
            ("redirectclipboard:i:", "RedirectClipboard"),
            ("devicestoredirect:s:", "DevicesToRedirect"),
            ("drivestoredirect:s:", "DrivesToRedirect"),
            ("loadbalanceinfo:s:", "LoadBalanceInfo"),
            ("redirectdirectx:i:", "RedirectDirectX"),
            ("rdgiskdcproxy:i:", "RDGIsKDCProxy"),
            ("kdcproxyname:s:", "KDCProxyName"),
            ("eventloguploadaddress:s:", "EventLogUploadAddress"),
            ("enablerdsaadauth:i:", "EnableRdsAadAuth"),
            ("redirectwebauthn:i:", "RedirectWebAuthn")
        ];

        internal static bool TrySignIfConfigured(string rdpPath)
        {
            SigningConfiguration configuration = ResolveSigningConfiguration();
            if (string.IsNullOrEmpty(configuration.EffectiveHash))
                return false;

            string operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
            WriteDiagnostic(operationId, "BEGIN native RDP managed signing attempt");
            WriteDiagnostic(operationId, DescribeEnvironment());
            WriteDiagnostic(operationId, configuration.Describe());
            WriteDiagnostic(operationId, DescribeRdpFile(rdpPath));

            try
            {
                SignRdpFile(operationId, rdpPath, configuration.EffectiveHash);
                WriteDiagnostic(operationId, DescribeRdpFile(rdpPath));
                WriteDiagnostic(operationId, "SUCCESS: temporary RDP file was signed in-process.");
                WriteDiagnostic(operationId, "END native RDP managed signing attempt");
                return true;
            }
            catch (Exception exception)
            {
                WriteDiagnostic(operationId, "FAILURE: " + exception);
                WriteDiagnostic(operationId, "END native RDP managed signing attempt");
                ReportSigningFailure(exception, operationId);
                return false;
            }
        }

        internal static string GetSigningHashFilePath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "mRemoteNG",
            SigningHashFileName);

        internal static string GetDiagnosticsLogPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "mRemoteNG",
            "Logs",
            DiagnosticsLogFileName);

        internal static string NormalizeHash(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return new string(value
                .Where(Uri.IsHexDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        }

        internal static string SignContent(string content, X509Certificate2 certificate)
        {
            ArgumentNullException.ThrowIfNull(content);
            ArgumentNullException.ThrowIfNull(certificate);
            if (!certificate.HasPrivateKey)
                throw new InvalidOperationException("The RDP signing certificate has no private key.");

            List<string> settings = ParseSettings(content);
            RemoveExistingSignature(settings);
            EnsureAlternateFullAddress(settings);

            List<string> signedLines = [];
            List<string> scopeNames = [];
            foreach ((string prefix, string scopeName) in SecureSettings)
            {
                foreach (string setting in settings)
                {
                    if (!setting.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    signedLines.Add(setting);
                    scopeNames.Add(scopeName);
                }
            }

            if (signedLines.Count == 0)
                throw new InvalidOperationException("The generated RDP file contains no settings eligible for signing.");

            string signScopeLine = "signscope:s:" + string.Join(',', scopeNames);
            string signedMessage =
                string.Join("\r\n", signedLines) + "\r\n" + signScopeLine + "\r\n\0";

            ContentInfo contentInfo = new(Encoding.Unicode.GetBytes(signedMessage));
            SignedCms cms = new(contentInfo, detached: true);
            CmsSigner signer = new(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
            {
                IncludeOption = X509IncludeOption.WholeChain,
                DigestAlgorithm = new Oid(Sha256Oid)
            };
            cms.ComputeSignature(signer, silent: true);

            byte[] pkcs7 = cms.Encode();
            byte[] signatureBlob = BuildRdpSignatureBlob(pkcs7);
            string signatureValue = FormatBase64(Convert.ToBase64String(signatureBlob));

            return string.Join("\r\n", settings) + "\r\n" +
                   signScopeLine + "\r\n" +
                   "signature:s:" + signatureValue + "\r\n";
        }

        private static void SignRdpFile(
            string operationId,
            string rdpPath,
            string certificateSha256Hash)
        {
            if (certificateSha256Hash.Length != 64)
            {
                throw new InvalidOperationException(
                    $"The configured RDP signing certificate SHA-256 hash has {certificateSha256Hash.Length} hexadecimal characters; expected 64. " +
                    "Use certificate.GetCertHashString(HashAlgorithmName.SHA256), not certificate.Thumbprint.");
            }

            using X509Certificate2 certificate = FindSigningCertificate(operationId, certificateSha256Hash);
            WriteDiagnostic(operationId, DescribeCertificate(certificate));

            string originalContent = File.ReadAllText(rdpPath, Encoding.Unicode);
            string signedContent = SignContent(originalContent, certificate);

            string temporaryPath = rdpPath + ".signed.tmp";
            try
            {
                File.WriteAllText(temporaryPath, signedContent, Encoding.Unicode);
                File.Move(temporaryPath, rdpPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            WriteDiagnostic(
                operationId,
                $"Managed CMS signature created. Signed settings={CountScopeEntries(signedContent)}, output length={new FileInfo(rdpPath).Length} bytes.");
        }

        private static X509Certificate2 FindSigningCertificate(
            string operationId,
            string certificateSha256Hash)
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
                    WriteDiagnostic(
                        operationId,
                        $"Opened certificate store {storeLocation}\\My; certificate count={store.Certificates.Count}.");
                }
                catch (Exception exception)
                {
                    WriteDiagnostic(
                        operationId,
                        $"Unable to open certificate store {storeLocation}\\My: {exception}");
                    continue;
                }

                foreach (X509Certificate2 candidate in store.Certificates)
                {
                    string candidateSha256Hash = NormalizeHash(
                        candidate.GetCertHashString(HashAlgorithmName.SHA256));
                    if (!string.Equals(
                            candidateSha256Hash,
                            certificateSha256Hash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    certificateFound = true;
                    WriteDiagnostic(
                        operationId,
                        $"Matched certificate in {storeLocation}\\My: Subject='{candidate.Subject}', " +
                        $"SHA1={NormalizeHash(candidate.Thumbprint)}, SHA256={candidateSha256Hash}, " +
                        $"HasPrivateKey={candidate.HasPrivateKey}, NotBefore={candidate.NotBefore:O}, NotAfter={candidate.NotAfter:O}.");

                    if (!candidate.HasPrivateKey)
                        continue;

                    certificateWithPrivateKeyFound = true;
                    DateTime now = DateTime.Now;
                    if (now < candidate.NotBefore || now > candidate.NotAfter)
                        continue;

                    certificateIsCurrentlyValid = true;
                    return new X509Certificate2(candidate);
                }
            }

            if (!certificateFound)
            {
                throw new InvalidOperationException(
                    "No certificate matching the configured RDP signing SHA-256 hash was found in CurrentUser\\My or LocalMachine\\My.");
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

            throw new InvalidOperationException("The configured RDP signing certificate could not be used.");
        }

        private static List<string> ParseSettings(string content)
        {
            string normalized = content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            List<string> lines = normalized.Split('\n').ToList();
            while (lines.Count > 0 && string.IsNullOrEmpty(lines[^1]))
                lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        private static void RemoveExistingSignature(List<string> settings)
        {
            settings.RemoveAll(line =>
                line.StartsWith("signscope:s:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("signature:s:", StringComparison.OrdinalIgnoreCase));
        }

        private static void EnsureAlternateFullAddress(List<string> settings)
        {
            string? fullAddress = settings.FirstOrDefault(line =>
                line.StartsWith("full address:s:", StringComparison.OrdinalIgnoreCase));
            bool hasAlternateAddress = settings.Any(line =>
                line.StartsWith("alternate full address:s:", StringComparison.OrdinalIgnoreCase));

            if (fullAddress is null || hasAlternateAddress)
                return;

            string value = fullAddress["full address:s:".Length..];
            if (!string.IsNullOrEmpty(value))
                settings.Add("alternate full address:s:" + value);
        }

        private static byte[] BuildRdpSignatureBlob(byte[] pkcs7)
        {
            byte[] result = new byte[pkcs7.Length + 12];
            result[0] = 1;
            result[2] = 1;
            result[4] = 1;
            Array.Copy(BitConverter.GetBytes((uint)pkcs7.Length), 0, result, 8, 4);
            Array.Copy(pkcs7, 0, result, 12, pkcs7.Length);
            return result;
        }

        private static string FormatBase64(string value)
        {
            StringBuilder result = new(value.Length + value.Length / 64 + 1);
            for (int offset = 0; offset < value.Length; offset += 64)
            {
                int length = Math.Min(64, value.Length - offset);
                result.Append(value, offset, length).Append(' ');
            }

            return result.ToString();
        }

        private static int CountScopeEntries(string signedContent)
        {
            string? signScope = ParseSettings(signedContent).FirstOrDefault(line =>
                line.StartsWith("signscope:s:", StringComparison.OrdinalIgnoreCase));
            if (signScope is null)
                return 0;

            string value = signScope["signscope:s:".Length..];
            return string.IsNullOrEmpty(value) ? 0 : value.Split(',').Length;
        }

        private static SigningConfiguration ResolveSigningConfiguration()
        {
            string processValue = ReadEnvironmentVariable(EnvironmentVariableTarget.Process);
            string userValue = ReadEnvironmentVariable(EnvironmentVariableTarget.User);
            string machineValue = ReadEnvironmentVariable(EnvironmentVariableTarget.Machine);
            string filePath = GetSigningHashFilePath();
            string fileValue = ReadTextFile(filePath);

            string effectiveValue;
            string source;
            if (!string.IsNullOrWhiteSpace(processValue))
            {
                effectiveValue = processValue;
                source = "process environment";
            }
            else if (!string.IsNullOrWhiteSpace(fileValue))
            {
                effectiveValue = fileValue;
                source = "configuration file";
            }
            else
            {
                effectiveValue = string.Empty;
                source = "not configured";
            }

            return new SigningConfiguration(
                NormalizeHash(effectiveValue),
                source,
                NormalizeHash(processValue),
                NormalizeHash(userValue),
                NormalizeHash(machineValue),
                NormalizeHash(fileValue),
                filePath);
        }

        private static string ReadEnvironmentVariable(EnvironmentVariableTarget target)
        {
            try
            {
                return Environment.GetEnvironmentVariable(SigningHashEnvironmentVariable, target) ?? string.Empty;
            }
            catch (Exception exception)
            {
                WriteDiagnostic("config", $"Unable to read {target} environment variable: {exception}");
                return string.Empty;
            }
        }

        private static string ReadTextFile(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (Exception exception)
            {
                WriteDiagnostic("config", $"Unable to read signing hash file '{path}': {exception}");
                return string.Empty;
            }
        }

        private static void ReportSigningFailure(Exception exception, string operationId)
        {
            string logMessage =
                "Unable to sign the temporary RDP file in-process. mstsc will continue with an unsigned file. " +
                $"Signing diagnostics operation={operationId}; file={GetDiagnosticsLogPath()}";
            Runtime.MessageCollector.AddExceptionStackTrace(logMessage, exception);

            if (Interlocked.Exchange(ref _signingFailureNotificationShown, 1) != 0)
                return;

            string notification =
                "The temporary RDP file could not be signed.\r\n\r\n" +
                "mstsc will continue with an unsigned file, so Windows may display its security confirmation.\r\n\r\n" +
                "Reason: " + GetUserFriendlySigningFailure(exception) + "\r\n\r\n" +
                "Detailed signing log:\r\n" + GetDiagnosticsLogPath() + "\r\n\r\n" +
                "Operation: " + operationId;

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

        private static string DescribeEnvironment()
        {
            string identity;
            try
            {
                identity = WindowsIdentity.GetCurrent().Name;
            }
            catch
            {
                identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
            }

            return string.Join(
                Environment.NewLine,
                "Environment:",
                $"  Local time: {DateTimeOffset.Now:O}",
                $"  UTC time: {DateTimeOffset.UtcNow:O}",
                $"  Process: {Environment.ProcessPath}",
                $"  PID: {Environment.ProcessId}",
                $"  Identity: {identity}",
                $"  OS: {RuntimeInformation.OSDescription}",
                $"  OS architecture: {RuntimeInformation.OSArchitecture}",
                $"  Process architecture: {RuntimeInformation.ProcessArchitecture}",
                $"  Current directory: {Environment.CurrentDirectory}",
                $"  System directory: {Environment.SystemDirectory}");
        }

        private static string DescribeRdpFile(string path)
        {
            try
            {
                FileInfo file = new(path);
                string sha256 = File.Exists(path)
                    ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                    : "<file does not exist>";

                return string.Join(
                    Environment.NewLine,
                    "RDP file:",
                    $"  Path: {path}",
                    $"  Exists: {file.Exists}",
                    $"  Length: {(file.Exists ? file.Length : -1)}",
                    $"  Last write UTC: {(file.Exists ? file.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture) : "n/a")}",
                    $"  SHA-256: {sha256}");
            }
            catch (Exception exception)
            {
                return $"Unable to inspect RDP file '{path}': {exception}";
            }
        }

        private static string DescribeCertificate(X509Certificate2 certificate) => string.Join(
            Environment.NewLine,
            "Selected certificate:",
            $"  Subject: {certificate.Subject}",
            $"  SHA-1 thumbprint: {NormalizeHash(certificate.Thumbprint)}",
            $"  SHA-256 hash: {NormalizeHash(certificate.GetCertHashString(HashAlgorithmName.SHA256))}",
            $"  Has private key: {certificate.HasPrivateKey}",
            $"  Signature algorithm: {certificate.SignatureAlgorithm.FriendlyName}",
            $"  Not before: {certificate.NotBefore:O}",
            $"  Not after: {certificate.NotAfter:O}");

        private static void WriteDiagnostic(string operationId, string message)
        {
            try
            {
                lock (DiagnosticsLogLock)
                {
                    string path = GetDiagnosticsLogPath();
                    string? directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    if (File.Exists(path) && new FileInfo(path).Length > MaximumDiagnosticsLogSize)
                    {
                        string previousPath = path + ".previous";
                        File.Delete(previousPath);
                        File.Move(path, previousPath);
                    }

                    string entry =
                        $"[{DateTimeOffset.Now:O}] [{operationId}] {message}{Environment.NewLine}";
                    File.AppendAllText(
                        path,
                        entry,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
            catch
            {
                // Diagnostics must never block an RDP launch.
            }
        }

        private readonly record struct SigningConfiguration(
            string EffectiveHash,
            string EffectiveSource,
            string ProcessValue,
            string UserValue,
            string MachineValue,
            string FileValue,
            string FilePath)
        {
            internal string Describe() => string.Join(
                Environment.NewLine,
                "Signing configuration:",
                $"  Effective source: {EffectiveSource}",
                $"  Effective hash: {EffectiveHash}",
                $"  Effective length: {EffectiveHash.Length}",
                $"  Process environment: {ProcessValue} (length {ProcessValue.Length})",
                $"  User environment: {UserValue} (length {UserValue.Length})",
                $"  Machine environment: {MachineValue} (length {MachineValue.Length})",
                $"  File: {FilePath}",
                $"  File value: {FileValue} (length {FileValue.Length})");
        }
    }
}
