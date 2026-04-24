using System.IO;
using System.IO.Compression;
using AudioPilot.Constants;

namespace AudioPilot.Logging
{
    internal readonly record struct LogFileInventory(
        string LogFilePath,
        bool LogFileExists,
        long LogFileBytes,
        string LogBackupDirectory,
        IReadOnlyList<string> LogBackupFiles);

    internal readonly record struct LogArchiveExportResult(
        string ExportPath,
        int ExportedFileCount,
        long ExportedBytes,
        IReadOnlyList<LogArchiveExportEntryResult> Entries);

    internal readonly record struct LogArchiveExportEntryResult(
        string Status,
        string SourceKind,
        string SourcePath,
        string ArchiveEntry,
        long Bytes);

    internal readonly record struct LogArchiveExportCandidate(
        string SourceKind,
        string SourcePath,
        string ArchiveEntry);

    internal static class LogArchiveExportService
    {
        internal static LogFileInventory GetInventory(string logRootDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(logRootDirectory);

            string fullRoot = Path.GetFullPath(logRootDirectory);
            string logFilePath = Path.Combine(fullRoot, AppConstants.Files.LogFileName);
            bool logFileExists = File.Exists(logFilePath);
            long logFileBytes = logFileExists ? new FileInfo(logFilePath).Length : 0;

            string logBackupDirectory = Path.Combine(fullRoot, AppConstants.Files.BackupFolderName);
            List<string> logBackupFiles = Directory.Exists(logBackupDirectory)
                ? [.. Directory.GetFiles(logBackupDirectory, AppConstants.Files.LogFileName + ".bak*").OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]
                : [];

            return new LogFileInventory(logFilePath, logFileExists, logFileBytes, logBackupDirectory, logBackupFiles);
        }

        internal static LogArchiveExportResult ExportLogs(string logRootDirectory, string destinationPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            string fullDestinationPath = Path.GetFullPath(destinationPath);
            if (!string.Equals(Path.GetExtension(fullDestinationPath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("Only .zip log exports are supported.");
            }

            string? destinationDirectory = Path.GetDirectoryName(fullDestinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            LogFileInventory inventory = GetInventory(logRootDirectory);
            List<LogArchiveExportCandidate> candidates = BuildExportCandidates(inventory, logRootDirectory);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("No log files are available to export.");
            }

            int exportedFileCount = 0;
            long exportedBytes = 0;
            var entries = new List<LogArchiveExportEntryResult>(candidates.Count);

            using var stream = new FileStream(fullDestinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            for (int index = 0; index < candidates.Count; index++)
            {
                LogArchiveExportCandidate candidate = candidates[index];
                string sourcePath = candidate.SourcePath;

                try
                {
                    using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    ZipArchiveEntry entry = archive.CreateEntry(candidate.ArchiveEntry, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    sourceStream.CopyTo(entryStream);

                    exportedFileCount++;
                    exportedBytes += sourceStream.Length;
                    entries.Add(new LogArchiveExportEntryResult("exported", candidate.SourceKind, sourcePath, candidate.ArchiveEntry, sourceStream.Length));
                }
                catch (FileNotFoundException)
                {
                    entries.Add(new LogArchiveExportEntryResult("missing-at-export", candidate.SourceKind, sourcePath, candidate.ArchiveEntry, 0));
                }
                catch (DirectoryNotFoundException)
                {
                    entries.Add(new LogArchiveExportEntryResult("missing-at-export", candidate.SourceKind, sourcePath, candidate.ArchiveEntry, 0));
                }
            }

            if (exportedFileCount == 0)
            {
                throw new InvalidOperationException("No log files were available to export.");
            }

            return new LogArchiveExportResult(fullDestinationPath, exportedFileCount, exportedBytes, entries);
        }

        private static List<LogArchiveExportCandidate> BuildExportCandidates(LogFileInventory inventory, string logRootDirectory)
        {
            var candidates = new List<LogArchiveExportCandidate>(inventory.LogBackupFiles.Count + 1);
            if (inventory.LogFileExists)
            {
                candidates.Add(new LogArchiveExportCandidate("current", inventory.LogFilePath, GetArchiveEntryName(inventory.LogFilePath, logRootDirectory)));
            }

            for (int index = 0; index < inventory.LogBackupFiles.Count; index++)
            {
                string backupPath = inventory.LogBackupFiles[index];
                if (File.Exists(backupPath))
                {
                    candidates.Add(new LogArchiveExportCandidate("backup", backupPath, GetArchiveEntryName(backupPath, logRootDirectory)));
                }
            }

            return candidates;
        }

        private static string GetArchiveEntryName(string sourcePath, string logRootDirectory)
        {
            string fullRoot = Path.GetFullPath(logRootDirectory);
            string relativePath = Path.GetRelativePath(fullRoot, sourcePath);
            if (relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                return Path.GetFileName(sourcePath);
            }

            return relativePath.Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
