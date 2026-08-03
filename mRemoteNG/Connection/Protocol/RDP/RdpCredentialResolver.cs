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
                connectionInfo.UserViaAPI ?? string.Empty,
                connectionInfo,
                connectionInfo.Username ?? string.Empty,
                ref username,
                ref password,
                ref domain);

            ParseDomain(ref username, ref domain);
            ApplyCredentialDefaults(connectionInfo, ref username, ref password, ref domain);
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
                connectionInfo.RDGatewayUserViaAPI ?? string.Empty,
                connectionInfo,
                connectionInfo.RDGatewayUsername ?? string.Empty,
                ref username,
                ref password,
                ref domain);

            ParseDomain(ref username, ref domain);
            return new RdpResolvedCredentials(username, password, domain);
        }

        private static void ParseDomain(ref string username, ref string domain)
        {
            if (!string.IsNullOrEmpty(domain))
                return;

            (string parsedUsername, string parsedDomain) = RdpFileSerializer.ParseDomainFromUsername(username);
            username = parsedUsername;
            domain = parsedDomain;
        }

        private static void ApplyCredentialDefaults(
            ConnectionInfo connectionInfo,
            ref string username,
            ref string password,
            ref string domain)
        {
            string emptyCredentialsMode = OptionsCredentialsPage.Default.EmptyCredentials;

            if (string.IsNullOrEmpty(username))
            {
                switch (emptyCredentialsMode)
                {
                    case "windows":
                        username = Environment.UserName;
                        break;
                    case "custom":
                        username = OptionsCredentialsPage.Default.DefaultUsername ?? string.Empty;
                        if (string.IsNullOrEmpty(username))
                            ResolveDefaultExternalProvider(connectionInfo, ref username, ref password, ref domain);
                        break;
                }
            }

            if (string.IsNullOrEmpty(domain))
            {
                domain = emptyCredentialsMode switch
                {
                    "windows" => Environment.UserDomainName,
                    "custom" => OptionsCredentialsPage.Default.DefaultDomain ?? string.Empty,
                    _ => domain
                };
            }

            if (string.IsNullOrEmpty(password) &&
                string.Equals(emptyCredentialsMode, "custom", StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(OptionsCredentialsPage.Default.DefaultPassword))
            {
                LegacyRijndaelCryptographyProvider cryptographyProvider = new();
                password = cryptographyProvider.Decrypt(
                    OptionsCredentialsPage.Default.DefaultPassword,
                    Runtime.EncryptionKey);
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

            ResolveExternalProvider(
                provider,
                OptionsCredentialsPage.Default.UserViaAPIDefault ?? string.Empty,
                connectionInfo,
                connectionInfo.Username ?? string.Empty,
                ref username,
                ref password,
                ref domain);
        }

        private static void ResolveExternalProvider(
            ExternalCredentialProvider provider,
            string apiReference,
            ConnectionInfo connectionInfo,
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
                switch (provider.ToString())
                {
                    case "DelineaSecretServer":
                        ExternalConnectors.DSS.SecretServerInterface.FetchSecretFromServer(
                            apiReference, out username, out password, out domain, out privateKey);
                        break;
                    case "ClickstudiosPasswordState":
                        ExternalConnectors.CPS.PasswordstateInterface.FetchSecretFromServer(
                            apiReference, out username, out password, out domain, out privateKey);
                        break;
                    case "OnePassword":
                        ExternalConnectors.OP.OnePasswordCli.ReadPassword(
                            apiReference, out username, out password, out domain, out privateKey);
                        break;
                    case "VaultOpenbao":
                        if (connectionInfo.VaultOpenbaoSecretEngine == VaultOpenbaoSecretEngine.Kv &&
                            string.IsNullOrEmpty(username))
                        {
                            username = vaultFallbackUsername;
                        }

                        ExternalConnectors.VO.VaultOpenbao.ReadPasswordRDP(
                            (int)connectionInfo.VaultOpenbaoSecretEngine,
                            connectionInfo.VaultOpenbaoMount ?? string.Empty,
                            connectionInfo.VaultOpenbaoRole ?? string.Empty,
                            ref username,
                            out password);
                        break;
                    default:
                        throw new NotSupportedException(
                            $"External credential provider '{provider}' is not available in this fork for native RDP launch.");
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
