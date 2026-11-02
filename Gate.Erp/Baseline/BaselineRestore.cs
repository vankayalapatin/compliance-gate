// Baseline restore: copies the shipped ERP snapshot over the live ERP db.
// Runs while the app is up: the app's own pooled sqlite connections hold
// the live file, so every pooled handle is dropped before the copy (with
// retries in case a request is mid-query) and again after, so later reads
// open the restored file instead of the replaced one. No loader, no model
// call. The platform directory is a passthrough hook owned by the UI lane
// (its restore runs there); this helper only ensures the directory exists.
using Microsoft.Data.Sqlite;

namespace Gate.Erp.Baseline;

public static class BaselineRestore
{
    public static void Restore(string snapshotDir, string erpDbPath, string platformDir)
    {
        if (string.IsNullOrWhiteSpace(snapshotDir)) throw new ArgumentException("Snapshot directory is required.", nameof(snapshotDir));
        if (string.IsNullOrWhiteSpace(erpDbPath)) throw new ArgumentException("ERP db path is required.", nameof(erpDbPath));
        var source = Path.Combine(snapshotDir, "erp.db");
        if (!File.Exists(source))
            throw new FileNotFoundException($"Baseline snapshot not found: {source}.", source);
        var targetDir = Path.GetDirectoryName(Path.GetFullPath(erpDbPath));
        if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
        SqliteConnection.ClearAllPools();
        DeleteSidecars(erpDbPath);
        CopyWithRetry(source, erpDbPath);
        DeleteSidecars(erpDbPath);
        SqliteConnection.ClearAllPools();
        if (!string.IsNullOrWhiteSpace(platformDir)) Directory.CreateDirectory(platformDir);
    }

    private static void DeleteSidecars(string erpDbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = erpDbPath + suffix;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private static void CopyWithRetry(string source, string target)
    {
        IOException? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(250);
            }
        }
        throw last!;
    }
}
