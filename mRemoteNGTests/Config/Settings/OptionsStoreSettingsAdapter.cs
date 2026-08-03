using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using mRemoteNG.Config.Settings;
using mRemoteNG.Config.Settings.Store;

namespace mRemoteNGTests.Config.Settings
{
    internal sealed class OptionsStoreSettingsAdapter : ISettingsStore
    {
        private readonly OptionsStore _store;

        public OptionsStoreSettingsAdapter(OptionsStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public bool IsInitialized => _store.IsInitialized;
        public int SchemaVersion => 1;

        public T Get<T>(string category, string key, T defaultValue = default) => defaultValue;
        public void Set<T>(string category, string key, T value) => throw new NotSupportedException();
        public bool Remove(string category, string key) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, string> GetAll(string category) =>
            new Dictionary<string, string>();
        public IReadOnlyList<string> GetCategories() => Array.Empty<string>();
        public bool Exists(string category, string key) => false;
        public void Flush()
        {
        }

        public Task<IEnumerable<OptionInfo>> GetAllOptionsAsync() => _store.GetAllOptionsAsync();
        public Task<OptionInfo> GetOptionByKeyAsync(string key) => _store.GetOptionByKeyAsync(key);
        public Task<OptionInfo> GetOptionByIdAsync(int id) => _store.GetOptionByIdAsync(id);
        public Task<IEnumerable<OptionInfo>> GetOptionsByCategoryAsync(string category) =>
            _store.GetOptionsByCategoryAsync(category);
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
