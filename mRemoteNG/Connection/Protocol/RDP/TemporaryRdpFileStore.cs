using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public sealed class TemporaryRdpFileStore
    {
        private static readonly TimeSpan StaleAge = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DeleteDelay = TimeSpan.FromSeconds(30);
        private const int MaxDisplayNameLength = 80;

        public string DirectoryPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "mRemoteNG", "Temp", "Rdp");

        public string Create(string content)
        {
            return Create(content, null);
        }

        public string Create(string content, string? connectionName)
        {
            ArgumentNullException.ThrowIfNull(content);
            Directory.CreateDirectory(DirectoryPath);
            CleanupStaleFiles();

            string safeName = SanitizeFileName(connectionName);
            string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            string path = Path.Combine(DirectoryPath, $"{safeName} - {uniqueSuffix}.rdp");
            File.WriteAllText(path, content, Encoding.Unicode);
            return path;
        }

        internal static string SanitizeFileName(string? value)
        {
            string name = string.IsNullOrWhiteSpace(value) ? "mRemoteNG" : value.Trim();
            char[] invalidChars = Path.GetInvalidFileNameChars();
            StringBuilder builder = new(name.Length);

            foreach (char character in name)
            {
                builder.Append(invalidChars.Contains(character) || char.IsControl(character) ? '_' : character);
            }

            name = builder.ToString().Trim().TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(name))
                name = "mRemoteNG";

            if (name.Length > MaxDisplayNameLength)
                name = name[..MaxDisplayNameLength].TrimEnd('.', ' ');

            return name;
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
