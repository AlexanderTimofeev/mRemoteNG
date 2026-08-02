# Native RDP implementation report

## Changed files
- `mRemoteNGTests\Connection\Protocol\RDP\RdpFileSerializerTests.cs`
- `mRemoteNG\Config\Serializers\ConnectionSerializers\Xml\RdpClientModeXmlPersistence.cs`
- `mRemoteNG\Config\Serializers\ConnectionSerializers\Xml\XmlConnectionNodeSerializer28.cs`
- `mRemoteNG\Connection\AbstractConnectionRecord.cs`
- `mRemoteNG\Connection\Protocol\RDP\NativeRdpLauncher.cs`
- `mRemoteNG\Connection\Protocol\RDP\RdpClientMode.cs`
- `mRemoteNG\Connection\Protocol\RDP\RdpFileSerializer.cs`
- `mRemoteNG\Connection\Protocol\RDP\TemporaryRdpFileStore.cs`
- `mRemoteNG\Connection\Protocol\RDP\WindowsCredentialManager.cs`
- `mRemoteNG\Schemas\mremoteng_confcons_v2_8.xsd`

## Notes
- Deserializer candidate not patchable automatically: mRemoteNG\Config\Serializers\ConnectionSerializers\Xml\XmlConnectionsDeserializer.cs

## Patcher errors
- Could not patch XML deserializer. Candidates:
### mRemoteNG\Config\Serializers\ConnectionSerializers\Xml\XmlConnectionsDeserializer.cs
```csharp
gine.Kv);
                    connectionInfo.EC2InstanceId = a.GetAttr("EC2InstanceId");
                    connectionInfo.EC2Region = a.GetAttr("EC2Region");
                    connectionInfo.UseRestrictedAdmin = a.GetAttrBool("UseRestrictedAdmin");
                    connectionInfo.Inheritance.UseRestrictedAdmin = a.GetAttrBool("InheritUseRestrictedAdmin");
                    connectionInfo.UseRCG = a.GetAttrBool("UseRCG");
                    connectionInfo.Inheritance.UseRCG = a.GetAttrBool("InheritUseRCG");
                    connectionInfo.UseRedirectionServerName = a.GetAttrBool("UseRedirectionServerName");
                    connectionInfo.Inheritance.UseRedirectionServerName = a.GetAttrBool("InheritUseRedirectionServerName");
                    connectionInfo.RDGatewayExternalCredentialProvider = a.GetAttrEnum("RDGatewayExternalCredentialProvider", ExternalCredentialProvider.None);
                    connectionInfo.RDGatewayUserViaAPI = a.GetAttr("RDGatewayUserViaAPI")
```
