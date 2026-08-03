using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        private const uint CryptENotFound = 0x80092004u;
        private const long MaximumDiagnosticsLogSize = 2 * 1024 * 1024;

        private static readonly TimeSpan SigningTimeout = TimeSpan.FromSeconds(30);
        private static readonly object DiagnosticsLogLock = new();
        private static int _signingFailureNotificationShown;

        internal static bool TrySignIfConfigured(string rdpPath)
        {
            SigningConfiguration configuration = ResolveSigningConfiguration();
            if (string.IsNullOrEmpty(configuration.EffectiveHash))
                return false;

            string operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
            WriteDiagnostic(operationId, "BEGIN native RDP signing attempt");
            WriteDiagnostic(operationId, DescribeEnvironment());
            WriteDiagnostic(operationId, configuration.Describe());
            WriteDiagnostic(operationId, DescribeRdpFile(rdpPath));

            try
            {
                SignRdpFile(operationId, rdpPath, configuration.EffectiveHash);
                WriteDiagnostic(operationId, "SUCCESS: temporary RDP file was signed.");
                WriteDiagnostic(operationId, "END native RDP signing attempt");
                return true;
            }
            catch (Exception exception)
            {
                WriteDiagnostic(operationId, "FAILURE: " + exception);
                WriteDiagnostic(operationId, "END native RDP signing attempt");
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

        internal static bool IsCertificateLookupFailure(int exitCode) =>
            unchecked((uint)exitCode) == CryptENotFound;

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

        private static void SignRdpFile(string operationId, string rdpPath, string certificateSha256Hash)
        {
            if (certificateSha256Hash.Length != 64)
            {
                throw new InvalidOperationException(
                    $"The configured RDP signing certificate SHA-256 hash has {certificateSha256Hash.Length} hexadecimal characters; expected 64. " +
                    "Use certificate.GetCertHashString(HashAlgorithmName.SHA256), not certificate.Thumbprint.");
            }

            SigningCertificate certificate = FindSigningCertificate(operationId, certificateSha256Hash);
            string signerExecutable = Path.Combine(Environment.SystemDirectory, "rdpsign.exe");
            if (!File.Exists(signerExecutable))
                throw new FileNotFoundException("rdpsign.exe was not found.", signerExecutable);

            WriteDiagnostic(operationId, certificate.Describe());
            WriteDiagnostic(operationId, DescribeExecutable(signerExecutable));

            RdpsignResult primaryResult = RunRdpsign(
                operationId,
                "configured SHA-256 hash",
                signerExecutable,
                certificateSha256Hash,
                rdpPath);
            if (primaryResult.ExitCode == 0)
                return;

            WriteDiagnostic(
                operationId,
                $"Primary attempt failed. IsCertificateLookupFailure={IsCertificateLookupFailure(primaryResult.ExitCode)}.");

            if (IsCertificateLookupFailure(primaryResult.ExitCode) &&
                !string.IsNullOrWhiteSpace(certificate.Sha1Thumbprint))
            {
                WriteDiagnostic(operationId, "Starting compatibility attempt with the certificate SHA-1 thumbprint.");
                RdpsignResult compatibilityResult = RunRdpsign(
                    operationId,
                    "SHA-1 thumbprint compatibility value",
                    signerExecutable,
                    certificate.Sha1Thumbprint,
                    rdpPath);

                if (compatibilityResult.ExitCode == 0)
                {
                    Runtime.MessageCollector.AddMessage(
                        MessageClass.WarningMsg,
                        "rdpsign.exe succeeded using the certificate SHA-1 thumbprint compatibility fallback. " +
                        $"Diagnostics: {GetDiagnosticsLogPath()}");
                    return;
                }

                throw CreateRdpsignFailure(
                    primaryResult,
                    compatibilityResult,
                    certificateSha256Hash,
                    certificate.Sha1Thumbprint,
                    rdpPath);
            }

            throw CreateRdpsignFailure(
                primaryResult,
                null,
                certificateSha256Hash,
                certificate.Sha1Thumbprint,
                rdpPath);
        }

        private static RdpsignResult RunRdpsign(
            string operationId,
            string attemptName,
            string signerExecutable,
            string certificateHash,
            string rdpPath)
        {
            Encoding outputEncoding = GetRdpsignOutputEncoding();
            string arguments = $"/sha256 {certificateHash} /v \"{rdpPath}\"";

            WriteDiagnostic(operationId, $"Attempt: {attemptName}");
            WriteDiagnostic(operationId, $"Command: \"{signerExecutable}\" {arguments}");
            WriteDiagnostic(operationId, $"Output encoding: {outputEncoding.EncodingName} (code page {outputEncoding.CodePage})");

            ProcessStartInfo startInfo = new(signerExecutable)
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = outputEncoding,
                StandardErrorEncoding = outputEncoding,
                WorkingDirectory = Environment.SystemDirectory
            };

            Stopwatch stopwatch = Stopwatch.StartNew();
            using Process signer = Process.Start(startInfo)
                ?? throw new InvalidOperationException("rdpsign.exe did not return a process instance.");

            WriteDiagnostic(operationId, $"rdpsign process started. PID={signer.Id}.");

            string standardOutput = signer.StandardOutput.ReadToEnd();
            string standardError = signer.StandardError.ReadToEnd();
            if (!signer.WaitForExit((int)SigningTimeout.TotalMilliseconds))
            {
                try
                {
                    signer.Kill();
                }
                catch (Exception killException)
                {
                    WriteDiagnostic(operationId, "Unable to terminate timed-out rdpsign process: " + killException);
                }

                throw new TimeoutException("Signing the temporary RDP file timed out after 30 seconds.");
            }

            stopwatch.Stop();
            string output = string.Join(
                Environment.NewLine,
                new[] { standardError, standardOutput }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()));

            WriteDiagnostic(
                operationId,
                $"rdpsign exited after {stopwatch.ElapsedMilliseconds} ms with {FormatExitCode(signer.ExitCode)}.");
            WriteDiagnostic(
                operationId,
                string.IsNullOrWhiteSpace(output)
                    ? "rdpsign produced no stdout/stderr output."
                    : "rdpsign stdout/stderr:" + Environment.NewLine + output);

            return new RdpsignResult(signer.ExitCode, output);
        }

        private static SigningCertificate FindSigningCertificate(
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
                    WriteDiagnostic(operationId, $"Opened certificate store {storeLocation}\\My; certificate count={store.Certificates.Count}.");
                }
                catch (Exception exception)
                {
                    WriteDiagnostic(operationId, $"Unable to open certificate store {storeLocation}\\My: {exception}");
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
                    string sha1Thumbprint = NormalizeHash(candidate.Thumbprint);
                    WriteDiagnostic(
                        operationId,
                        $"Matched certificate in {storeLocation}\\My: Subject='{candidate.Subject}', " +
                        $"SHA1={sha1Thumbprint}, SHA256={candidateSha256Hash}, " +
                        $"HasPrivateKey={candidate.HasPrivateKey}, NotBefore={candidate.NotBefore:O}, NotAfter={candidate.NotAfter:O}.");

                    if (!candidate.HasPrivateKey)
                        continue;

                    certificateWithPrivateKeyFound = true;
                    DateTime now = DateTime.Now;
                    if (now < candidate.NotBefore || now > candidate.NotAfter)
                        continue;

                    certificateIsCurrentlyValid = true;
                    return new SigningCertificate(
                        storeLocation,
                        candidate.Subject,
                        sha1Thumbprint,
                        candidateSha256Hash,
                        candidate.NotBefore,
                        candidate.NotAfter,
                        candidate.HasPrivateKey);
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

        private static InvalidOperationException CreateRdpsignFailure(
            RdpsignResult primaryResult,
            RdpsignResult? compatibilityResult,
            string certificateSha256Hash,
            string certificateSha1Thumbprint,
            string rdpPath)
        {
            StringBuilder details = new();
            if (compatibilityResult.HasValue)
            {
                details.Append("Both rdpsign attempts failed. Primary exit code ")
                    .Append(FormatExitCode(primaryResult.ExitCode))
                    .Append("; compatibility exit code ")
                    .Append(FormatExitCode(compatibilityResult.Value.ExitCode))
                    .AppendLine(".");
            }
            else
            {
                details.Append("rdpsign.exe failed with exit code ")
                    .Append(FormatExitCode(primaryResult.ExitCode))
                    .AppendLine(".");
            }

            details.Append("SHA-256 hash: ")
                .AppendLine(certificateSha256Hash)
                .Append("SHA-1 thumbprint: ")
                .AppendLine(certificateSha1Thumbprint)
                .Append("RDP file: ")
                .AppendLine(rdpPath)
                .Append("Diagnostics log: ")
                .AppendLine(GetDiagnosticsLogPath());

            return new InvalidOperationException(details.ToString().TrimEnd());
        }

        private static void ReportSigningFailure(Exception exception, string operationId)
        {
            string logMessage =
                "Unable to sign the temporary RDP file. mstsc will continue with an unsigned file. " +
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
                $"  64-bit OS/process: {Environment.Is64BitOperatingSystem}/{Environment.Is64BitProcess}",
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

        private static string DescribeExecutable(string path)
        {
            try
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
                return string.Join(
                    Environment.NewLine,
                    "rdpsign executable:",
                    $"  Path: {path}",
                    $"  File version: {version.FileVersion}",
                    $"  Product version: {version.ProductVersion}",
                    $"  File length: {new FileInfo(path).Length}");
            }
            catch (Exception exception)
            {
                return $"Unable to inspect rdpsign executable '{path}': {exception}";
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

        private static string FormatExitCode(int exitCode) =>
            $"0x{unchecked((uint)exitCode):X8} ({exitCode})";

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
                    File.AppendAllText(path, entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
            catch
            {
                // Diagnostics must never block an RDP launch.
            }
        }

        private readonly record struct RdpsignResult(int ExitCode, string Output);

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

        private readonly record struct SigningCertificate(
            StoreLocation StoreLocation,
            string Subject,
            string Sha1Thumbprint,
            string Sha256Hash,
            DateTime NotBefore,
            DateTime NotAfter,
            bool HasPrivateKey)
        {
            internal string Describe() => string.Join(
                Environment.NewLine,
                "Selected certificate:",
                $"  Store: {StoreLocation}\\My",
                $"  Subject: {Subject}",
                $"  SHA-1 thumbprint: {Sha1Thumbprint}",
                $"  SHA-256 hash: {Sha256Hash}",
                $"  Has private key: {HasPrivateKey}",
                $"  Not before: {NotBefore:O}",
                $"  Not after: {NotAfter:O}");
        }
    }
}
