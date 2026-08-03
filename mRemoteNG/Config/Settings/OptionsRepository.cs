using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using mRemoteNG.Config.Settings.Store;

namespace mRemoteNG.Config.Settings
{
    /// <summary>
    /// Business logic layer for managing development-only options.
    /// Provides CRUD operations with validation and consistency checks.
    /// </summary>
    public class OptionsRepository : IOptionsRepository
    {
        private readonly ISettingsStore _store;

        public OptionsRepository(ISettingsStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public OptionsRepository(OptionsStore store)
            : this(new OptionsStoreAdapter(store))
        {
        }

        public Task<IEnumerable<OptionInfo>> GetAllOptionsAsync()
        {
            return _store.GetAllOptionsAsync();
        }

        public Task<OptionInfo> GetOptionByKeyAsync(string key)
        {
            ValidateKey(key);
            return _store.GetOptionByKeyAsync(key);
        }

        public Task<OptionInfo> GetOptionByIdAsync(int id)
        {
            ValidateId(id);
            return _store.GetOptionByIdAsync(id);
        }

        public Task<IEnumerable<OptionInfo>> GetOptionsByCategoryAsync(string category)
        {
            ValidateCategory(category);
            return _store.GetOptionsByCategoryAsync(category);
        }

        public async Task<OptionInfo> AddOptionAsync(OptionInfo option)
        {
            ValidateOption(option);

            bool exists = await _store.OptionExistsAsync(option.Key);
            if (exists)
            {
                throw new InvalidOperationException($"An option with key '{option.Key}' already exists.");
            }

            return await _store.AddOptionAsync(option);
        }

        public async Task<bool> UpdateOptionAsync(OptionInfo option)
        {
            ValidateOption(option);
            ValidateId(option.Id);
            return await _store.UpdateOptionAsync(option);
        }

        public async Task<bool> DeleteOptionAsync(int id)
        {
            ValidateId(id);
            return await _store.DeleteOptionAsync(id);
        }

        public async Task<bool> DeleteOptionByKeyAsync(string key)
        {
            ValidateKey(key);
            return await _store.DeleteOptionByKeyAsync(key);
        }

        public Task<bool> OptionExistsAsync(string key)
        {
            ValidateKey(key);
            return _store.OptionExistsAsync(key);
        }

        public Task<int> GetOptionCountAsync()
        {
            return _store.GetOptionCountAsync();
        }

        public Task<string> ExportSchemaAsync()
        {
            return _store.ExportSchemaAsync();
        }

        public Task ImportSchemaAsync(string schemaSql)
        {
            return _store.ImportSchemaAsync(schemaSql);
        }

        public Task ClearAllOptionsAsync()
        {
            return _store.ClearAllOptionsAsync();
        }

        private static void ValidateOption(OptionInfo option)
        {
            if (option == null)
                throw new ArgumentNullException(nameof(option));

            ValidateKey(option.Key);
        }

        private static void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Option key cannot be null or whitespace.", nameof(key));

            if (key.Length > 255)
                throw new ArgumentException("Option key cannot exceed 255 characters.", nameof(key));
        }

        private static void ValidateCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Category cannot be null or whitespace.", nameof(category));

            if (category.Length > 255)
                throw new ArgumentException("Category cannot exceed 255 characters.", nameof(category));
        }

        private static void ValidateId(int id)
        {
            if (id <= 0)
                throw new ArgumentException("ID must be greater than 0.", nameof(id));
        }

        private sealed class OptionsStoreAdapter : ISettingsStore
        {
            private readonly OptionsStore _store;

            public OptionsStoreAdapter(OptionsStore store)
            {
                _store = store ?? throw new ArgumentNullException(nameof(store));
            }

            public bool IsInitialized => _store.IsInitialized;
            public int SchemaVersion => 1;

            public T Get<T>(string category, string key, T defaultValue = default) => defaultValue;
            public void Set<T>(string category, string key, T value) => throw new NotSupportedException();
            public bool Remove(string category, string key) => throw new NotSupportedException();
            public IReadOnlyDictionary<string, string> GetAll(string category) => new Dictionary<string, string>();
            public IReadOnlyList<string> GetCategories() => Array.Empty<string>();
            public bool Exists(string category, string key) => false;
            public void Flush()
            {
            }

            public Task<IEnumerable<OptionInfo>> GetAllOptionsAsync() => _store.GetAllOptionsAsync();
            public Task<OptionInfo> GetOptionByKeyAsync(string key) => _store.GetOptionByKeyAsync(key);
            public Task<OptionInfo> GetOptionByIdAsync(int id) => _store.GetOptionByIdAsync(id);
            public Task<IEnumerable<OptionInfo>> GetOptionsByCategoryAsync(string category) => _store.GetOptionsByCategoryAsync(category);
            public Task<OptionInfo> AddOptionAsync(OptionInfo option) => _store.AddOptionAsync(option);
            public Task<bool> UpdateOptionAsync(OptionInfo option) => _store.UpdateOptionAsync(option);
            public Task<bool> DeleteOptionAsync(int id) => _store.DeleteOptionAsync(id);
            public Task<bool> DeleteOptionByKeyAsync(string key) => _store.DeleteOptionByKeyAsync(key);
            public Task<bool> OptionExistsAsync(string key) => _store.OptionExistsAsync(key);
            public Task<int> GetOptionCountAsync() => _store.GetOptionCountAsync();
            public Task ClearAllOptionsAsync() => _store.ClearAllOptionsAsync();
            public Task<string> ExportSchemaAsync() => Task.FromResult(string.Empty);
            public Task ImportSchemaAsync(string schemaSql) => Task.CompletedTask;
            public void Dispose()
            {
            }
        }
    }
}
