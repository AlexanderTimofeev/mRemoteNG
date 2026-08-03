using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace mRemoteNG.Config.Settings.Store
{
    /// <summary>
    /// SQLite-backed options store for development-only option management.
    /// Provides asynchronous CRUD operations for options that can be added/edited/deleted at runtime.
    /// </summary>
    public class OptionsStore : IOptionsStore
    {
        private readonly string _dbPath;
        private readonly string _connectionString;
        private SqliteConnection _connection;
        private bool _disposed;

        public bool IsInitialized { get; private set; }

        public OptionsStore(string dbPath, string dekHex = null)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));

            SqliteConnectionStringBuilder builder = new()
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };

            if (!string.IsNullOrEmpty(dekHex))
                builder.Password = dekHex;

            _connectionString = builder.ToString();
        }

        public void Initialize()
        {
            EnsureNotDisposed();
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();
            Execute("PRAGMA journal_mode=WAL;");
            if (!TableExists("options"))
                CreateSchema();
            IsInitialized = true;
        }

        private void CreateSchema()
        {
            const string ddl = """
                CREATE TABLE IF NOT EXISTS options (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    key         TEXT    NOT NULL UNIQUE,
                    value       TEXT,
                    category    TEXT,
                    description TEXT
                );
                """;
            Execute(ddl);
        }

        private bool TableExists(string tableName)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }

        private void Execute(string sql)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public async Task<IEnumerable<OptionInfo>> GetAllOptionsAsync()
        {
            EnsureInitialized();
            List<OptionInfo> results = [];
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT id, key, value, category, description FROM options ORDER BY key;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                results.Add(ReadOption(reader));
            return results;
        }

        public async Task<OptionInfo> GetOptionByKeyAsync(string key)
        {
            EnsureInitialized();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT id, key, value, category, description FROM options WHERE key = $key LIMIT 1;";
            command.Parameters.AddWithValue("$key", key);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadOption(reader) : null;
        }

        public async Task<OptionInfo> GetOptionByIdAsync(int id)
        {
            EnsureInitialized();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT id, key, value, category, description FROM options WHERE id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadOption(reader) : null;
        }

        public async Task<IEnumerable<OptionInfo>> GetOptionsByCategoryAsync(string category)
        {
            EnsureInitialized();
            List<OptionInfo> results = [];
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT id, key, value, category, description FROM options WHERE category = $category ORDER BY key;";
            command.Parameters.AddWithValue("$category", category);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                results.Add(ReadOption(reader));
            return results;
        }

        public async Task<OptionInfo> AddOptionAsync(OptionInfo option)
        {
            EnsureInitialized();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO options (key, value, category, description) VALUES ($key, $value, $category, $description); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$key", option.Key);
            command.Parameters.AddWithValue("$value", (object)option.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("$category", (object)option.Category ?? DBNull.Value);
            command.Parameters.AddWithValue("$description", (object)option.Description ?? DBNull.Value);
            option.Id = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            return option;
        }

        public async Task<bool> UpdateOptionAsync(OptionInfo option)
        {
            EnsureInitialized();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "UPDATE options SET key = $key, value = $value, category = $category, description = $description WHERE id = $id;";
            command.Parameters.AddWithValue("$id", option.Id);
            command.Parameters.AddWithValue("$key", option.Key);
            command.Parameters.AddWithValue("$value", (object)option.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("$category", (object)option.Category ?? DBNull.Value);
            command.Parameters.AddWithValue("$description", (object)option.Description ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync() > 0;
        }

        public async Task<bool> DeleteOptionAsync(int id)
        {
            EnsureInitialized();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM options WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteNonQueryAsync() > 0;
        }

        public async Task<bool> DeleteOptionByKeyAsync(string key)
        {
            EnsureInitialized();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM options WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return await command.ExecuteNonQueryAsync() > 0;
        }

        public async Task<bool> OptionExistsAsync(string key)
        {
            EnsureInitialized();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM options WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
        }

        public async Task<int> GetOptionCountAsync()
        {
            EnsureInitialized();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM options;";
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        public async Task ClearAllOptionsAsync()
        {
            EnsureInitialized();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM options;";
            await command.ExecuteNonQueryAsync();
        }

        private static OptionInfo ReadOption(SqliteDataReader reader) => new()
        {
            Id = reader.GetInt32(0),
            Key = reader.GetString(1),
            Value = reader.IsDBNull(2) ? null : reader.GetString(2),
            Category = reader.IsDBNull(3) ? null : reader.GetString(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4)
        };

        private void EnsureInitialized()
        {
            EnsureNotDisposed();
            if (!IsInitialized)
                throw new InvalidOperationException("OptionsStore must be initialized before use.");
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OptionsStore));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _connection?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
