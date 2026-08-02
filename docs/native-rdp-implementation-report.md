# Native RDP implementation report

## Finalizer changes
- `mRemoteNG\Config\Serializers\ConnectionSerializers\Xml\XmlConnectionsDeserializer.cs`
- `mRemoteNG\Connection\ConnectionInitiator.cs`
- `mRemoteNG\Config\Serializers\ConnectionSerializers\Xml\RdpClientModeXmlPersistence.cs (removed)`
- `docs\native-rdp-client.md`

## Persistence behavior
- XML serializer writes optional `RdpClientMode`.
- XML deserializer uses `Embedded` when the attribute is absent or invalid.
- The setting is intentionally not inherited in the first implementation.

## Native launch behavior
- Native mode is selected immediately after effective connection preparation and the pre-connection external app.
- No panel, tab, protocol instance, or ActiveX control is created for native mode.
- mRemoteNG-managed SSH tunnels are rejected with an actionable warning until their lifetime is decoupled from embedded tabs.

## Validation
- Build and test results are provided by the repository PR_Validation workflow.
