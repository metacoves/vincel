using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace WindowsFormsApp1
{
    public static class Cleaner
    {
        // 已成功删除的服务名集合（sc delete标记删除后重启前仍在列表中，用此集合确保重扫时跳过）
        private static readonly HashSet<string> _deletedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>检查服务是否已被本程序标记删除（重扫时跳过用）。</summary>
        public static bool IsServiceDeleted(string serviceName)
        {
            return !string.IsNullOrEmpty(serviceName) && _deletedServices.Contains(serviceName);
        }

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RogueCleaner", "clean.log");

        /// <summary>写本地诊断日志（仅保存在用户电脑，不联网、不上传，用于排查问题）。</summary>
        public static void LogClean(string action, string detail)
        {
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                {
                    try { File.Copy(LogPath, LogPath + ".old", true); File.Delete(LogPath); } catch { }
                }
                File.AppendAllText(LogPath,
                    string.Format("[{0}] {1}: {2}{3}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), action, detail, Environment.NewLine));
            }
            catch { }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);

        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        // ============================================================
        // 标准Windows命令行解析（解决"C:\Program Files\..."不带引号时被空格截断的bug）
        // ============================================================
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>
        /// 解析命令行，正确处理带空格的路径（无论是否带引号）。
        /// 只负责解析，不检查文件是否存在（文件检查由调用方负责）。
        /// 解析成功返回true，fileName为程序路径，arguments为剩余参数。
        /// </summary>
        private static bool TryParseCommandLine(string cmd, out string fileName, out string arguments)
        {
            fileName = "";
            arguments = "";
            if (string.IsNullOrWhiteSpace(cmd)) return false;

            cmd = cmd.Trim();

            // 第一步：用Windows标准API解析
            int argc;
            IntPtr argv = CommandLineToArgvW(cmd, out argc);
            if (argv != IntPtr.Zero && argc > 0)
            {
                try
                {
                    IntPtr pArg = Marshal.ReadIntPtr(argv);
                    var parsedName = Marshal.PtrToStringUni(pArg) ?? "";
                    if (!string.IsNullOrEmpty(parsedName))
                    {
                        fileName = parsedName;
                        if (argc > 1)
                        {
                            var args = new List<string>();
                            for (int i = 1; i < argc; i++)
                            {
                                pArg = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                                args.Add(Marshal.PtrToStringUni(pArg) ?? "");
                            }
                            arguments = string.Join(" ", args);
                        }
                        // 关键：CommandLineToArgvW 对不带引号的含空格路径（如 "C:\Program Files\...\uninst.exe /S"）
                        // 会按空格拆成 "C:\Program"，解析"成功"但文件不存在——必须验证文件存在（或msiexec），
                        // 否则继续走第二步 .exe 启发式回退，避免 c:\program 截断 bug
                        if (File.Exists(fileName) ||
                            fileName.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                finally
                {
                    LocalFree(argv);
                }
            }
            if (argv != IntPtr.Zero) LocalFree(argv);

            // 第二步：启发式回退——找最后一个可执行扩展名的位置
            // 注册表 UninstallString 可能不带引号（如 "C:\Program Files\...\uninst.exe /S"），
            // CommandLineToArgvW 偶尔会解析失败，这里从最后一个 .exe/.com/.bat/.cmd 往回找路径边界。
            var exts = new[] { ".exe", ".com", ".bat", ".cmd", ".msi" };
            foreach (var ext in exts)
            {
                var idx = cmd.LastIndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    fileName = cmd.Substring(0, idx + ext.Length).Trim('"', ' ');
                    arguments = cmd.Substring(idx + ext.Length).TrimStart();
                    return !string.IsNullOrEmpty(fileName);
                }
            }

            // 第三步：最简回退——按第一个空格分割
            var space = cmd.IndexOf(' ');
            if (space > 0)
            {
                fileName = cmd.Substring(0, space);
                arguments = cmd.Substring(space + 1);
            }
            else
            {
                fileName = cmd;
            }
            return !string.IsNullOrEmpty(fileName);
        }

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
                    case "已安装软件":
                        return CleanInstalledApp(item);
                    default:
                        return "跳过：未知类型";
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
            string stopError = null;

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
            catch (Exception ex)
            {
                stopError = ex.Message;
            }

            // 停服后可能还有托盘/守护进程占用 exe，先杀掉同名进程，避免文件占用删不掉（与 CleanStartup 一致）
            bool processKilled = true;
            if (!exeProtected && !string.IsNullOrEmpty(exePath))
            {
                processKilled = TryKillProcessByPath(exePath);
            }

            int scExitCode = 0;
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
                    if (proc.HasExited) scExitCode = proc.ExitCode;
                }
            }
            catch (Exception ex)
            {
                return $"失败: 删除服务时出错 {ex.Message}";
            }

            // 1072 = 服务已标记为删除：服务仍在停止中，重启后自动消失，算成功不算失败
            // 1060 = 服务不存在：已被自带卸载程序删除，算成功
            if (scExitCode != 0 && scExitCode != 1072 && scExitCode != 1060)
            {
                // 服务停止失败 + 删除失败 = 很可能有自我保护
                if (!stopped || !processKilled)
                {
                    return $"失败: 该服务可能有自我保护，普通权限无法停止/删除。请先在软件设置中关闭自我保护，或重启到安全模式后再清理（错误码 {scExitCode}）";
                }
                return $"失败: sc.exe 返回错误码 {scExitCode}";
            }

            // 服务已被自带卸载程序删除
            if (scExitCode == 1060)
            {
                _deletedServices.Add(serviceName);
                return "服务已被自带卸载程序删除";
            }

            if (exeProtected)
            {
                _deletedServices.Add(serviceName);
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

            // 服务标记删除了但进程没杀掉，提示重启
            _deletedServices.Add(serviceName);
            if (!processKilled && !string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                return "服务已停止并标记删除（重启后彻底消失）";
            }

            return "服务已停止并标记删除（重启后彻底消失）";
        }

        private static string CleanStartup(ScanResult item)
        {
            var exePath = ExtractExePath(item.Path);
            bool exeProtected = IsProtectedPath(exePath);

            bool regDeleted = true;
            try
            {
                if (item.Type == "启动项" || item.Type == "启动项(一次性)")
                {
                    if (!DeleteRunValue(Registry.CurrentUser, item)) regDeleted = false;
                    if (!DeleteRunValue(Registry.CurrentUser, item, @"Software\Microsoft\Windows\CurrentVersion\RunOnce")) regDeleted = false;
                }
                else if (item.Type == "启动项(系统)" || item.Type == "启动项(系统/一次性)")
                {
                    if (!DeleteRunValue(Registry.LocalMachine, item)) regDeleted = false;
                    if (!DeleteRunValue(Registry.LocalMachine, item, @"Software\Microsoft\Windows\CurrentVersion\RunOnce")) regDeleted = false;
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
            catch { regDeleted = false; }

            if (!exeProtected && !string.IsNullOrEmpty(exePath))
            {
                TryKillProcessByPath(exePath);
            }

            if (!regDeleted)
            {
                return "启动项删除失败（可能被软件保护或权限不足），请重启后再试";
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

        private static bool TryKillProcessByPath(string exePath)
        {
            bool killed = true;
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
                            try
                            {
                                proc.Kill();
                            }
                            catch
                            {
                                killed = false; // 进程有自我保护，杀不掉
                            }
                        }
                    }
                    catch { }
                    finally { try { proc.Dispose(); } catch { } }
                }
            }
            catch { }
            return killed;
        }

        private static bool DeleteRunValue(RegistryKey root, ScanResult item, string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
        {
            try
            {
                using (var key = root.OpenSubKey(runPath, true))
                {
                    if (key == null) return false;
                    if (key.GetValue(item.Name) == null) return true; // 值已不存在 = 已删除
                    BackupManager.BackupRegistryValue(root, runPath, item.Name, item.Type, item.Name);
                    key.DeleteValue(item.Name, false);
                    return key.GetValue(item.Name) == null; // 删除后验证
                }
            }
            catch { return false; }
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


        // ============================================================
        // SeTakeOwnershipPrivilege：夺取注册表所有权前必须先启用该特权，
        // 否则 OpenSubKey(TakeOwnership) 会因权限不足直接失败（360锁定的键）。
        // ============================================================
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string systemName, string name, out long luid);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public long Luid;
            public uint Attributes;
        }

        private static void EnableTakeOwnershipPrivilege()
        {
            try
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token)) return;
                try
                {
                    long luid;
                    if (!LookupPrivilegeValue(null, "SeTakeOwnershipPrivilege", out luid)) return;
                    var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                    AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            catch
            {
                // 启用特权失败（非管理员等），后续操作会返回明确错误
            }
        }

        private static string CleanBrowser(ScanResult item)
        {
            try
            {
                // IE 主页：360等软件会锁定注册表权限，先尝试夺取权限再修改
                TryTakeRegistryOwnership(Registry.CurrentUser, @"Software\Microsoft\Internet Explorer\Main");
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
                // 360等软件会锁定策略注册表项权限，先夺取权限
                TryTakeRegistryOwnership(root, policyPath);
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
                return "失效快捷方式已删除，请从开始菜单重新创建";
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

                // 第一步：杀目录下的所有进程
                bool allKilled = TryKillProcessesInDirectory(item.Path);

                // 第二步：根据目录名关键词杀相关进程（占用目录的进程可能不在该目录下，如2345Pic的主程序在Program Files）
                bool keywordKilled = TryKillProcessesByNameKeyword(item.Name, item.Path);
                if (!keywordKilled) allKilled = false;

                BackupManager.BackupDirectory(item.Path, item.Type, item.Name);

                TryDeleteDirectory(item.Path);

                if (Directory.Exists(item.Path))
                {
                    if (!allKilled)
                        return "目录被占用且进程无法结束（可能有自我保护），已标记为重启后删除";
                    return "目录被占用，已标记为重启后删除";
                }
                return "目录已删除";
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

        /// <summary>
        /// 根据目录名/项目名关键词杀相关进程。
        /// 例如目录名"2345Pic"→杀所有进程名包含"2345"的进程（主程序可能在Program Files下）。
        /// 只杀匹配特征库黑名单的进程，避免误杀。
        /// </summary>
        private static bool TryKillProcessesByNameKeyword(string itemName, string directoryPath)
        {
            bool allKilled = true;
            try
            {
                if (string.IsNullOrEmpty(itemName) && string.IsNullOrEmpty(directoryPath)) return true;

                // 从目录名和项目名中提取关键词（去掉常见后缀，取前6个字符）
                var dirName = Path.GetFileName(directoryPath?.TrimEnd('\\')) ?? "";
                var combined = (itemName + " " + dirName).ToLower();

                // 提取有意义的关键词（长度>=3，去掉数字开头的纯版本号）
                var keywords = new List<string>();
                foreach (var part in combined.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.Length >= 3 && !System.Text.RegularExpressions.Regex.IsMatch(part, @"^\d+$"))
                        keywords.Add(part);
                }
                // 也用完整目录名作为关键词
                if (dirName.Length >= 3) keywords.Add(dirName.ToLower());

                if (keywords.Count == 0) return true;

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var procName = proc.ProcessName.ToLower();
                        // 进程名匹配任一关键词
                        bool match = false;
                        foreach (var kw in keywords)
                        {
                            if (procName.Contains(kw)) { match = true; break; }
                        }
                        if (!match) continue;

                        // 只杀匹配黑名单的进程（避免误杀）
                        var exePath = "";
                        try { exePath = proc.MainModule?.FileName ?? ""; } catch { }
                        if (!Scanner.IsBadSoftwareForProcess(procName, exePath)) continue;

                        try
                        {
                            proc.Kill();
                        }
                        catch
                        {
                            allKilled = false; // 进程有自我保护
                        }
                    }
                    catch { }
                    finally { try { proc.Dispose(); } catch { } }
                }
            }
            catch { }
            return allKilled;
        }

        private static bool TryKillProcessesInDirectory(string path)
        {
            bool allKilled = true;
            try
            {
                if (IsProtectedPath(path)) return true;

                var prefix = Path.GetFullPath(path).TrimEnd('\\') + "\\";

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var exe = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exe) &&
                            exe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                proc.Kill();
                            }
                            catch
                            {
                                allKilled = false; // 进程有自我保护
                            }
                        }
                    }
                    catch { }
                    finally { try { proc.Dispose(); } catch { } }
                }
            }
            catch { }
            return allKilled;
        }

        // ============================================================
        // 已安装软件清理（调用自带卸载程序，与Geek/BCU同源）
        // ============================================================

        /// <summary>
        /// 清理"已安装软件"类型项：调用软件自带卸载程序。
        /// - 有驱动保护的软件（360/金山/腾讯管家等）：PreCleanNativeUninstall已弹GUI卸载向导，
        ///   这里检查注册表项是否还在，不在则算成功，还在则提示用户完成卸载向导。
        /// - 无驱动保护的软件（2345好压等）：调用静默卸载。
        /// </summary>
        private static string CleanInstalledApp(ScanResult item)
        {
            try
            {
                // 获取UninstallString（优先用扫描时保存的，没有则从注册表重新查）
                var uninstallCmd = item.UninstallString;
                if (string.IsNullOrEmpty(uninstallCmd))
                {
                    uninstallCmd = FindUninstallStringByName(item.Name);
                }

                if (string.IsNullOrEmpty(uninstallCmd))
                {
                    // 注册表中已找不到卸载信息：若 Uninstall 项也不在 → 已卸载；否则说明从未找到卸载程序
                    if (!UninstallEntryExists(item.Name))
                    {
                        return "已通过自带卸载程序卸载";
                    }
                    return "失败: 未找到自带卸载程序，请手动在控制面板卸载，或重启后重试";
                }

                // 判断是否有驱动保护
                var family = GetSelfProtectedFamily(item);

                if (family != null)
                {
                    // 有驱动保护：PreCleanNativeUninstall已弹GUI卸载向导
                    // 检查注册表Uninstall项是否还在
                    if (!UninstallEntryExists(item.Name))
                    {
                        return "已通过自带卸载程序卸载";
                    }
                    // 注册表项还在 → 用户可能取消了卸载，或卸载未完成
                    return "失败: 请重新清理并在弹出的卸载向导中完成卸载（驱动保护软件需用自带卸载程序关闭保护）";
                }

                // 无驱动保护：先尝试静默卸载，失败则强制删除
                string error;
                if (TrySilentUninstall(uninstallCmd, out error))
                {
                    return "已通过自带卸载程序卸载";
                }

                // 静默卸载失败（可能是卸载程序文件不存在/损坏），尝试强制删除
                // 解析卸载命令获取安装目录
                string uninstPath, uninstArgs;
                if (TryParseCommandLine(uninstallCmd, out uninstPath, out uninstArgs))
                {
                    var installDir = "";
                    try { installDir = Path.GetDirectoryName(uninstPath); } catch { }

                    // 优先用扫描结果中的Path（InstallLocation），其次用卸载程序所在目录
                    var dirToDelete = !string.IsNullOrEmpty(item.Path) && Directory.Exists(item.Path)
                        ? item.Path
                        : (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir) ? installDir : "");

                    bool dirDeleted = false;
                    if (!string.IsNullOrEmpty(dirToDelete) && !IsProtectedPath(dirToDelete))
                    {
                        try
                        {
                            // 先杀目录下的进程
                            TryKillProcessesInDirectory(dirToDelete);
                            TryKillProcessesByNameKeyword(item.Name, dirToDelete);
                            // 删除目录（被占用的标记重启删除）
                            TryDeleteDirectory(dirToDelete);
                            dirDeleted = !Directory.Exists(dirToDelete);
                        }
                        catch { }
                    }

                    // 删除注册表Uninstall项
                    bool regDeleted = RemoveUninstallEntry(item.Name);

                    if (dirDeleted || regDeleted)
                    {
                        return $"已强制删除（{error}）";
                    }
                }

                return $"失败：自带卸载程序执行失败（{error}），请手动在控制面板卸载";
            }
            catch (Exception ex)
            {
                return $"失败：{ex.Message}";
            }
        }

        /// <summary>
        /// 根据软件名在注册表Uninstall键中查找UninstallString。
        /// </summary>
        private static string FindUninstallStringByName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return null;

            var uninstallPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var path in uninstallPaths)
                {
                    try
                    {
                        using (var key = root.OpenSubKey(path))
                        {
                            if (key == null) continue;
                            foreach (var subKeyName in key.GetSubKeyNames())
                            {
                                try
                                {
                                    using (var subKey = key.OpenSubKey(subKeyName))
                                    {
                                        if (subKey == null) continue;
                                        var name = subKey.GetValue("DisplayName") as string;
                                        if (string.IsNullOrEmpty(name)) continue;
                                        if (string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            var normal = subKey.GetValue("UninstallString") as string;
                                            var quiet = subKey.GetValue("QuietUninstallString") as string;
                                            return !string.IsNullOrEmpty(normal) ? normal : quiet;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// 检查注册表Uninstall键中是否还存在指定软件名。
        /// </summary>
        private static bool UninstallEntryExists(string displayName)
        {
            return !string.IsNullOrEmpty(FindUninstallStringByName(displayName));
        }

        /// <summary>
        /// 从注册表Uninstall键中删除指定软件的卸载信息（卸载程序不存在时的兜底清理）。
        /// </summary>
        private static bool RemoveUninstallEntry(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return false;

            var uninstallPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var path in uninstallPaths)
                {
                    try
                    {
                        using (var key = root.OpenSubKey(path, true))
                        {
                            if (key == null) continue;
                            foreach (var subKeyName in key.GetSubKeyNames())
                            {
                                try
                                {
                                    using (var subKey = key.OpenSubKey(subKeyName))
                                    {
                                        if (subKey == null) continue;
                                        var name = subKey.GetValue("DisplayName") as string;
                                        if (string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            key.DeleteSubKeyTree(subKeyName, false);
                                            return true;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            return false;
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

        // ============================================================
        // 智能卸载：优先调用软件自带卸载程序（对付360等有自我保护的软件）
        // ============================================================

        /// <summary>有内核驱动级自我保护的软件家族定义。</summary>
        private class ProtectedFamily
        {
            public string Key;        // 去重用唯一键
            public string Display;    // 显示给用户的名称
            public string[] Keywords; // 搜索注册表Uninstall用的关键词列表（依次尝试）
            public string[] ExeNames; // 备用：常见卸载程序文件名（注册表找不到时直接搜路径）
        }

        private static readonly ProtectedFamily[] SelfProtectedFamilies = {
            new ProtectedFamily {
                Key="360", Display="360安全卫士",
                Keywords=new[]{"360安全卫士","360safe","360安全中心","360总控台","360tray","zhudongfangyu","360ws","360leakfix"},
                ExeNames=new[]{"uninst.exe","uninstall.exe","360uninst.exe"}
            },
            new ProtectedFamily {
                Key="金山毒霸", Display="金山毒霸",
                Keywords=new[]{"金山毒霸","kingsoft antivirus","kingsoft","duba","kxescore","kwsprotect","kxsengine","kavstart"},
                ExeNames=new[]{"uninst.exe","uninstall.exe","kxuninst.exe"}
            },
            new ProtectedFamily {
                Key="腾讯电脑管家", Display="腾讯电脑管家",
                Keywords=new[]{"腾讯电脑管家","qqpcmgr","qqpcrtp","qqcrtp","tencent pc manager"},
                ExeNames=new[]{"uninst.exe","uninstall.exe","QQPCRTP.exe"}
            },
            new ProtectedFamily {
                Key="2345安全卫士", Display="2345安全卫士",
                Keywords=new[]{"2345安全卫士","2345safe","2345安全中心"},
                ExeNames=new[]{"uninst.exe","uninstall.exe"}
            },
            new ProtectedFamily {
                Key="鲁大师", Display="鲁大师",
                Keywords=new[]{"鲁大师","ludashi","lnms"},
                ExeNames=new[]{"uninst.exe","uninstall.exe"}
            },
            new ProtectedFamily {
                Key="瑞星", Display="瑞星杀毒",
                Keywords=new[]{"瑞星","rising","ravmond","rsagent","rsmain","rising antivirus"},
                ExeNames=new[]{"uninst.exe","uninstall.exe"}
            },
            new ProtectedFamily {
                Key="江民", Display="江民杀毒",
                Keywords=new[]{"江民","jiangmin","kvmonxp","kvmobxp","jiangmin antivirus"},
                ExeNames=new[]{"uninst.exe","uninstall.exe"}
            },
            new ProtectedFamily {
                Key="卡巴斯基", Display="卡巴斯基",
                Keywords=new[]{"卡巴斯基","kaspersky","avp","kaspersky antivirus"},
                ExeNames=new[]{"uninst.exe","uninstall.exe"}
            },
            new ProtectedFamily {
                Key="McAfee", Display="McAfee",
                Keywords=new[]{"mcafee","mcshield","mcsysmon","迈克菲","mcafee antivirus"},
                ExeNames=new[]{"uninst.exe","uninstall.exe"}
            },
            new ProtectedFamily {
                Key="Norton", Display="Norton",
                Keywords=new[]{"norton","ccsvchst","诺顿","norton antivirus"},
                ExeNames=new[]{"uninst.exe","uninstall.exe"}
            }
        };

        /// <summary>
        /// 从扫描项中识别有自我保护的软件家族。
        /// 匹配扫描项的名称/路径/描述/服务名中是否包含家族的任一特征词。
        /// </summary>
        private static ProtectedFamily GetSelfProtectedFamily(ScanResult item)
        {
            if (item == null) return null;
            var haystack = (item.Name + " " + item.Path + " " + item.Description + " " + item.ServiceName).ToLower();
            foreach (var family in SelfProtectedFamilies)
            {
                // 用家族的所有关键词检查匹配（任一命中即属于该家族）
                foreach (var kw in family.Keywords)
                {
                    if (haystack.Contains(kw.ToLower()))
                        return family;
                }
            }
            return null;
        }

        /// <summary>
        /// 清理前自动调用有自我保护软件的自带卸载程序。
        /// 对每个不同的软件家族只调用一次卸载程序，避免重复弹窗。
        /// 返回执行过的卸载操作摘要列表（key=家族显示名，value=结果描述）。
        /// </summary>
        public static List<KeyValuePair<string, string>> PreCleanNativeUninstall(List<ScanResult> items)
        {
            var results = new List<KeyValuePair<string, string>>();
            if (items == null || items.Count == 0) return results;

            // 收集所有有自我保护的软件家族（按Key去重）
            var families = new Dictionary<string, ProtectedFamily>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var family = GetSelfProtectedFamily(item);
                if (family != null && !families.ContainsKey(family.Key))
                    families[family.Key] = family;
            }

            // 对每个家族，查找并运行自带卸载程序
            foreach (var kv in families)
            {
                var family = kv.Value;
                try
                {
                    // 第一步：用多关键词在注册表搜索卸载命令
                    var cmd = FindUninstallString(family.Keywords);

                    // 第二步：注册表找不到时，在常见安装目录搜索卸载程序
                    if (string.IsNullOrEmpty(cmd))
                    {
                        cmd = FindUninstallerByScanning(family);
                    }

                    if (string.IsNullOrEmpty(cmd))
                    {
                        results.Add(new KeyValuePair<string, string>(family.Display,
                            "未找到自带卸载程序（注册表和安装目录均未找到），将尝试强制删除"));
                        continue;
                    }

                    // 第三步：运行卸载程序，获取具体结果
                    string error;
                    var exitCode = RunNativeUninstaller(cmd, out error);

                    if (exitCode == -1)
                        results.Add(new KeyValuePair<string, string>(family.Display,
                            $"自带卸载程序启动失败：{error}，将尝试强制删除"));
                    else if (exitCode == -2)
                        results.Add(new KeyValuePair<string, string>(family.Display,
                            "自带卸载程序超时，将尝试强制删除"));
                    else if (exitCode == 0 || exitCode == 2 || exitCode == 3010)
                        // 0=成功, 2=成功需重启(金山/360等), 3010=MSI成功需重启
                        results.Add(new KeyValuePair<string, string>(family.Display,
                            "自带卸载程序已完成，正在清理残留..."));
                    else
                        results.Add(new KeyValuePair<string, string>(family.Display,
                            $"自带卸载程序未完成（代码{exitCode}），将尝试强制删除"));
                }
                catch (Exception ex)
                {
                    results.Add(new KeyValuePair<string, string>(family.Display,
                        $"自带卸载程序异常：{ex.Message}，将尝试强制删除"));
                }
            }

            return results;
        }

        /// <summary>
        /// 在目录树中递归搜索卸载程序（限制深度，避免全盘扫描）。
        /// </summary>
        private static string SearchUninstallInTree(string rootDir, string[] exeNames, int maxDepth)
        {
            if (maxDepth <= 0 || !Directory.Exists(rootDir)) return null;
            try
            {
                foreach (var exeName in exeNames)
                {
                    var candidate = Path.Combine(rootDir, exeName);
                    if (File.Exists(candidate))
                        return "\"" + candidate + "\"";
                }
                foreach (var subDir in Directory.GetDirectories(rootDir))
                {
                    var found = SearchUninstallInTree(subDir, exeNames, maxDepth - 1);
                    if (found != null) return found;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 根据多个关键词在注册表中查找自带卸载命令。
        /// 关键词按顺序尝试，找到第一个匹配的即返回。
        /// 搜索 HKLM/HKCU 的 Uninstall 键，匹配 DisplayName。
        /// </summary>
        private static string FindUninstallString(string[] keywords)
        {
            if (keywords == null || keywords.Length == 0) return null;

            var uninstallPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            // 按关键词顺序搜索，每个关键词在所有注册表路径中搜索
            foreach (var keyword in keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword)) continue;

                foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    foreach (var path in uninstallPaths)
                    {
                        try
                        {
                            using (var key = root.OpenSubKey(path))
                            {
                                if (key == null) continue;
                                foreach (var subKeyName in key.GetSubKeyNames())
                                {
                                    try
                                    {
                                        using (var subKey = key.OpenSubKey(subKeyName))
                                        {
                                            if (subKey == null) continue;
                                            var displayName = subKey.GetValue("DisplayName") as string;
                                            if (string.IsNullOrEmpty(displayName)) continue;

                                            // 关键词匹配（包含关系，不区分大小写）
                                            if (displayName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                                                continue;

                                            // 优先使用 UninstallString（GUI卸载向导），不用 QuietUninstallString
                                            var normal = subKey.GetValue("UninstallString") as string;
                                            var quiet = subKey.GetValue("QuietUninstallString") as string;
                                            var cmd = !string.IsNullOrEmpty(normal) ? normal : quiet;
                                            if (!string.IsNullOrEmpty(cmd)) return cmd;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 备用方案：注册表找不到卸载命令时，在常见安装目录直接搜索卸载程序。
        /// 搜索 Program Files / Program Files (x86) / AppData 下匹配家族关键词的目录，
        /// 在其中查找 uninst.exe / uninstall.exe 等常见卸载程序。
        /// </summary>
        private static string FindUninstallerByScanning(ProtectedFamily family)
        {
            if (family == null || family.ExeNames == null || family.ExeNames.Length == 0) return null;

            var searchRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            };

            // 用所有关键词匹配目录名（任一命中即可），解决英文/缩写目录名匹配不到的问题
            var keywords = family.Keywords != null && family.Keywords.Length > 0
                ? family.Keywords.Select(k => k.ToLower()).ToArray()
                : new[] { family.Key.ToLower() };

            foreach (var root in searchRoots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        var dirName = Path.GetFileName(dir).ToLower();
                        bool hit = false;
                        foreach (var kw in keywords)
                        {
                            if (dirName.Contains(kw)) { hit = true; break; }
                        }
                        if (!hit) continue;

                        // 递归搜索最多3层（如 Program Files(x86)\360\360safe\），避免安装路径层级过深
                        var found = SearchUninstallInTree(dir, family.ExeNames, 3);
                        if (found != null) return found;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 执行自带卸载程序，等待用户完成卸载向导。
        /// 返回值：-1=启动失败，-2=超时，>=0=程序退出码。
        /// error 输出具体的错误描述（启动失败时有效）。
        /// </summary>
        /// <summary>
        /// 安装器类型识别（借鉴 BCU 的 UninstallerType 思路，按国内常见安装器实现）。
        /// 不同安装器的卸载命令与静默参数不同，识别后能更准确地处理。
        /// </summary>
        public enum UninstallerType
        {
            Unknown,
            Msiexec,
            InnoSetup,    // unins000.exe，2345系列/驱动精灵/鲁大师等常用
            Nsis,         // uninstall.exe /S，国内软件打包常用
            InstallShield // setup.exe 的卸载变体
        }

        private static UninstallerType DetectUninstallerType(string fileName, string arguments)
        {
            if (fileName.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                return UninstallerType.Msiexec;

            var fn = Path.GetFileName(fileName).ToLower();
            if (fn.StartsWith("unins") && fn.EndsWith(".exe")) // unins000.exe
                return UninstallerType.InnoSetup;
            if (fn == "uninstall.exe" || fn == "uninst.exe")
                return UninstallerType.Nsis;
            if (fn == "setup.exe" || fn == "unsetup.exe" || fn.Contains("installshield") || fn.Contains("ishield"))
                return UninstallerType.InstallShield;
            return UninstallerType.Unknown;
        }

        /// <summary>
        /// 按安装器类型构建静默卸载参数。
        /// 注意：有驱动保护的软件（360/金山/腾讯管家等）必须用 GUI 向导卸载（RunNativeUninstaller），
        /// 静默卸载可能不关闭驱动保护导致残留。此方法供无驱动保护软件的静默卸载使用。
        /// </summary>
        private static string BuildSilentArguments(UninstallerType type, string arguments)
        {
            switch (type)
            {
                case UninstallerType.Msiexec:
                    arguments = System.Text.RegularExpressions.Regex.Replace(arguments, @"/[iI]{", "/X{");
                    if (!arguments.Contains("/quiet") && !arguments.Contains("/qn"))
                        arguments += " /quiet";
                    break;
                case UninstallerType.InnoSetup:
                    if (!arguments.ToLower().Contains("/verysilent"))
                        arguments += " /VERYSILENT /NORESTART /SUPPRESSMSGBOXES";
                    break;
                case UninstallerType.Nsis:
                    var trimmed = arguments.Trim();
                    if (!trimmed.Equals("/S", StringComparison.OrdinalIgnoreCase) && !trimmed.Contains(" /S "))
                        arguments += " /S";
                    break;
                case UninstallerType.InstallShield:
                    if (!arguments.Contains("/silent"))
                        arguments += " /silent";
                    break;
            }
            return arguments;
        }

        /// <summary>
        /// 静默卸载（无驱动保护软件适用）：识别安装器类型并追加静默参数，等待完成。
        /// 返回是否成功；失败时 error 给出具体原因。
        /// </summary>
        public static bool TrySilentUninstall(string uninstallCommand, out string error)
        {
            error = "";
            try
            {
                // 用Windows标准API解析命令行
                string fileName;
                string arguments;
                if (!TryParseCommandLine(uninstallCommand, out fileName, out arguments))
                {
                    error = $"无法解析卸载命令：{uninstallCommand}";
                    return false;
                }

                var uType = DetectUninstallerType(fileName, arguments);
                arguments = BuildSilentArguments(uType, arguments);

                if (!File.Exists(fileName) && uType != UninstallerType.Msiexec)
                {
                    error = $"卸载程序文件不存在：{fileName}";
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) { error = "进程启动失败"; return false; }
                    if (!proc.WaitForExit(300000))
                    {
                        try { proc.Kill(); } catch { }
                        error = "静默卸载超时（5分钟）";
                        return false;
                    }
                    if (proc.ExitCode != 0)
                    {
                        error = $"静默卸载退出码：{proc.ExitCode}";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static int RunNativeUninstaller(string uninstallCommand, out string error)
        {
            error = "";
            try
            {
                // 用Windows标准API解析命令行，正确处理带空格的路径（无论是否带引号）
                string fileName;
                string arguments;
                if (!TryParseCommandLine(uninstallCommand, out fileName, out arguments))
                {
                    error = $"无法解析卸载命令：{uninstallCommand}";
                    return -1;
                }

                // 检查卸载程序文件是否存在
                if (!File.Exists(fileName) && !fileName.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"卸载程序文件不存在：{fileName}";
                    return -1;
                }

                // MsiExec 标准卸载：/I{GUID} 是维护模式（修复/修改），必须转为 /X{GUID} 卸载模式
                var uType = DetectUninstallerType(fileName, arguments);
                if (uType == UninstallerType.Msiexec)
                {
                    arguments = System.Text.RegularExpressions.Regex.Replace(arguments, @"/[iI]\{", "/X{");
                }

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true // 自带卸载程序需要ShellExecute保证权限提升
                };

                Process proc;
                try
                {
                    proc = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    error = $"启动失败：{ex.Message}（可能需要管理员权限或文件已损坏）";
                    return -1;
                }

                if (proc == null)
                {
                    error = "进程启动返回空";
                    return -1;
                }

                // 等待卸载程序退出（最多10分钟，用户可能需要手动点"下一步"）
                if (!proc.WaitForExit(600000))
                {
                    try { proc.Kill(); } catch { }
                    LogClean("自带卸载", "超时已终止，命令:" + uninstallCommand);
                    return -2; // 超时未完成
                }
                LogClean("自带卸载", "命令:" + uninstallCommand + " 解析:" + fileName + "|" + arguments + " 退出码:" + proc.ExitCode);
                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                error = $"异常：{ex.Message}";
                return -1;
            }
        }

        /// <summary>
        /// 智能卸载：查找并调用软件自带卸载程序。
        /// keyword 是用于匹配 DisplayName 的关键词（如 "360安全卫士"）。
        /// 返回 JSON 字符串，包含是否找到卸载程序、是否执行成功。
        /// </summary>
        public static string SmartUninstall(string keyword)
        {
            var serializer = new JavaScriptSerializer();
            try
            {
                var cmd = FindUninstallString(new[] { keyword });
                if (string.IsNullOrEmpty(cmd))
                {
                    return serializer.Serialize(new
                    {
                        success = false,
                        message = $"未找到「{keyword}」的自带卸载程序，将使用强制删除"
                    });
                }

                string error;
                var exitCode = RunNativeUninstaller(cmd, out error);
                if (exitCode == -1)
                {
                    return serializer.Serialize(new
                    {
                        success = false,
                        message = $"自带卸载程序启动失败：{error}，请手动在控制面板卸载"
                    });
                }
                if (exitCode == -2)
                {
                    return serializer.Serialize(new
                    {
                        success = false,
                        message = "自带卸载程序超时未完成，请手动在控制面板卸载"
                    });
                }
                if (exitCode != 0)
                {
                    return serializer.Serialize(new
                    {
                        success = false,
                        message = $"自带卸载程序已退出(代码{exitCode})，可能已取消，请手动在控制面板卸载"
                    });
                }

                return serializer.Serialize(new
                {
                    success = true,
                    message = $"已启动「{keyword}」自带卸载程序，请在弹出的窗口中完成卸载，完成后点击确定扫描残留"
                });
            }
            catch (Exception ex)
            {
                return serializer.Serialize(new { success = false, message = $"智能卸载出错: {ex.Message}" });
            }
        }

        /// <summary>
        /// 尝试夺取注册表项所有权并授予当前用户完全控制权限。
        /// 360等软件会锁定注册表ACL导致"未经授权的操作"，用这个方法突破。
        /// </summary>
        private static void TryTakeRegistryOwnership(RegistryKey root, string subKeyPath)
        {
            try
            {
                // 启用 SeTakeOwnershipPrivilege，否则 OpenSubKey(TakeOwnership) 会因权限不足直接失败
                EnableTakeOwnershipPrivilege();
                // 先以只读方式打开获取权限信息
                using (var key = root.OpenSubKey(subKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.TakeOwnership | RegistryRights.ChangePermissions))
                {
                    if (key == null) return;

                    var currentUser = WindowsIdentity.GetCurrent().User;
                    var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

                    // 夺取所有权
                    var ownership = new RegistrySecurity();
                    ownership.SetOwner(currentUser);
                    key.SetAccessControl(ownership);

                    // 授予完全控制权限
                    var access = new RegistrySecurity();
                    access.AddAccessRule(new RegistryAccessRule(
                        currentUser,
                        RegistryRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                    access.AddAccessRule(new RegistryAccessRule(
                        admins,
                        RegistryRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                    key.SetAccessControl(access);
                }
            }
            catch
            {
                // 夺取权限失败（可能需要更高权限），静默失败，后续操作会返回明确错误
            }
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
