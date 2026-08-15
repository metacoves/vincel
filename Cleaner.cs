using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace WindowsFormsApp1
{
    public static class Cleaner
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);

        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        public static string Clean(ScanResult item)
        {
            try
            {
                switch (item.Type)
                {
                    case "系统服务":
                        return CleanService(item);
                    case "启动项":
                    case "启动项(系统)":
                    case "启动项(一次性)":
                    case "启动项(文件夹)":
                        return CleanStartup(item);
                    case "计划任务":
                        return CleanScheduledTask(item);
                    case "浏览器设置":
                        return CleanBrowser(item);
                    case "快捷方式劫持":
                        return CleanShortcut(item);
                    case "右键菜单残留":
                        return CleanContextMenu(item);
                    case "目录残留":
                        return CleanDirectory(item);
                    default:
                        return "未知类型，跳过";
                }
            }
            catch (Exception ex)
            {
                return $"失败: {ex.Message}";
            }
        }

        private static bool IsProtectedPath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return true;

            string full;
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
                full = Path.GetFullPath(expanded);
            }
            catch
            {
                return true;
            }

            if (full.Length <= 3) return true;

            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (IsUnderOrEqual(full, windowsDir)) return true;

            var containerDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
            };

            var normalized = full.TrimEnd('\\');
            foreach (var dir in containerDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                if (string.Equals(normalized, dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsUnderOrEqual(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            var p = path.TrimEnd('\\');
            var r = root.TrimEnd('\\');
            if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
            return p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanService(ScanResult item)
        {
            var serviceName = !string.IsNullOrEmpty(item.ServiceName) ? item.ServiceName : item.Name;
            var exePath = ExtractExePath(item.Path);
            bool exeProtected = IsProtectedPath(exePath);
            bool stopped = false;

            try
            {
                BackupManager.BackupRegistryKey(Registry.LocalMachine,
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}", item.Type, item.Name);
            }
            catch { }

            try
            {
                using (var sc = new ServiceController(serviceName))
                {
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    }
                    stopped = true;
                }
            }
            catch { }

            // 停服后可能还有托盘/守护进程占用 exe，先杀掉同名进程，避免文件占用删不掉（与 CleanStartup 一致）
            if (!exeProtected && !string.IsNullOrEmpty(exePath))
            {
                try { KillProcessByPath(exePath); } catch { }
            }

            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"delete \"{serviceName}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (proc != null)
                {
                    proc.WaitForExit(8000);
                    // 1072 = 服务已标记为删除：服务仍在停止中，重启后自动消失，算成功不算失败
                    if (proc.HasExited && proc.ExitCode != 0 && proc.ExitCode != 1072)
                    {
                        return $"失败: sc.exe 返回错误码 {proc.ExitCode}";
                    }
                }
            }
            catch (Exception ex)
            {
                return $"失败: 删除服务时出错 {ex.Message}";
            }

            if (exeProtected)
            {
                return stopped
                    ? "服务已停止并删除注册；程序文件位于系统目录，已跳过删除（受保护）"
                    : "服务注册已删除；程序文件位于系统目录，已跳过删除（受保护）";
            }

            try
            {
                if (File.Exists(exePath))
                {
                    BackupManager.BackupFile(exePath, item.Type, item.Name);
                    TryDeleteFile(exePath);
                }
            }
            catch { }

            return "服务已停止并标记删除（重启后彻底消失）";
        }

        private static string CleanStartup(ScanResult item)
        {
            var exePath = ExtractExePath(item.Path);
            bool exeProtected = IsProtectedPath(exePath);

            try
            {
                if (item.Type == "启动项" || item.Type == "启动项(一次性)")
                {
                    DeleteRunValue(Registry.CurrentUser, item);
                    DeleteRunValue(Registry.CurrentUser, item, @"Software\Microsoft\Windows\CurrentVersion\RunOnce");
                }
                else if (item.Type == "启动项(系统)" || item.Type == "启动项(系统/一次性)")
                {
                    DeleteRunValue(Registry.LocalMachine, item);
                    DeleteRunValue(Registry.LocalMachine, item, @"Software\Microsoft\Windows\CurrentVersion\RunOnce");
                }
                else if (item.Type == "启动项(文件夹)")
                {
                    if (File.Exists(item.Path))
                    {
                        BackupManager.BackupFile(item.Path, item.Type, item.Name);
                        TryDeleteFile(item.Path);
                    }
                }
            }
            catch { }

            if (!exeProtected && !string.IsNullOrEmpty(exePath))
            {
                try
                {
                    KillProcessByPath(exePath);
                }
                catch { }
            }

            if (exeProtected)
            {
                return "启动项已删除；程序文件位于系统目录，已跳过删除（受保护）";
            }

            try
            {
                if (File.Exists(exePath))
                {
                    BackupManager.BackupFile(exePath, item.Type, item.Name);
                    TryDeleteFile(exePath);
                }
            }
            catch { }

            return "启动项已删除";
        }

        private static void KillProcessByPath(string exePath)
        {
            try
            {
                var targetPath = Path.GetFullPath(exePath).ToLower();
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var procPath = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(procPath) &&
                            Path.GetFullPath(procPath).ToLower() == targetPath)
                        {
                            proc.Kill();
                        }
                    }
                    catch { }
                    finally { try { proc.Dispose(); } catch { } }
                }
            }
            catch { }
        }

        private static void DeleteRunValue(RegistryKey root, ScanResult item, string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
        {
            try
            {
                using (var key = root.OpenSubKey(runPath, true))
                {
                    if (key != null && key.GetValue(item.Name) != null)
                    {
                        BackupManager.BackupRegistryValue(root, runPath, item.Name, item.Type, item.Name);
                        key.DeleteValue(item.Name, false);
                    }
                }
            }
            catch { }
        }

        private static string CleanScheduledTask(ScanResult item)
        {
            var taskName = !string.IsNullOrEmpty(item.TaskName) ? item.TaskName : item.Name;

            try
            {
                BackupManager.BackupScheduledTask(taskName, item.Type, item.Name);
            }
            catch { }

            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/delete /tn \"{taskName}\" /f",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });

                if (proc != null)
                {
                    proc.WaitForExit(8000);
                    if (proc.HasExited && proc.ExitCode != 0)
                    {
                        return $"失败: 计划任务删除返回错误码 {proc.ExitCode}";
                    }
                }

                return "计划任务已删除";
            }
            catch (Exception ex)
            {
                return $"失败: {ex.Message}";
            }
        }

        private static string CleanBrowser(ScanResult item)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\Main", true))
                {
                    if (key != null && key.GetValue("Start Page") != null)
                    {
                        BackupManager.BackupRegistryValue(Registry.CurrentUser,
                            @"Software\Microsoft\Internet Explorer\Main", "Start Page", item.Type, item.Name);
                        key.SetValue("Start Page", "about:blank");
                    }
                }

                ClearPolicyValues(Registry.CurrentUser, @"Software\Policies\Microsoft\Edge", item);
                ClearPolicyValues(Registry.LocalMachine, @"Software\Policies\Microsoft\Edge", item);
                ClearPolicyValues(Registry.CurrentUser, @"Software\Policies\Google\Chrome", item);
                ClearPolicyValues(Registry.LocalMachine, @"Software\Policies\Google\Chrome", item);
                ClearPolicyValues(Registry.LocalMachine, @"Software\WOW6432Node\Policies\Microsoft\Edge", item);
                ClearPolicyValues(Registry.LocalMachine, @"Software\WOW6432Node\Policies\Google\Chrome", item);

                // Chrome/Edge 配置文件主页修复：先备份，再真的把劫持主页改掉（不只是备份）
                var prefsFiles = new[]
                {
                    new { Browser = "Edge", Path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\Edge\User Data\Default\Preferences") },
                    new { Browser = "Chrome", Path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Google\Chrome\User Data\Default\Preferences") }
                };

                var fixedBrowsers = new List<string>();
                foreach (var prefs in prefsFiles)
                {
                    try
                    {
                        if (!File.Exists(prefs.Path)) continue;

                        BackupManager.BackupFile(prefs.Path, item.Type, item.Name);

                        if (FixPreferencesHomepage(prefs.Path))
                            fixedBrowsers.Add(prefs.Browser);
                        else
                            fixedBrowsers.Add(prefs.Browser + "(配置文件修复失败，已备份请手动处理)");
                    }
                    catch { }
                }

                var msg = "主页策略已清除";
                if (fixedBrowsers.Count > 0)
                    msg += "，" + string.Join("、", fixedBrowsers) + " 配置文件中的劫持主页已修复";
                msg += "。请重启浏览器后检查主页设置。";
                return msg;
            }
            catch (Exception ex)
            {
                return $"失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 修复 Chrome/Edge 的 Preferences 配置文件：把命中黑名单的主页/启动网址清掉，
        /// 其余内容原样保留。修改后用 JSON 重新解析验证，解析失败则不写文件（避免损坏浏览器配置）。
        /// 返回 true 表示文件已处理（含无需修改的情况）。
        /// </summary>
        private static bool FixPreferencesHomepage(string prefsPath)
        {
            string content;
            try { content = File.ReadAllText(prefsPath, Encoding.UTF8); }
            catch { return false; }
            if (string.IsNullOrEmpty(content)) return false;

            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

            object parsed;
            try { parsed = serializer.DeserializeObject(content); }
            catch { return false; }

            var root = parsed as Dictionary<string, object>;
            if (root == null) return false;

            bool changed;
            try { changed = FixHomepageDict(root); }
            catch { return false; }

            if (!changed) return true; // 没有命中黑名单，无需修改

            var output = serializer.Serialize(root);

            // 写回前再验证一次序列化结果能被解析，避免写坏文件
            try { serializer.DeserializeObject(output); }
            catch { return false; }

            try { File.WriteAllText(prefsPath, output, new UTF8Encoding(false)); }
            catch { return false; }
            return true;
        }

        /// <summary>递归处理 Preferences 字典，清理已知劫持主页/启动网址。</summary>
        private static bool FixHomepageDict(Dictionary<string, object> dict)
        {
            bool changed = false;

            // 1. 主页键：命中黑名单 → 改回 about:blank
            foreach (var key in new[] { "homepage", "homepage_url" })
            {
                var value = GetDictValue(dict, key) as string;
                if (!string.IsNullOrEmpty(value) && Scanner.ContainsBadHomepage(value))
                {
                    dict[key] = "about:blank";
                    changed = true;
                }
            }

            // 2. 启动网址数组：剔除命中黑名单的网址
            foreach (var key in new[] { "startup_urls", "urls_to_restore_on_startup" })
            {
                var arr = GetDictValue(dict, key) as object[];
                if (arr == null || arr.Length == 0) continue;

                var kept = arr.Where(u => !(u is string s) || !Scanner.ContainsBadHomepage(s)).ToArray();
                if (kept.Length != arr.Length)
                {
                    dict[key] = kept;
                    changed = true;
                }
            }

            // 3. restore_on_startup = 4（固定网址启动）且启动网址已被清空 → 改回新标签页
            if (ConvertToInt(GetDictValue(dict, "restore_on_startup")) == 4)
            {
                var urls = GetDictValue(dict, "startup_urls") as object[];
                if (urls != null && urls.Length == 0)
                {
                    dict["restore_on_startup"] = 1;
                    dict.Remove("startup_urls");
                    changed = true;
                }
            }

            // 4. 嵌套的 session 对象（Edge/Chrome 的启动设置一般在这里）
            var session = GetDictValue(dict, "session") as Dictionary<string, object>;
            if (session != null && FixHomepageDict(session))
                changed = true;

            return changed;
        }

        private static object GetDictValue(Dictionary<string, object> dict, string key)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v)) return v;
            return null;
        }

        private static int ConvertToInt(object value)
        {
            if (value is int) return (int)value;
            if (value is long) return (int)(long)value;
            if (value is decimal) return (int)(decimal)value;
            if (value is double) return (int)(double)value;
            if (value is string && int.TryParse((string)value, out var i)) return i;
            return 0;
        }

        private static void ClearPolicyValues(RegistryKey root, string policyPath, ScanResult item)
        {
            try
            {
                using (var key = root.OpenSubKey(policyPath, true))
                {
                    if (key == null) return;
                    foreach (var valueName in new[] { "HomepageLocation", "RestoreOnStartup", "RestoreOnStartupURLs" })
                    {
                        try
                        {
                            if (key.GetValue(valueName) != null)
                            {
                                BackupManager.BackupRegistryValue(root, policyPath, valueName, item.Type, item.Name);
                                key.DeleteValue(valueName, false);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static string CleanShortcut(ScanResult item)
        {
            try
            {
                if (IsProtectedPath(item.Path)) return "跳过：该快捷方式位于受保护目录";

                if (File.Exists(item.Path))
                {
                    BackupManager.BackupFile(item.Path, item.Type, item.Name);
                    TryDeleteFile(item.Path);
                }
                return "恶意快捷方式已删除，请从开始菜单重新创建";
            }
            catch (Exception ex)
            {
                return $"失败: {ex.Message}，建议手动删除快捷方式";
            }
        }

        private static string CleanContextMenu(ScanResult item)
        {
            try
            {
                RegistryKey root;
                if (item.Path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
                    root = Registry.CurrentUser;
                else if (item.Path.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase))
                    root = Registry.LocalMachine;
                else
                    return "失败: 无法识别的注册表根路径";

                var regPath = item.Path.Substring(5);
                var lastSlash = regPath.LastIndexOf('\\');
                if (lastSlash <= 0) return "失败: 注册表路径格式异常";

                var keyPath = regPath.Substring(0, lastSlash);
                var subKeyName = regPath.Substring(lastSlash + 1);

                BackupManager.BackupRegistryKey(root, keyPath + "\\" + subKeyName, item.Type, item.Name);

                using (var key = root.OpenSubKey(keyPath, true))
                {
                    if (key != null) key.DeleteSubKeyTree(subKeyName, false);
                }

                return "右键菜单残留已删除";
            }
            catch (Exception ex)
            {
                return $"失败: {ex.Message}";
            }
        }

        private static string CleanDirectory(ScanResult item)
        {
            try
            {
                if (IsProtectedPath(item.Path))
                    return "跳过：该目录属于系统或关键目录，已保护";

                if (!Directory.Exists(item.Path)) return "目录不存在，跳过";

                KillProcessesInDirectory(item.Path);

                BackupManager.BackupDirectory(item.Path, item.Type, item.Name);

                TryDeleteDirectory(item.Path);

                return Directory.Exists(item.Path)
                    ? "目录被占用，已标记为重启后删除"
                    : "目录已删除";
            }
            catch (Exception ex)
            {
                try
                {
                    MoveFileEx(item.Path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                    return "文件占用，重启后自动删除";
                }
                catch
                {
                    return $"失败: {ex.Message}";
                }
            }
        }

        private static void KillProcessesInDirectory(string path)
        {
            try
            {
                if (IsProtectedPath(path)) return;

                var prefix = Path.GetFullPath(path).TrimEnd('\\') + "\\";

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var exe = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exe) &&
                            exe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            proc.Kill();
                        }
                    }
                    catch { }
                    finally { try { proc.Dispose(); } catch { } }
                }
            }
            catch { }
        }

        private static void TryDeleteFile(string path)
        {
            if (IsProtectedPath(path)) return;

            try
            {
                File.Delete(path);
            }
            catch
            {
                try { MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
                catch { }
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (IsProtectedPath(path)) return;

            try
            {
                Directory.Delete(path, true);
            }
            catch
            {
                try
                {
                    MarkFilesForDelete(path);
                }
                catch { }
            }
        }

        private static void MarkFilesForDelete(string path)
        {
            try
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
                    catch { }
                }

                var dirs = Directory.GetDirectories(path, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length);

                foreach (var dir in dirs)
                {
                    try { MoveFileEx(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
                    catch { }
                }

                MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            }
            catch { }
        }

        private static string ExtractExePath(string command)
        {
            if (string.IsNullOrEmpty(command)) return "";

            var cmd = command.Trim();

            if (cmd.StartsWith(@"\??\")) cmd = cmd.Substring(4);

            try { cmd = Environment.ExpandEnvironmentVariables(cmd); }
            catch { }

            if (cmd.StartsWith("\""))
            {
                var endQuote = cmd.IndexOf('"', 1);
                if (endQuote > 0) return cmd.Substring(1, endQuote - 1);
            }

            var extensions = new[] { ".exe", ".com", ".bat", ".cmd", ".scr" };
            foreach (var ext in extensions)
            {
                var extIndex = cmd.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (extIndex > 0) return cmd.Substring(0, extIndex + ext.Length);
            }

            var spaceIndex = cmd.IndexOf(' ');
            if (spaceIndex > 0) return cmd.Substring(0, spaceIndex);

            return cmd;
        }
    }
}
