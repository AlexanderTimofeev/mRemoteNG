using System;
using mRemoteNG.App;
using mRemoteNG.Properties;
using mRemoteNG.Security.SymmetricEncryption;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public readonly record struct RdpResolvedCredentials(string Username, string Password, string Domain)
    {
        public static RdpResolvedCredentials Empty => new(string.Empty, string.Empty, string.Empty);
        public bool HasPassword => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);
    }

    /// <summary>
    /// Resolves the same credential sources used by embedded RDP without requiring an ActiveX control.
    /// Passwords remain in memory only long enough to write the Windows Credential Manager entries.
    /// </summary>
    public static class RdpCredentialResolver
    {
        public static RdpResolvedCredentials ResolveDestination(
            ConnectionInfo connectionInfo,
            ConnectionInfo.Force force)
        {
            ArgumentNullException.ThrowIfNull(connectionInfo);
            if (force.HasFlag(ConnectionInfo.Force.NoCredentials))
                return RdpResolvedCredentials.Empty;

            string username = connectionInfo.Username ?? string.Empty;
            string password = connectionInfo.Password ?? string.Empty;
            string domain = connectionInfo.Domain ?? string.Empty;

            ResolveExternalProvider(
                connectionInfo.ExternalCredentialProvider,
                connectionInfo.UserViaAPI,
                connectionInfo,
                connectionInfo.Hostname,
                connectionInfo.Username,
                ref username,
                ref password,
                ref domain);

            if (string.IsNullOrEmpty(domain))
            {
                (string parsedUsername, string parsedDomain) = RdpProtocol.ParseDomainFromUsername(username);
                username = parsedUsername;
                domain = parsedDomain;
            }

            ApplyEmptyCredentialDefaults(connectionInfo, ref username, ref password, ref domain);
            return new RdpResolvedCredentials(username, password, domain);
        }

        public static RdpResolvedCredentials ResolveGateway(
            ConnectionInfo connectionInfo,
            RdpResolvedCredentials destinationCredentials,
            ConnectionInfo.Force force)
        {
            ArgumentNullException.ThrowIfNull(connectionInfo);
            if (force.HasFlag(ConnectionInfo.Force.NoCredentials) ||
                connectionInfo.RDGatewayUsageMethod == RDGatewayUsageMethod.Never ||
                string.IsNullOrWhiteSpace(connectionInfo.RDGatewayHostname))
            {
                return RdpResolvedCredentials.Empty;
            }

            switch (connectionInfo.RDGatewayUseConnectionCredentials)
            {
                case RDGatewayUseConnectionCredentials.Yes:
                    return destinationCredentials;
                case RDGatewayUseConnectionCredentials.SmartCard:
                case RDGatewayUseConnectionCredentials.AccessToken:
                    return RdpResolvedCredentials.Empty;
            }

            string username = connectionInfo.RDGatewayUsername ?? string.Empty;
            string password = connectionInfo.RDGatewayPassword ?? string.Empty;
            string domain = connectionInfo.RDGatewayDomain ?? string.Empty;

            ResolveExternalProvider(
                connectionInfo.RDGatewayExternalCredentialProvider,
                connectionInfo.RDGatewayUserViaAPI,
                connectionInfo,
                connectionInfo.RDGatewayHostname,
                connectionInfo.RDGatewayUsername,
                ref username,
                ref password,
                ref domain);

            if (string.IsNullOrEmpty(domain))
            {
                (string parsedUsername, string parsedDomain) = RdpProtocol.ParseDomainFromUsername(username);
                username = parsedUsername;
                domain = parsedDomain;
            }

            return new RdpResolvedCredentials(username, password, domain);
        }

        private static void ApplyEmptyCredentialDefaults(
            ConnectionInfo connectionInfo,
            ref string username,
            ref string password,
            ref string domain)
        {
            if (!string.IsNullOrEmpty(username))
                return;

            switch (OptionsCredentialsPage.Default.EmptyCredentials)
            {
                case "windows":
                    username = Environment.UserName;
                    if (string.IsNullOrEmpty(domain))
                        domain = Environment.UserDomainName;
                    return;
                case "custom":
                    username = OptionsCredentialsPage.Default.DefaultUsername ?? string.Empty;
                    if (string.IsNullOrEmpty(username))
                    {
                        ResolveDefaultExternalProvider(
                            connectionInfo,
                            ref username,
                            ref password,
                            ref domain);
                    }

                    if (string.IsNullOrEmpty(domain))
                        domain = OptionsCredentialsPage.Default.DefaultDomain ?? string.Empty;

                    if (string.IsNullOrEmpty(password) &&
                        !string.IsNullOrEmpty(OptionsCredentialsPage.Default.DefaultPassword))
                    {
                        LegacyRijndaelCryptographyProvider cryptographyProvider = new();
                        password = cryptographyProvider.Decrypt(
                            OptionsCredentialsPage.Default.DefaultPassword,
                            Runtime.EncryptionKey);
                    }

                    return;
            }
        }

        private static void ResolveDefaultExternalProvider(
            ConnectionInfo connectionInfo,
            ref string username,
            ref string password,
            ref string domain)
        {
            ExternalCredentialProvider provider = OptionsCredentialsPage.Default.ExternalCredentialProviderDefault;
            if (provider == ExternalCredentialProvider.None)
                return;

            string apiReference = OptionsCredentialsPage.Default.UserViaAPIDefault ?? string.Empty;
            ResolveExternalProvider(
                provider,
                apiReference,
                connectionInfo,
                connectionInfo.Hostname,
                connectionInfo.Username,
                ref username,
                ref password,
                ref domain);
        }

        private static void ResolveExternalProvider(
            ExternalCredentialProvider provider,
            string apiReference,
            ConnectionInfo connectionInfo,
            string targetHostname,
            string vaultFallbackUsername,
            ref string username,
            ref string password,
            ref string domain)
        {
            if (provider == ExternalCredentialProvider.None)
                return;

            string privateKey = string.Empty;
            try
            {
                switch (provider)
                {
                    case ExternalCredentialProvider.DelineaSecretServer:
                        ExternalConnectors.DSS.SecretServerInterface.FetchSecretFromServer(
                            apiReference, out username, out password, out domain, out privateKey);
                        break;
                    case ExternalCredentialProvider.ClickstudiosPasswordState:
                        ExternalConnectors.CPS.PasswordstateInterface.FetchSecretFromServer(
                            apiReference, out username, out password, out domain, out privateKey);
                        break;
                    case ExternalCredentialProvider.OnePassword:
                        ExternalConnectors.OP.OnePasswordCli.ReadPassword(
                            apiReference, out username, out password, out domain, out privateKey);
                        break;
                    case ExternalCredentialProvider.PasswordSafe:
                        ExternalConnectors.PasswordSafe.PasswordSafeCli.ReadPassword(
                            apiReference, out username, out password, out domain, out privateKey);
                        break;
                    case ExternalCredentialProvider.VaultOpenbao:
                        if (connectionInfo.VaultOpenbaoSecretEngine == VaultOpenbaoSecretEngine.Kv &&
                            string.IsNullOrEmpty(username))
                        {
                            username = vaultFallbackUsername ?? string.Empty;
                        }

                        ExternalConnectors.VO.VaultOpenbao.ReadPasswordRDP(
                            (int)connectionInfo.VaultOpenbaoSecretEngine,
                            connectionInfo.VaultOpenbaoMount ?? string.Empty,
                            connectionInfo.VaultOpenbaoRole ?? string.Empty,
                            ref username,
                            out password);
                        break;
                    case ExternalCredentialProvider.LAPS:
                        ExternalConnectors.LAPS.LAPSHelper.QueryLAPSPassword(
                            targetHostname, out username, out password, out domain);
                        break;
                    default:
                        throw new NotSupportedException(
                            $"External credential provider '{provider}' is not supported for native RDP launch.");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Unable to resolve credentials using external provider '{provider}'.",
                    ex);
            }
        }
    }
}
