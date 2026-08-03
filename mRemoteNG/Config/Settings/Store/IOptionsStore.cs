using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace mRemoteNG.Config.Settings.Store
{
    public interface IOptionsStore : IDisposable
    {
        bool IsInitialized { get; }
        Task<IEnumerable<OptionInfo>> GetAllOptionsAsync();
        Task<OptionInfo> GetOptionByKeyAsync(string key);
        Task<OptionInfo> GetOptionByIdAsync(int id);
        Task<IEnumerable<OptionInfo>> GetOptionsByCategoryAsync(string category);
        Task<OptionInfo> AddOptionAsync(OptionInfo option);
        Task<bool> UpdateOptionAsync(OptionInfo option);
        Task<bool> DeleteOptionAsync(int id);
        Task<bool> DeleteOptionByKeyAsync(string key);
        Task<bool> OptionExistsAsync(string key);
        Task<int> GetOptionCountAsync();
        Task ClearAllOptionsAsync();
    }
}
