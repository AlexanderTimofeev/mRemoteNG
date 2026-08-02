from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")


def write(path: Path, content: str) -> None:
    path.write_text(content.replace("\r\n", "\n"), encoding="utf-8", newline="\n")


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = read(path)
    if new in text:
        return
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one anchor, found {count} in {path}")
    write(path, text.replace(old, new, 1))


serializer = ROOT / "mRemoteNG" / "Config" / "Serializers" / "ConnectionSerializers" / "Sql" / "DataTableSerializer.cs"
deserializer = ROOT / "mRemoteNG" / "Config" / "Serializers" / "ConnectionSerializers" / "Sql" / "DataTableDeserializer.cs"

replace_once(
    serializer,
    '            dataTable.Columns.Add("RdpVersion", typeof(string));\n',
    '            dataTable.Columns.Add("RdpVersion", typeof(string));\n'
    '            dataTable.Columns.Add("RdpClientMode", typeof(string));\n',
    "SQL schema column")

replace_once(
    serializer,
    '            isFieldNotChange = isFieldNotChange && dataRow["RdpVersion"].Equals(connectionInfo.RdpVersion.ToString());\n',
    '            isFieldNotChange = isFieldNotChange && dataRow["RdpVersion"].Equals(connectionInfo.RdpVersion.ToString());\n'
    '            isFieldNotChange = isFieldNotChange && dataRow["RdpClientMode"].Equals(connectionInfo.RdpClientMode.ToString());\n',
    "SQL dirty checking")

replace_once(
    serializer,
    '            dataRow["RdpVersion"] = connectionInfo.RdpVersion;\n',
    '            dataRow["RdpVersion"] = connectionInfo.RdpVersion;\n'
    '            dataRow["RdpClientMode"] = connectionInfo.RdpClientMode;\n',
    "SQL serialization")

rdp_version_block = '''            if (!dataRow.IsNull("RdpVersion")) // table allows null values which must be handled
                if (Enum.TryParse((string)dataRow["RdpVersion"], true, out RdpVersion rdpVersion))
                    connectionInfo.RdpVersion = rdpVersion;
'''
replace_once(
    deserializer,
    rdp_version_block,
    rdp_version_block + '''
            if (dataRow.Table.Columns.Contains("RdpClientMode") && !dataRow.IsNull("RdpClientMode"))
                if (Enum.TryParse((string)dataRow["RdpClientMode"], true, out RdpClientMode rdpClientMode))
                    connectionInfo.RdpClientMode = rdpClientMode;
''',
    "SQL deserialization")

# Add a focused schema regression test. The project exposes serializer internals to its test assembly.
test = ROOT / "mRemoteNGTests" / "Config" / "Serializers" / "ConnectionSerializers" / "Sql" / "NativeRdpDataTableSchemaTests.cs"
test.parent.mkdir(parents=True, exist_ok=True)
write(test, '''using mRemoteNG.Config.Serializers.ConnectionSerializers.Sql;
using NUnit.Framework;

namespace mRemoteNGTests.Config.Serializers.ConnectionSerializers.Sql
{
    [TestFixture]
    public class NativeRdpDataTableSchemaTests
    {
        [Test]
        public void ExpectedSchemaContainsRdpClientMode()
        {
            var schema = DataTableSerializer.GetExpectedSchema();
            Assert.That(schema.Columns.Contains("RdpClientMode"), Is.True);
            Assert.That(schema.Columns["RdpClientMode"]!.DataType, Is.EqualTo(typeof(string)));
        }
    }
}
''')

# Record how the expected schema is consumed so the migration behavior is reviewable.
hits = []
for path in ROOT.rglob("*.cs"):
    if path == serializer:
        continue
    text = read(path)
    if "GetExpectedSchema" in text:
        for line_number, line in enumerate(text.splitlines(), 1):
            if "GetExpectedSchema" in line:
                hits.append(f"- `{path.relative_to(ROOT)}` line {line_number}: `{line.strip()}`")

report = ROOT / "docs" / "native-rdp-implementation-report.md"
existing = read(report) if report.exists() else "# Native RDP implementation report\n"
section = "\n## SQL/database persistence\n- Added `RdpClientMode` to the explicit `tblCons` DataTable schema.\n- Added dirty checking, serialization, and backward-compatible deserialization.\n- Missing columns/values keep the model default `Embedded`.\n"
if hits:
    section += "- Expected-schema consumers found:\n" + "\n".join(hits) + "\n"
else:
    section += "- No external `GetExpectedSchema` consumer was found by source scan; review physical database migration separately.\n"
if "## SQL/database persistence" not in existing:
    write(report, existing.rstrip() + "\n" + section)
