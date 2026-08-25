using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;
using System.ServiceProcess;

namespace WindowsFormsApp1
{
    public class ScanResult
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Type { get; set; }
        public string RiskLevel { get; set; }
        public string Description { get; set; }
        public string TaskName { get; set; }
        public string ServiceName { get; set; }
        public string UninstallString { get; set; }  // 已安装软件类型专用：注册表UninstallString
        public bool Checked { get; set; } = true;
        public string Result { get; set; } = "";
    }

    public static class Scanner
    {
        // ============================================================
        // 检测服务是否已被标记为删除（sc delete 后重启前仍会出现在服务列表中）
        // ============================================================
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SERVICE_QUERY_CONFIG = 0x0001;
        private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

        /// <summary>
        /// 检查服务是否已被标记为删除（DeleteService 调用后、重启前，服务仍在列表中但实际已删除）。
        /// </summary>
        private static bool IsServiceMarkedForDelete(string serviceName)
        {
            try
            {
                IntPtr hSCM = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (hSCM == IntPtr.Zero) return false;
                try
                {
                    // 已标记删除的服务，用 SERVICE_QUERY_CONFIG 打开会返回 NULL + 错误码 1072
                    IntPtr hService = OpenService(hSCM, serviceName, SERVICE_QUERY_CONFIG);
                    if (hService == IntPtr.Zero)
                    {
                        return Marshal.GetLastWin32Error() == ERROR_SERVICE_MARKED_FOR_DELETE;
                    }
                    CloseServiceHandle(hService);
                    return false;
                }
                finally
                {
                    CloseServiceHandle(hSCM);
                }
            }
            catch { return false; }
        }
        public static List<ScanResult> ScanAll()
        {
            var results = new List<ScanResult>();
            results.AddRange(ScanStartup());
            results.AddRange(ScanServices());
            results.AddRange(ScanScheduledTasks());
            results.AddRange(ScanBrowser());
            results.AddRange(ScanBrowserShortcuts());
            results.AddRange(ScanContextMenu());
            results.AddRange(ScanAppData());
            results.AddRange(ScanInstallApps());
            return results;
        }

        // ============================================================
        // 启动项扫描（修复：增加 RunOnce、Startup 文件夹）
        // ============================================================
        public static List<ScanResult> ScanStartup()
        {
            var results = new List<ScanResult>();

            ScanRunKey(results, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "启动项");
            ScanRunKey(results, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "启动项(系统)");
            ScanRunKey(results, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "启动项(一次性)");
            ScanRunKey(results, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "启动项(系统/一次性)");
            ScanStartupFolder(results, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "启动项(文件夹)");
            ScanStartupFolder(results, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "启动项(系统/文件夹)");

            return results;
        }

        private static void ScanRunKey(List<ScanResult> results, RegistryKey root, string path, string type)
        {
            try
            {
                using (var key = root.OpenSubKey(path))
                {
                    if (key == null) return;
                    foreach (var name in key.GetValueNames())
                    {
                        var value = key.GetValue(name)?.ToString() ?? "";
                        if (IsBadSoftware(out string desc, out string risk, name, value))
                        {
                            results.Add(new ScanResult
                            {
                                Name = name,
                                Path = value,
                                Type = type,
                                RiskLevel = risk,
                                Description = desc
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private static void ScanStartupFolder(List<ScanResult> results, string folderPath, string type)
        {
            try
            {
                if (!Directory.Exists(folderPath)) return;
                foreach (var file in Directory.GetFiles(folderPath, "*.lnk"))
                {
                    var fileName = Path.GetFileName(file);
                    if (IsBadSoftware(out string desc, out string risk, fileName, file))
                    {
                        results.Add(new ScanResult
                        {
                            Name = fileName,
                            Path = file,
                            Type = type,
                            RiskLevel = risk,
                            Description = desc
                        });
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 扫描已安装程序列表（注册表 Uninstall 键，与 Geek/BCU 同源）。
        /// 只显示特征库匹配的问题软件，保持"只扫问题项"的定位。
        /// </summary>
        public static List<ScanResult> ScanInstallApps()
        {
            var results = new List<ScanResult>();
            var roots = new[]
            {
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (root == null) continue;
                try
                {
                    foreach (var subName in root.GetSubKeyNames())
                    {
                        try
                        {
                            using (var sub = root.OpenSubKey(subName))
                            {
                                if (sub == null) continue;
                                var displayName = sub.GetValue("DisplayName") as string;
                                if (string.IsNullOrEmpty(displayName) || seen.Contains(displayName)) continue;

                                var uninstallStr = sub.GetValue("UninstallString") as string ?? "";
                                var installLocation = sub.GetValue("InstallLocation") as string ?? "";
                                var path = !string.IsNullOrEmpty(installLocation)
                                    ? installLocation
                                    : ExtractExePathFromCmd(uninstallStr);

                                // 特征库匹配（key + 描述前段）才显示；白名单在 IsBadSoftware 内部已优先
                                if (IsBadSoftware(out string desc, out string risk, displayName, path, uninstallStr))
                                {
                                    seen.Add(displayName);
                                    results.Add(new ScanResult
                                    {
                                        Name = displayName,
                                        Path = path,
                                        Type = "已安装软件",
                                        RiskLevel = risk,
                                        Description = desc,
                                        UninstallString = uninstallStr
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                finally { root.Dispose(); }
            }
            return results;
        }

        /// <summary>
        /// 从卸载命令行中提取主程序路径（"C:\xx\uninst.exe" /S → C:\xx\uninst.exe）
        /// </summary>
        private static string ExtractExePathFromCmd(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                var end = cmd.IndexOf('"', 1);
                return end > 0 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
            }
            var sp = cmd.IndexOf(' ');
            return sp > 0 ? cmd.Substring(0, sp) : cmd;
        }

        public static List<ScanResult> ScanServices()
        {
            var results = new List<ScanResult>();
            try
            {
                foreach (ServiceController sc in ServiceController.GetServices())
                {
                    try
                    {
                        // 跳过已标记删除的服务（sc delete 后重启前仍在列表中，但实际已删除）
                        if (IsServiceMarkedForDelete(sc.ServiceName)) continue;
                        // 跳过本程序已成功删除的服务（比API检测更可靠）
                        if (Cleaner.IsServiceDeleted(sc.ServiceName)) continue;

                        var path = GetServicePath(sc.ServiceName);
                        if (IsBadSoftware(out string desc, out string risk, sc.ServiceName, sc.DisplayName, path))
                        {
                            results.Add(new ScanResult
                            {
                                Name = sc.DisplayName,
                                ServiceName = sc.ServiceName,
                                Path = path,
                                Type = "系统服务",
                                RiskLevel = risk,
                                Description = desc
                            });
                        }
                    }
                    catch { }
                    finally
                    {
                        try { sc.Dispose(); } catch { }
                    }
                }
            }
            catch { }
            return results;
        }

        // ============================================================
        // 计划任务扫描（修复：用 XmlDocument 替代 Regex）
        // ============================================================
        public static List<ScanResult> ScanScheduledTasks()
        {
            var results = new List<ScanResult>();
            var tasksRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");

            if (!Directory.Exists(tasksRoot)) return results;

            string[] files;
            try
            {
                files = Directory.GetFiles(tasksRoot, "*", SearchOption.AllDirectories);
            }
            catch { return results; }

            foreach (var file in files)
            {
                try
                {
                    var fileName = Path.GetFileName(file);
                    var command = ExtractTaskCommand(file);

                    if (IsBadSoftware(out string desc, out string risk, fileName, command))
                    {
                        results.Add(new ScanResult
                        {
                            Name = fileName,
                            Path = string.IsNullOrEmpty(command) ? file : command,
                            Type = "计划任务",
                            RiskLevel = risk,
                            Description = desc + "（会自动复活）",
                            TaskName = GetTaskFullName(tasksRoot, file)
                        });
                    }
                }
                catch { }
            }

            return results;
        }

        private static string ExtractTaskCommand(string filePath)
        {
            try
            {
                var xml = new XmlDocument();
                xml.Load(filePath);
                var nsmgr = new XmlNamespaceManager(xml.NameTable);
                nsmgr.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");

                var cmdNode = xml.SelectSingleNode("//t:Command", nsmgr)
                            ?? xml.SelectSingleNode("//Command");
                var argsNode = xml.SelectSingleNode("//t:Arguments", nsmgr)
                             ?? xml.SelectSingleNode("//Arguments");

                var result = cmdNode?.InnerText.Trim() ?? "";
                if (argsNode != null) result += " " + argsNode.InnerText.Trim();
                return result.Trim();
            }
            catch { return ""; }
        }

        private static string GetTaskFullName(string tasksRoot, string filePath)
        {
            try
            {
                var relative = filePath.Substring(tasksRoot.Length).TrimStart('\\');
                return "\\" + relative;
            }
            catch { return Path.GetFileName(filePath); }
        }

        // ============================================================
        // 浏览器设置扫描（修复：Preferences 只检查配置键值）
        // ============================================================
        public static List<ScanResult> ScanBrowser()
        {
            var results = new List<ScanResult>();

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\Main"))
                {
                    if (key != null)
                    {
                        var startPage = key.GetValue("Start Page")?.ToString() ?? "";
                        if (IsBadHomepage(startPage))
                        {
                            results.Add(new ScanResult
                            {
                                Name = "IE/系统主页被篡改",
                                Path = startPage,
                                Type = "浏览器设置",
                                RiskLevel = "中",
                                Description = "主页被锁定为已知劫持网址"
                            });
                        }
                    }
                }

                ScanBrowserPolicy(results, @"Software\Policies\Microsoft\Edge", "Edge");
                ScanBrowserPolicy(results, @"Software\Policies\Google\Chrome", "Chrome");
                ScanBrowserPolicy(results, @"Software\WOW6432Node\Policies\Microsoft\Edge", "Edge(32位)");
                ScanBrowserPolicy(results, @"Software\WOW6432Node\Policies\Google\Chrome", "Chrome(32位)");

                CheckPreferencesFile(results,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\Edge\User Data\Default\Preferences"), "Edge");

                CheckPreferencesFile(results,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Google\Chrome\User Data\Default\Preferences"), "Chrome");
            }
            catch { }

            return results;
        }

        private static void CheckPreferencesFile(List<ScanResult> results, string prefsPath, string browserName)
        {
            try
            {
                if (!File.Exists(prefsPath)) return;
                var content = File.ReadAllText(prefsPath);

                var configKeys = new[] {
                    "\"homepage\"",
                    "\"homepage_url\"",
                    "\"startup_urls\"",
                    "\"restore_on_startup\"",
                    "\"urls_to_restore_on_startup\""
                };

                bool found = false;
                foreach (var key in configKeys)
                {
                    int keyIndex = content.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                    if (keyIndex >= 0)
                    {
                        int valueStart = content.IndexOf('"', keyIndex + key.Length);
                        if (valueStart > 0)
                        {
                            int valueEnd = content.IndexOf('"', valueStart + 1);
                            if (valueEnd > valueStart)
                            {
                                var value = content.Substring(valueStart + 1, valueEnd - valueStart - 1);
                                if (ContainsBadHomepage(value))
                                {
                                    found = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (found)
                {
                    results.Add(new ScanResult
                    {
                        Name = $"{browserName}主页被篡改",
                        Path = prefsPath,
                        Type = "浏览器设置",
                        RiskLevel = "中",
                        Description = $"{browserName}配置文件中出现已知劫持网址"
                    });
                }
            }
            catch { }
        }

        // ============================================================
        // 快捷方式扫描（修复：用 IWshShortcut 解析 .lnk）
        // ============================================================
        public static List<ScanResult> ScanBrowserShortcuts()
        {
            var results = new List<ScanResult>();
            var shortcutPaths = new List<string>();

            try
            {
                shortcutPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                shortcutPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
                shortcutPaths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"));
                shortcutPaths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"));

                var taskbarPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (Directory.Exists(taskbarPath)) shortcutPaths.Add(taskbarPath);

                var browserKeywords = new[] { "edge", "chrome", "firefox", "浏览器", "opera", "vivaldi", "brave", "yandex" };

                foreach (var dir in shortcutPaths)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

                    string[] lnkFiles;
                    try { lnkFiles = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories); }
                    catch { continue; }

                    foreach (var lnk in lnkFiles)
                    {
                        try
                        {
                            var fileName = Path.GetFileNameWithoutExtension(lnk).ToLower();
                            if (!browserKeywords.Any(k => fileName.Contains(k)))
                                continue;

                            var target = GetShortcutTarget(lnk);
                            var args = GetShortcutArguments(lnk);
                            var fullCommand = target + " " + args;

                            if (ContainsBadHomepage(fullCommand))
                            {
                                results.Add(new ScanResult
                                {
                                    Name = Path.GetFileNameWithoutExtension(lnk) + " 快捷方式被篡改",
                                    Path = lnk,
                                    Type = "快捷方式劫持",
                                    RiskLevel = "高",
                                    Description = "快捷方式被添加了已知劫持网址参数"
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return results;
        }

        private static string GetShortcutTarget(string lnkPath)
        {
            try
            {
                Type t = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
                dynamic shell = Activator.CreateInstance(t);
                try
                {
                    var lnk = shell.CreateShortcut(lnkPath);
                    return lnk.TargetPath ?? "";
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                }
            }
            catch { return ""; }
        }

        private static string GetShortcutArguments(string lnkPath)
        {
            try
            {
                Type t = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
                dynamic shell = Activator.CreateInstance(t);
                try
                {
                    var lnk = shell.CreateShortcut(lnkPath);
                    return lnk.Arguments ?? "";
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                }
            }
            catch { return ""; }
        }

        // ============================================================
        // 右键菜单扫描（修复：增加 shell 子键）
        // ============================================================
        public static List<ScanResult> ScanContextMenu()
        {
            var results = new List<ScanResult>();

            var contextMenuPaths = new[]
            {
                @"*\shellex\ContextMenuHandlers",
                @"Directory\shellex\ContextMenuHandlers",
                @"Directory\Background\shellex\ContextMenuHandlers",
                @"Folder\shellex\ContextMenuHandlers",
                @"AllFilesystemObjects\shellex\ContextMenuHandlers"
            };

            var shellPaths = new[]
            {
                @"*\shell",
                @"Directory\shell",
                @"Directory\Background\shell",
                @"Drive\shell",
                @"DesktopBackground\shell"
            };

            foreach (var regPath in contextMenuPaths)
            {
                ScanContextMenuKey(results, Registry.CurrentUser, "HKCU", regPath);
                ScanContextMenuKey(results, Registry.LocalMachine, "HKLM", regPath);
            }

            foreach (var regPath in shellPaths)
            {
                ScanShellKey(results, Registry.CurrentUser, "HKCU", regPath);
                ScanShellKey(results, Registry.LocalMachine, "HKLM", regPath);
            }

            return results;
        }

        private static void ScanContextMenuKey(List<ScanResult> results, RegistryKey root, string rootLabel, string regPath)
        {
            try
            {
                using (var key = root.OpenSubKey(@"Software\Classes\" + regPath))
                {
                    if (key == null) return;
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        if (IsBadSoftware(out string desc, out string risk, subKeyName))
                        {
                            results.Add(new ScanResult
                            {
                                Name = subKeyName + " 右键菜单",
                                Path = rootLabel + @"\Software\Classes\" + regPath + @"\" + subKeyName,
                                Type = "右键菜单残留",
                                RiskLevel = risk,
                                Description = desc + "（右键菜单残留项）"
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private static void ScanShellKey(List<ScanResult> results, RegistryKey root, string rootLabel, string regPath)
        {
            try
            {
                using (var key = root.OpenSubKey(@"Software\Classes\" + regPath))
                {
                    if (key == null) return;
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using (var cmdKey = key.OpenSubKey(subKeyName + @"\command"))
                        {
                            var cmdValue = cmdKey?.GetValue("")?.ToString() ?? "";
                            if (IsBadSoftware(out string desc, out string risk, subKeyName, cmdValue))
                            {
                                results.Add(new ScanResult
                                {
                                    Name = subKeyName + " 右键菜单",
                                    Path = rootLabel + @"\Software\Classes\" + regPath + @"\" + subKeyName,
                                    Type = "右键菜单残留",
                                    RiskLevel = risk,
                                    Description = desc + "（右键菜单命令项）"
                                });
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public static List<ScanResult> ScanAppData()
        {
            var results = new List<ScanResult>();
            ScanDirectory(results, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            ScanDirectory(results, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            return results;
        }

        private static void ScanDirectory(List<ScanResult> results, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                foreach (var dir in Directory.GetDirectories(path))
                {
                    try
                    {
                        var dirName = new DirectoryInfo(dir).Name;
                        if (IsBadSoftware(out string desc, out string risk, dirName, dir))
                        {
                            results.Add(new ScanResult
                            {
                                Name = dirName,
                                Path = dir,
                                Type = "目录残留",
                                RiskLevel = risk,
                                Description = desc
                            });
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void ScanBrowserPolicy(List<ScanResult> results, string policyPath, string browserName)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(policyPath))
                {
                    CheckPolicyKey(key, results, browserName);
                }
                using (var keyMachine = Registry.LocalMachine.OpenSubKey(policyPath))
                {
                    CheckPolicyKey(keyMachine, results, browserName);
                }
            }
            catch { }
        }

        private static void CheckPolicyKey(RegistryKey key, List<ScanResult> results, string browserName)
        {
            if (key == null) return;
            try
            {
                var homepage = key.GetValue("HomepageLocation")?.ToString() ?? "";
                var startupUrls = key.GetValue("RestoreOnStartupURLs")?.ToString() ?? "";

                if (IsBadHomepage(homepage) || IsBadHomepage(startupUrls))
                {
                    results.Add(new ScanResult
                    {
                        Name = $"{browserName}组策略被篡改",
                        Path = key.Name,
                        Type = "浏览器设置",
                        RiskLevel = "高",
                        Description = $"{browserName}主页被组策略强制锁定为已知劫持网址"
                    });
                }
            }
            catch { }
        }

        // ============================================================
        // 核心判定逻辑（修复：按类型分配风险等级）
        // ============================================================

        /// <summary>
        /// 公共接口：判断进程名和路径是否匹配黑名单（用于清理时杀相关进程）。
        /// </summary>
        public static bool IsBadSoftwareForProcess(string processName, string exePath)
        {
            string desc, risk;
            return IsBadSoftware(out desc, out risk, processName, exePath);
        }

        private static bool IsBadSoftware(out string description, out string riskLevel, params string[] fields)
        {
            description = "";
            riskLevel = "中";

            if (fields == null || fields.Length == 0) return false;

            var haystack = new List<string>();
            foreach (var f in fields)
            {
                if (!string.IsNullOrEmpty(f)) haystack.Add(f.ToLower());
            }
            if (haystack.Count == 0) return false;

            int whiteMatchLen = 0;
            foreach (var white in Signatures.WhiteList)
            {
                var w = white.ToLower();
                foreach (var h in haystack)
                {
                    if (h.Contains(w))
                    {
                        if (w.Length > whiteMatchLen) whiteMatchLen = w.Length;
                        break;
                    }
                }
            }

            string bestDesc = null;
            string bestKeyword = null;
            int badMatchLen = 0;
            foreach (var kv in Signatures.BadKeywords)
            {
                var k = kv.Key.ToLower();
                // 描述前段（"2345好压，含广告弹窗" → "2345好压"）作为别名匹配，
                // 解决 key 是内部标识（如"2345zip"）、与显示名对不上的问题
                var descHead = kv.Value.Split(new[] { '，', ',' })[0].Trim().ToLower();
                if (descHead.Length < k.Length) descHead = k;
                if (descHead.Length <= badMatchLen) continue;
                foreach (var h in haystack)
                {
                    if (h.Contains(k) || h.Contains(descHead))
                    {
                        badMatchLen = descHead.Length;
                        bestDesc = kv.Value;
                        bestKeyword = kv.Key;
                        break;
                    }
                }
            }

            if (bestDesc != null && badMatchLen > whiteMatchLen)
            {
                description = bestDesc;
                riskLevel = CalculateRiskLevel(bestKeyword, bestDesc);
                return true;
            }
            return false;
        }

        private static string CalculateRiskLevel(string keyword, string description)
        {
            var highRiskFamilies = new[] { "360", "2345", "kingsoft", "duba", "liebao", "baiduan", "baidusd", "rising", "jiangmin" };
            foreach (var family in highRiskFamilies)
            {
                if (keyword.ToLower().Contains(family) || description.Contains(family))
                    return "高";
            }

            if (description.Contains("主页") || description.Contains("劫持") || description.Contains("锁定"))
                return "高";

            if (description.Contains("输入法") || description.Contains("input"))
                return "中";

            if (description.Contains("下载") || description.Contains("播放器") || description.Contains("影音"))
                return "中";

            return "高";
        }

        private static bool IsBadHomepage(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            var lowerUrl = url.ToLower();
            foreach (var bad in Signatures.BadHomepages)
            {
                if (lowerUrl.Contains(bad)) return true;
            }
            return false;
        }

        internal static bool ContainsBadHomepage(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            var lower = content.ToLower();
            foreach (var bad in Signatures.BadHomepages)
            {
                if (lower.Contains(bad)) return true;
            }
            return false;
        }

        private static string GetServicePath(string serviceName)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}"))
                {
                    return key?.GetValue("ImagePath")?.ToString() ?? "";
                }
            }
            catch { }
            return "";
        }
    }
}
