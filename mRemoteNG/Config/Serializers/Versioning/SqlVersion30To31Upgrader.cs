using System;
using System.Data;
using System.Data.Common;
using System.Runtime.Versioning;
using mRemoteNG.App;
using mRemoteNG.Config.DatabaseConnectors;
using mRemoteNG.Messages;

namespace mRemoteNG.Config.Serializers.Versioning
{
    [SupportedOSPlatform("windows")]
    public class SqlVersion30To31Upgrader(IDatabaseConnector databaseConnector) : IVersionUpgrader
    {
        private readonly Version _version = new(3, 1);
        private readonly IDatabaseConnector _databaseConnector = databaseConnector ?? throw new ArgumentNullException(nameof(databaseConnector));

        public bool CanUpgrade(Version currentVersion)
        {
            return currentVersion == new Version(3, 0) ||
                   (currentVersion < _version && currentVersion >= new Version(3, 0));
        }

        public Version Upgrade()
        {
            Runtime.MessageCollector.AddMessage(
                MessageClass.InformationMsg,
                $"Upgrading database to version {_version}.");

            const string mySqlAlter = @"
ALTER TABLE tblCons ADD COLUMN `RdpClientMode` varchar(32) NOT NULL DEFAULT 'Embedded';
";
            const string mySqlUpdate = @"SET SQL_SAFE_UPDATES=0; UPDATE tblRoot SET ConfVersion=?; SET SQL_SAFE_UPDATES=1;";

            const string msSqlAlter = @"
ALTER TABLE tblCons ADD RdpClientMode varchar(32) NOT NULL CONSTRAINT DF_tblCons_RdpClientMode DEFAULT 'Embedded';
";
            const string msSqlUpdate = @"UPDATE tblRoot SET ConfVersion=@confVersion;";

            using DbTransaction transaction = _databaseConnector.DbConnection().BeginTransaction(IsolationLevel.Serializable);
            DbCommand command;
            if (_databaseConnector.GetType() == typeof(MSSqlDatabaseConnector))
            {
                command = _databaseConnector.DbCommand(msSqlAlter);
                command.Transaction = transaction;
                command.ExecuteNonQuery();
                command = _databaseConnector.DbCommand(msSqlUpdate);
            }
            else if (_databaseConnector.GetType() == typeof(MySqlDatabaseConnector))
            {
                command = _databaseConnector.DbCommand(mySqlAlter);
                command.Transaction = transaction;
                command.ExecuteNonQuery();
                command = _databaseConnector.DbCommand(mySqlUpdate);
            }
            else
            {
                throw new InvalidOperationException("Unknown database back-end");
            }

            command.Transaction = transaction;
            DbParameter versionParameter = command.CreateParameter();
            versionParameter.ParameterName = "confVersion";
            versionParameter.Value = _version.ToString();
            versionParameter.DbType = DbType.String;
            versionParameter.Direction = ParameterDirection.Input;
            command.Parameters.Add(versionParameter);
            command.ExecuteNonQuery();
            transaction.Commit();
            return _version;
        }
    }
}
