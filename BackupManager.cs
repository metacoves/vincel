using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace WindowsFormsApp1
{
    public class BackupItem
    {
        public string Type { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public string OriginalContent { get; set; }
        public string BackupPath { get; set; }
        public DateTime BackupTime { get; set; }
    }

    public static class BackupManager
    {
        private static readonly string BackupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RogueCleaner", "Backup");

        private static readonly string LogFile = Path.Combine(BackupDir, "backup.log");
        private static List<BackupItem> _currentBackup = new List<BackupItem>();

        private const long MaxSingleFileBytes = 100L * 1024 * 1024;
        private const long MaxTotalBytesPerRun = 2L * 1024 * 1024 * 1024;
        private static long _bytesThisRun;

        public static int SkippedTooLarge { get; private set; }

        static BackupManager()
        {
            try
            {
                if (!Directory.Exists(BackupDir)) Directory.CreateDirectory(BackupDir);
            }
            catch { }
        }

        public static void StartBackup()
        {
            _currentBackup = new List<BackupItem>();
            _bytesThisRun = 0;
            SkippedTooLarge = 0;
        }

        public static void BackupFile(string filePath, string type, string name)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                var info = new FileInfo(filePath);
                if (info.Length > MaxSingleFileBytes ||
                    _bytesThisRun + info.Length > MaxTotalBytesPerRun)
                {
                    SkippedTooLarge++;
                    return;
                }

                if (!Directory.Exists(BackupDir)) Directory.CreateDirectory(BackupDir);

                var backupPath = Path.Combine(BackupDir,
                    $"{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}.bak");
                File.Copy(filePath, backupPath, true);
                _bytesThisRun += info.Length;

                _currentBackup.Add(new BackupItem
                {
                    Type = type,
                    Path = filePath,
                    Name = name,
                    BackupPath = backupPath,
                    BackupTime = DateTime.Now
                });
            }
            catch { }
        }

        public static void BackupDirectory(string dirPath, string type, string name)
        {
            try
            {
                if (!Directory.Exists(dirPath)) return;

                foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
                {
                    BackupFile(file, type, name);
                }
            }
            catch { }
        }

        public static void BackupScheduledTask(string taskName, string type, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(taskName)) return;

                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/query /tn \"{taskName}\" /xml",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Unicode
                });
                if (proc == null) return;

                string xml = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(8000);
                if (!proc.HasExited || proc.ExitCode != 0 || string.IsNullOrWhiteSpace(xml)) return;

                if (!Directory.Exists(BackupDir)) Directory.CreateDirectory(BackupDir);

                var backupPath = Path.Combine(BackupDir,
                    $"{DateTime.Now:yyyyMMddHHmmss}_task_{Guid.NewGuid():N}.taskxml");
                File.WriteAllText(backupPath, xml, Encoding.Unicode);

                _currentBackup.Add(new BackupItem
                {
                    Type = type,
                    Path = taskName,
                    Name = name,
                    BackupPath = backupPath,
                    BackupTime = DateTime.Now
                });
            }
            catch { }
        }

        public static void BackupRegistryValue(RegistryKey root, string keyPath, string valueName, string type, string name)
        {
            try
            {
                using (var key = root.OpenSubKey(keyPath, false))
                {
                    if (key == null) return;
                    var value = key.GetValue(valueName);
                    if (value == null) return;

                    RegistryValueKind kind;
                    try { kind = key.GetValueKind(valueName); }
                    catch { kind = RegistryValueKind.String; }

                    var sb = new StringBuilder();
                    sb.AppendLine("Windows Registry Editor Version 5.00");
                    sb.AppendLine();
                    sb.AppendLine($"[{GetRegistryPath(root, keyPath)}]");
                    sb.AppendLine(FormatRegValue(valueName, value, kind));

                    var backupPath = Path.Combine(BackupDir,
                        $"{DateTime.Now:yyyyMMddHHmmss}_reg_{Guid.NewGuid():N}.reg");

                    if (!Directory.Exists(BackupDir)) Directory.CreateDirectory(BackupDir);
                    File.WriteAllText(backupPath, sb.ToString(), Encoding.Unicode);

                    _currentBackup.Add(new BackupItem
                    {
                        Type = type,
                        Path = $"{GetRegistryPath(root, keyPath)}\\{valueName}",
                        Name = name,
                        BackupPath = backupPath,
                        BackupTime = DateTime.Now
                    });
                }
            }
            catch { }
        }

        public static void BackupRegistryKey(RegistryKey root, string keyPath, string type, string name)
        {
            try
            {
                using (var key = root.OpenSubKey(keyPath, false))
                {
                    if (key == null) return;

                    var sb = new StringBuilder();
                    sb.AppendLine("Windows Registry Editor Version 5.00");
                    sb.AppendLine();
                    WriteKeyRecursive(key, GetRegistryPath(root, keyPath), sb, 0);

                    var backupPath = Path.Combine(BackupDir,
                        $"{DateTime.Now:yyyyMMddHHmmss}_reg_{Guid.NewGuid():N}.reg");

                    if (!Directory.Exists(BackupDir)) Directory.CreateDirectory(BackupDir);
                    File.WriteAllText(backupPath, sb.ToString(), Encoding.Unicode);

                    _currentBackup.Add(new BackupItem
                    {
                        Type = type,
                        Path = GetRegistryPath(root, keyPath),
                        Name = name,
                        BackupPath = backupPath,
                        BackupTime = DateTime.Now
                    });
                }
            }
            catch { }
        }

        private static void WriteKeyRecursive(RegistryKey key, string fullPath, StringBuilder sb, int depth)
        {
            if (depth > 8) return;

            sb.AppendLine($"[{fullPath}]");
            foreach (var valueName in key.GetValueNames())
            {
                try
                {
                    var value = key.GetValue(valueName);
                    if (value == null) continue;
                    var kind = key.GetValueKind(valueName);
                    sb.AppendLine(FormatRegValue(valueName, value, kind));
                }
                catch { }
            }
            sb.AppendLine();

            foreach (var subName in key.GetSubKeyNames())
            {
                try
                {
                    using (var sub = key.OpenSubKey(subName, false))
                    {
                        if (sub != null)
                            WriteKeyRecursive(sub, fullPath + "\\" + subName, sb, depth + 1);
                    }
                }
                catch { }
            }
        }

        private static string FormatRegValue(string valueName, object value, RegistryValueKind kind)
        {
            var escapedName = EscapeReg(valueName);

            switch (kind)
            {
                case RegistryValueKind.DWord:
                    return $"\"{escapedName}\"=dword:{unchecked((uint)Convert.ToInt32(value)):x8}";

                case RegistryValueKind.QWord:
                    return $"\"{escapedName}\"=hex(b):{ToHexBytes(BitConverter.GetBytes(unchecked((ulong)Convert.ToInt64(value))))}";

                case RegistryValueKind.Binary:
                    return $"\"{escapedName}\"=hex:{ToHexBytes((byte[])value)}";

                case RegistryValueKind.ExpandString:
                    return $"\"{escapedName}\"=hex(2):{ToHexUnicode(value.ToString())}";

                case RegistryValueKind.MultiString:
                    {
                        var joined = string.Join("\0", (string[])value) + "\0";
                        return $"\"{escapedName}\"=hex(7):{ToHexUnicode(joined)}";
                    }

                default:
                    return $"\"{escapedName}\"=\"{EscapeReg(value.ToString())}\"";
            }
        }

        private static string EscapeReg(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string ToHexBytes(byte[] bytes)
        {
            var parts = new List<string>();
            foreach (var b in bytes) parts.Add(b.ToString("x2"));
            return string.Join(",", parts);
        }

        private static string ToHexUnicode(string s)
        {
            var bytes = Encoding.Unicode.GetBytes(s + "\0");
            return ToHexBytes(bytes);
        }

        public static void SaveBackupLog()
        {
            try
            {
                if (_currentBackup.Count == 0) return;
                if (!Directory.Exists(BackupDir)) Directory.CreateDirectory(BackupDir);

                var logEntry = new StringBuilder();
                logEntry.AppendLine($"=== 备份时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                foreach (var item in _currentBackup)
                {
                    logEntry.AppendLine(string.Join("|", new[]
                    {
                        Sanitize(item.Type),
                        Sanitize(item.Name),
                        Sanitize(item.Path),
                        Sanitize(item.BackupPath)
                    }));
                }
                logEntry.AppendLine();

                File.AppendAllText(LogFile, logEntry.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        public static string RestoreLastBackup()
        {
            try
            {
                if (!File.Exists(LogFile)) return "没有找到备份记录";

                var lines = File.ReadAllLines(LogFile, Encoding.UTF8);

                int lastHeaderIndex = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("=== 备份时间:")) lastHeaderIndex = i;
                }
                if (lastHeaderIndex == -1) return "没有找到备份记录";

                var lastBackup = new List<BackupItem>();
                for (int i = lastHeaderIndex + 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("=== 备份时间:")) break;
                    if (!line.Contains("|")) continue;

                    var parts = line.Split('|');
                    if (parts.Length >= 4)
                    {
                        lastBackup.Add(new BackupItem
                        {
                            Type = parts[0],
                            Name = parts[1],
                            Path = parts[2],
                            BackupPath = parts[3]
                        });
                    }
                }

                if (lastBackup.Count == 0) return "没有找到可恢复的备份";

                int success = 0, fail = 0, missing = 0;
                bool restoredService = false;

                foreach (var item in lastBackup)
                {
                    try
                    {
                        if (!File.Exists(item.BackupPath)) { missing++; continue; }

                        if (item.BackupPath.EndsWith(".taskxml", StringComparison.OrdinalIgnoreCase))
                        {
                            var proc = Process.Start(new ProcessStartInfo
                            {
                                FileName = "schtasks.exe",
                                Arguments = $"/create /tn \"{item.Path}\" /xml \"{item.BackupPath}\" /f",
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            });
                            if (proc != null)
                            {
                                proc.WaitForExit(8000);
                                if (proc.HasExited && proc.ExitCode == 0) success++;
                                else fail++;
                            }
                            else fail++;
                        }
                        else if (item.BackupPath.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
                        {
                            var proc = Process.Start(new ProcessStartInfo
                            {
                                FileName = "regedit.exe",
                                Arguments = $"/s \"{item.BackupPath}\"",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            });
                            proc?.WaitForExit(8000);
                            success++;
                            if (item.Type == "系统服务") restoredService = true;
                        }
                        else
                        {
                            var dir = Path.GetDirectoryName(item.Path);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                Directory.CreateDirectory(dir);

                            File.Copy(item.BackupPath, item.Path, true);
                            success++;
                        }
                    }
                    catch
                    {
                        fail++;
                    }
                }

                var msg = $"恢复完成：成功 {success} 个，失败 {fail} 个";
                if (missing > 0) msg += $"，备份文件已丢失 {missing} 个";
                msg += "。";
                if (restoredService)
                    msg += "\n服务注册已写回注册表，需要重启电脑后 Windows 才会重新识别该服务并生效。";
                msg += "\n注意：清理时被结束的进程不会自动恢复，若相关程序未随备份还原，建议重启电脑。";
                return msg;
            }
            catch (Exception ex)
            {
                return $"恢复失败：{ex.Message}";
            }
        }

        private static string GetRegistryPath(RegistryKey root, string keyPath)
        {
            string rootName = "HKEY_CURRENT_USER";
            if (root == Registry.LocalMachine) rootName = "HKEY_LOCAL_MACHINE";
            else if (root == Registry.ClassesRoot) rootName = "HKEY_CLASSES_ROOT";
            else if (root == Registry.Users) rootName = "HKEY_USERS";
            return $"{rootName}\\{keyPath}";
        }
    }
}
