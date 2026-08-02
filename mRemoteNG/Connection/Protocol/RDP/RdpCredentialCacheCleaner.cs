using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using mRemoteNG.App;
using mRemoteNG.Messages;

namespace mRemoteNG.Connection.Protocol.RDP
{
    /// <summary>
    /// Outcome of an attempt to remove cached RDP credentials from Windows Credential Manager.
    /// </summary>
    public enum ClearCachedCredentialsResult
    {
        Deleted,
        NotFound,
        Failed,
    }

    /// <summary>
    /// Removes both mstsc-managed domain-password credentials and mRemoteNG native-launch generic
    /// credentials for TERMSRV targets.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class RdpCredentialCacheCleaner
    {
        private const int ErrorNotFound = 1168;

        [DllImport("Advapi32.dll", SetLastError = true, EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, CredentialType type, int reservedFlag);

        private enum CredentialType : uint
        {
            Generic = 1,
            DomainPassword = 2,
        }

        /// <summary>
        /// Removes cached credentials for a destination hostname.
        /// </summary>
        public static ClearCachedCredentialsResult ClearCachedCredentials(string hostname)
        {
            if (string.IsNullOrWhiteSpace(hostname))
                return ClearCachedCredentialsResult.Failed;

            return ClearTargets([NativeRdpLauncher.BuildCredentialTarget(hostname)]);
        }

        /// <summary>
        /// Removes destination and separate RD Gateway credential targets for a connection.
        /// </summary>
        public static ClearCachedCredentialsResult ClearCachedCredentials(ConnectionInfo connectionInfo)
        {
            ArgumentNullException.ThrowIfNull(connectionInfo);
            if (string.IsNullOrWhiteSpace(connectionInfo.Hostname))
                return ClearCachedCredentialsResult.Failed;

            HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase)
            {
                NativeRdpLauncher.BuildCredentialTarget(connectionInfo.Hostname)
            };

            if (connectionInfo.RDGatewayUsageMethod != RDGatewayUsageMethod.Never &&
                !string.IsNullOrWhiteSpace(connectionInfo.RDGatewayHostname))
            {
                targets.Add(NativeRdpLauncher.BuildCredentialTarget(connectionInfo.RDGatewayHostname));
            }

            return ClearTargets(targets);
        }

        private static ClearCachedCredentialsResult ClearTargets(IEnumerable<string> targets)
        {
            bool deletedAny = false;
            bool failedAny = false;

            foreach (string target in targets)
            {
                foreach (CredentialType type in new[] { CredentialType.Generic, CredentialType.DomainPassword })
                {
                    ClearCachedCredentialsResult result = DeleteCredential(target, type);
                    deletedAny |= result == ClearCachedCredentialsResult.Deleted;
                    failedAny |= result == ClearCachedCredentialsResult.Failed;
                }
            }

            if (failedAny)
                return ClearCachedCredentialsResult.Failed;
            return deletedAny
                ? ClearCachedCredentialsResult.Deleted
                : ClearCachedCredentialsResult.NotFound;
        }

        private static ClearCachedCredentialsResult DeleteCredential(string target, CredentialType type)
        {
            try
            {
                if (CredDelete(target, type, 0))
                {
                    Runtime.MessageCollector.AddMessage(
                        MessageClass.InformationMsg,
                        $"Cleared cached RDP credential {target} ({type}).");
                    return ClearCachedCredentialsResult.Deleted;
                }

                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                    return ClearCachedCredentialsResult.NotFound;

                Runtime.MessageCollector.AddMessage(
                    MessageClass.WarningMsg,
                    $"CredDelete failed for {target} ({type}, Win32 error {error}).");
                return ClearCachedCredentialsResult.Failed;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(
                    $"Failed to clear cached RDP credential {target} ({type}).",
                    ex);
                return ClearCachedCredentialsResult.Failed;
            }
        }
    }
}
