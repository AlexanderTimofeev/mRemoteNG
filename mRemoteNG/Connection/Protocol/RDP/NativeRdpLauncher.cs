using System;
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
                Validate(connectionInfo);
                string executable = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
                if (!File.Exists(executable))
                    throw new FileNotFoundException("The native Windows Remote Desktop client was not found.", executable);

                bool prompt = force.HasFlag(ConnectionInfo.Force.NoCredentials) || connectionInfo.AlwaysPromptForCredentials;
                bool credentialless = prompt || connectionInfo.UseRestrictedAdmin || connectionInfo.UseRCG;
                if (!credentialless && !string.IsNullOrWhiteSpace(connectionInfo.Username) && !string.IsNullOrEmpty(connectionInfo.Password))
                {
                    string target = BuildCredentialTarget(connectionInfo.Hostname);
                    string username = RdpFileSerializer.BuildUsername(connectionInfo);
                    WindowsCredentialManager.Write(target, username, connectionInfo.Password);
                }

                rdpPath = _fileStore.Create(RdpFileSerializer.Serialize(connectionInfo));
                ProcessStartInfo startInfo = new(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.SystemDirectory
                };
                startInfo.ArgumentList.Add(rdpPath);
                if (connectionInfo.UseConsoleSession)
                    startInfo.ArgumentList.Add("/admin");
                if (connectionInfo.UseRestrictedAdmin)
                    startInfo.ArgumentList.Add("/restrictedAdmin");
                if (connectionInfo.UseRCG)
                    startInfo.ArgumentList.Add("/remoteGuard");
                if (prompt)
                    startInfo.ArgumentList.Add("/prompt");

                Process? process = Process.Start(startInfo);
                if (process == null)
                    throw new InvalidOperationException("mstsc.exe did not return a process instance.");

                _fileStore.ScheduleDelete(rdpPath);
                Runtime.MessageCollector.AddMessage(
                    MessageClass.InformationMsg,
                    string.Format(CultureInfo.InvariantCulture, "Opened native RDP connection '{0}' using mstsc.exe.", connectionInfo.Name));
                return true;
            }
            catch (Exception ex)
            {
                TemporaryRdpFileStore.TryDelete(rdpPath);
                Runtime.MessageCollector.AddExceptionMessage("Unable to open the native Windows RDP client.", ex);
                return false;
            }
        }

        public static string BuildCredentialTarget(string hostname)
        {
            string host = (hostname ?? string.Empty).Trim().TrimStart('[').TrimEnd(']');
            return $"TERMSRV/{host}";
        }

        private static void Validate(ConnectionInfo connectionInfo)
        {
            if (string.IsNullOrWhiteSpace(connectionInfo.Hostname))
                throw new InvalidOperationException("A hostname is required for native RDP launch.");
            if (connectionInfo.Port is < 0 or > 65535)
                throw new InvalidOperationException($"The RDP port '{connectionInfo.Port}' is outside the valid range.");
        }
    }
}
