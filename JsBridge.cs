using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace WindowsFormsApp1
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public class JsBridge
    {
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly Form _mainForm;

        public JsBridge(Form mainForm)
        {
            _mainForm = mainForm;
        }

        /// <summary>
        /// 窗口最小化
        /// </summary>
        public void Minimize()
        {
            _mainForm.WindowState = FormWindowState.Minimized;
        }

        /// <summary>
        /// 窗口最大化/还原
        /// </summary>
        public void ToggleMaximize()
        {
            if (_mainForm.WindowState == FormWindowState.Maximized)
                _mainForm.WindowState = FormWindowState.Normal;
            else
                _mainForm.WindowState = FormWindowState.Maximized;
        }

        /// <summary>
        /// 关闭窗口
        /// </summary>
        public void CloseWindow()
        {
            _mainForm.Close();
        }

        /// <summary>
        /// 执行扫描，返回扫描结果JSON
        /// </summary>
        public async Task<string> Scan()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var results = Scanner.ScanAll();
                    // 按风险等级排序
                    results = results
                        .OrderByDescending(r => r.RiskLevel == "高" ? 2 : r.RiskLevel == "中" ? 1 : 0)
                        .ToList();
                    // 匿名遥测：扫描完成
                    TelemetryService.Report("scan");

                    return _json.Serialize(new { success = true, data = results });
                }
                catch (Exception ex)
                {
                    return _json.Serialize(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// 执行清理，传入选中的项目列表（按 Name + Path + Type 身份匹配，不再用索引，避免删错对象）。
        /// 前端调用格式：jsBridge.Clean(JSON.stringify(selectedItems))
        ///   selectedItems = [ { "name": "...", "path": "...", "type": "..." }, ... ]
        /// 字段名大小写不限；path 可省略（此时按 Name+Type 匹配），但建议三个都传，最安全。
        /// </summary>
        public async Task<string> Clean(string selectedItemsJson)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var selected = _json.Deserialize<List<Dictionary<string, object>>>(selectedItemsJson);
                    if (selected == null || selected.Count == 0)
                        return _json.Serialize(new { success = false, error = "没有选择要清理的项目" });

                    var allResults = Scanner.ScanAll()
                        .OrderByDescending(r => r.RiskLevel == "高" ? 2 : r.RiskLevel == "中" ? 1 : 0)
                        .ToList();

                    BackupManager.StartBackup();
                    int success = 0, fail = 0, skipped = 0, missing = 0;
                    var results = new List<object>();

                    // 第一步：找出有自我保护的软件（360/金山/鲁大师等），先自动调用自带卸载程序
                    var matchedItems = new List<ScanResult>();
                    foreach (var target in selected)
                    {
                        var name = GetTargetField(target, "name");
                        var path = GetTargetField(target, "path");
                        var type = GetTargetField(target, "type");
                        var item = FindScanResult(allResults, name, path, type);
                        if (item != null) matchedItems.Add(item);
                    }

                    var nativeResults = Cleaner.PreCleanNativeUninstall(matchedItems);

                    // 自带卸载程序可能已经删除/修改了部分项目，重新扫描获取最新状态
                    if (nativeResults.Count > 0)
                    {
                        allResults = Scanner.ScanAll()
                            .OrderByDescending(r => r.RiskLevel == "高" ? 2 : r.RiskLevel == "中" ? 1 : 0)
                            .ToList();
                    }

                    // 第二步：逐项强制清理残留
                    foreach (var target in selected)
                    {
                        var name = GetTargetField(target, "name");
                        var path = GetTargetField(target, "path");
                        var type = GetTargetField(target, "type");

                        var item = FindScanResult(allResults, name, path, type);

                        string result;
                        if (item == null)
                        {
                            // 重新扫描后没找到对应项目——可能已被自带卸载程序删掉了，算成功
                            missing++;
                            result = "未找到（可能已被自带卸载程序清理），已跳过";
                        }
                        else
                        {
                            result = Cleaner.Clean(item);
                            if (result.StartsWith("失败")) fail++;
                            else if (result.StartsWith("跳过")) skipped++;
                            else success++;
                        }

                        Cleaner.LogClean("清理", (type ?? "") + ":" + (name ?? "") + " -> " + result);
                        results.Add(new { name = name, path = path, type = type, result = result });
                    }

                    // 第三步：重扫后自动清理与本次软件匹配的残留（无需用户再勾选）
                    int autoCleaned = 0;
                    if (nativeResults.Count > 0)
                    {
                        var cleanKeywords = matchedItems
                            .Where(m => m != null && !string.IsNullOrEmpty(m.Name))
                            .Select(m => m.Name.Length > 8 ? m.Name.Substring(0, 8) : m.Name)
                            .ToList();
                        if (cleanKeywords.Count > 0)
                        {
                            allResults = Scanner.ScanAll()
                                .OrderByDescending(r => r.RiskLevel == "高" ? 2 : r.RiskLevel == "中" ? 1 : 0)
                                .ToList();
                            foreach (var r in allResults)
                            {
                                if (r.Type == "已安装软件") continue; // 软件本体需用户确认，不自动清
                                bool hit = false;
                                foreach (var kw in cleanKeywords)
                                {
                                    if ((r.Name ?? "").Contains(kw) || (r.Path ?? "").Contains(kw) || (r.Description ?? "").Contains(kw))
                                    {
                                        hit = true;
                                        break;
                                    }
                                }
                                if (!hit) continue;
                                var rs = Cleaner.Clean(r);
                                if (rs.StartsWith("失败")) fail++;
                                else if (rs.StartsWith("跳过")) skipped++;
                                else { success++; autoCleaned++; }
                            }
                            if (autoCleaned > 0)
                            {
                                nativeResults.Add(new KeyValuePair<string, string>("残留清理", "已自动清理残留 " + autoCleaned + " 项"));
                            }
                        }
                    }

                    Cleaner.LogClean("清理会话", string.Format("选中{0} 成功{1} 失败{2} 跳过{3} 自动清残留{4}", selected.Count, success, fail, skipped, autoCleaned));

                    BackupManager.SaveBackupLog();

                    // 匿名遥测：清理完成（只报成功数）
                    TelemetryService.Report("clean", success);

                    var nativeSummary = nativeResults.Count > 0
                        ? string.Join("；", nativeResults.Select(kv => $"「{kv.Key}」{kv.Value}"))
                        : "";

                    return _json.Serialize(new
                    {
                        success = true,
                        data = new { success, fail, skipped, missing, results, nativeUninstall = nativeSummary }
                    });
                }
                catch (Exception ex)
                {
                    return _json.Serialize(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// 智能卸载：调用软件自带卸载程序（对付360等有自我保护的软件）。
        /// 前端调用：jsBridge.SmartUninstall("360安全卫士")
        /// 会启动自带卸载向导，用户手动完成后，前端再触发扫描清理残留。
        /// </summary>
        public async Task<string> SmartUninstall(string keyword)
        {
            return await Task.Run(() => Cleaner.SmartUninstall(keyword ?? ""));
        }

        /// <summary>从前端传回的项目对象里取字段，键名大小写不敏感，null 安全。</summary>
        private static string GetTargetField(Dictionary<string, object> target, string key)
        {
            if (target == null) return "";
            foreach (var kv in target)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value as string ?? "";
            }
            return "";
        }

        /// <summary>
        /// 在最新扫描结果里按 Name + Path + Type 找项目。
        /// 只清理身份完全匹配的项；path 传了但匹配不上时宁可跳过，不误删。
        /// </summary>
        private static ScanResult FindScanResult(List<ScanResult> results, string name, string path, string type)
        {
            foreach (var r in results)
            {
                if (!string.Equals(r.Type ?? "", type ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(r.Name ?? "", name ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(path)) return r;
                if (string.Equals((r.Path ?? "").Trim(), path.Trim(), StringComparison.OrdinalIgnoreCase)) return r;
            }
            return null;
        }
        /// <summary>
        /// 撤销上次清理
        /// </summary>
        public string Undo()
        {
            try
            {
                var result = BackupManager.RestoreLastBackup();
                return _json.Serialize(new { success = true, message = result });
            }
            catch (Exception ex)
            {
                return _json.Serialize(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 获取弹窗统计数据
        /// </summary>
        public string GetPopupStats()
        {
            try
            {
                PopupCounter.DoCount(); // 先执行一次实时统计
                var today = PopupCounter.TotalToday;
                var total = PopupCounter.TotalAll;
                var week = 0; // 周统计暂未实现（前端已隐藏本周卡片）
                var stats = PopupCounter.Stats.ToDictionary(kv => kv.Key, kv => kv.Value);
                return _json.Serialize(new { success = true, today, week, total, stats });
            }
            catch (Exception ex)
            {
                return _json.Serialize(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 获取安装包监控统计数据
        /// </summary>
        public string GetInstallStats()
        {
            try
            {
                var today = ((MainForm)_mainForm).GetInstallTodayCount();
                var total = ((MainForm)_mainForm).GetInstallTotalCount();
                var week = 0; // 周统计暂未实现（前端已隐藏本周卡片）
                return _json.Serialize(new { success = true, today, week, total });
            }
            catch (Exception ex)
            {
                return _json.Serialize(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 获取版本号
        /// </summary>
        public string GetVersion()
        {
            return _json.Serialize(new { success = true, version = "v1.0" });
        }

        /// <summary>
        /// 获取开机自启状态
        /// </summary>
        public bool GetAutoStartStatus()
        {
            return AutoStartHelper.IsEnabled();
        }

        /// <summary>
        /// 设置开机自启
        /// </summary>
        public void SetAutoStartStatus(bool enabled)
        {
            AutoStartHelper.SetEnabled(enabled);
        }

        /// <summary>
        /// 获取悬浮球开关状态
        /// </summary>
        public bool GetFloatBallStatus()
        {
            return ((MainForm)_mainForm).GetFloatBallEnabled();
        }

        /// <summary>
        /// 设置悬浮球开关
        /// </summary>
        public void SetFloatBallStatus(bool enabled)
        {
            ((MainForm)_mainForm).SetFloatBallEnabled(enabled);
        }

        /// <summary>
        /// 获取设备唯一ID，首次运行生成并持久化
        /// </summary>
        public string GetDeviceId()
        {
            try
            {
                // 稳定目录（与版本号无关，升级不丢ID）
                string stableDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RogueCleaner");
                string configPath = Path.Combine(stableDir, "device_id.txt");
                if (File.Exists(configPath))
                {
                    string id = File.ReadAllText(configPath).Trim();
                    if (!string.IsNullOrWhiteSpace(id)) return id;
                }
                // 迁移旧路径（带版本号）中的设备ID
                string oldPath = Path.Combine(Application.LocalUserAppDataPath, "device_id.txt");
                if (File.Exists(oldPath))
                {
                    string oldId = File.ReadAllText(oldPath).Trim();
                    if (!string.IsNullOrWhiteSpace(oldId))
                    {
                        Directory.CreateDirectory(stableDir);
                        File.WriteAllText(configPath, oldId);
                        return oldId;
                    }
                }
                // 生成新ID
                string newId = Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(stableDir);
                File.WriteAllText(configPath, newId);
                return newId;
            }
            catch
            {
                // 出错就返回临时ID，不影响使用
                return Guid.NewGuid().ToString("N");
            }
        }

        /// <summary>
        /// 应用版本号
        /// </summary>
        public string GetAppVersion()
        {
            try { return Application.ProductVersion; }
            catch { return "0.0.0.0"; }
        }

        /// <summary>
        /// 操作系统版本
        /// </summary>
        public string GetOsVersion()
        {
            try { return Environment.OSVersion.VersionString; }
            catch { return "unknown"; }
        }

        /// <summary>
        /// 是否已同意用户协议。
        /// 特意不用 localStorage：WebView2 的用户数据目录当前配置在
        /// %TEMP%\VincelWebView 下（见 MainForm.InitWebView），那是系统认为
        /// "随时可以清空"的地方——用户手动清临时文件、或者以后为排查问题删这个缓存
        /// 文件夹，同意记录都会跟着消失，协议框会莫名其妙又弹出来。
        /// 这里改成和 device_id.txt、悬浮球配置一样，存在 LocalAppData 下的
        /// 普通文件里，和浏览器缓存彻底脱钩。
        /// </summary>
        public bool GetAgreementStatus()
        {
            try
            {
                // 稳定目录，升级不丢记录
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RogueCleaner", "agreement.txt");
                if (!File.Exists(path))
                {
                    string oldPath = Path.Combine(Application.LocalUserAppDataPath, "agreement.txt");
                    if (File.Exists(oldPath))
                    {
                        string old = File.ReadAllText(oldPath).Trim();
                        if (old == "1" || old == "0")
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            File.WriteAllText(path, old);
                        }
                    }
                }
                return File.Exists(path) && File.ReadAllText(path).Trim() == "1";
            }
            catch
            {
                // 读不到就当没同意过，弹一次协议，不算严重问题。
                return false;
            }
        }

        /// <summary>
        /// 记录用户对协议的同意状态。
        /// </summary>
        public void SetAgreementStatus(bool agreed)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RogueCleaner", "agreement.txt");
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, agreed ? "1" : "0");
            }
            catch
            {
                // 写不进去就算了，最多下次启动再问一次，不影响使用。
            }
        }

        /// <summary>
        /// 检查更新（返回JSON：hasUpdate/version/notes/force/url/sha256/error）
        /// </summary>
        public async Task<string> CheckUpdate()
        {
            return await UpdateService.CheckAsync();
        }

        /// <summary>
        /// 下载并安装更新（返回JSON：ok/msg）
        /// </summary>
        public async Task<string> DoUpdate(string url, string sha256)
        {
            return await UpdateService.InstallAsync(url ?? "", sha256 ?? "");
        }

        /// <summary>
        /// 读取上次更新失败信息（读后清除）
        /// </summary>
        public string GetLastUpdateError()
        {
            return UpdateService.ConsumeLastUpdateError() ?? "";
        }

        /// <summary>
        /// 退出程序（更新安装后调用）
        /// </summary>
        public void ExitApp()
        {
            try { Application.Exit(); }
            catch { Environment.Exit(0); }
        }
    }
}
