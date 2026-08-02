using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public sealed class TemporaryRdpFileStore
    {
        private static readonly TimeSpan StaleAge = TimeSpan.FromDays(1);
        private static readonly TimeSpan DeleteDelay = TimeSpan.FromSeconds(30);

        public string DirectoryPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "mRemoteNG", "Temp", "Rdp");

        public string Create(string content)
        {
            ArgumentNullException.ThrowIfNull(content);
            Directory.CreateDirectory(DirectoryPath);
            CleanupStaleFiles();
            string path = Path.Combine(DirectoryPath, $"{Guid.NewGuid():N}.rdp");
            File.WriteAllText(path, content, Encoding.Unicode);
            return path;
        }

        public static void ScheduleDelete(string path)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(DeleteDelay).ConfigureAwait(false);
                TryDelete(path);
            });
        }

        public static void TryDelete(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort. The next cleanup pass removes stale files.
            }
        }

        public void CleanupStaleFiles()
        {
            if (!Directory.Exists(DirectoryPath))
                return;

            DateTime threshold = DateTime.UtcNow - StaleAge;
            foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.rdp", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < threshold)
                        File.Delete(file);
                }
                catch
                {
                    // A running mstsc instance may still hold the file.
                }
            }
        }
    }
}
