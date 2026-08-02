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

## SQL/database persistence
- Added `RdpClientMode` to the explicit `tblCons` DataTable schema.
- Added dirty checking, serialization, and backward-compatible deserialization.
- Missing columns/values keep the model default `Embedded`.
- Expected-schema consumers found:
- `mRemoteNG\Config\Serializers\ConnectionSerializers\Sql\SqlDatabaseMetaDataRetriever.cs` line 851: `DataTable expectedSchema = DataTableSerializer.GetExpectedSchema();`
- `mRemoteNG\Config\Serializers\ConnectionSerializers\Sql\SqlDatabaseMetaDataRetriever.cs` line 878: `DataTable expectedSchema = DataTableSerializer.GetExpectedSchema();`
- `mRemoteNGTests\Config\Serializers\DataTableSerializerTests.cs` line 145: `DataTable expectedSchema = DataTableSerializer.GetExpectedSchema();`
- `mRemoteNGTests\Config\Serializers\DataTableSerializerTests.cs` line 155: `DataTable expectedSchema = DataTableSerializer.GetExpectedSchema();`
- `mRemoteNGTests\Config\Serializers\ConnectionSerializers\Sql\NativeRdpDataTableSchemaTests.cs` line 12: `var schema = DataTableSerializer.GetExpectedSchema();`
