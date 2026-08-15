using Microsoft.Win32;
using System;
using System.IO;
using System.Windows.Forms;

namespace WindowsFormsApp1
{
    /// <summary>
    /// 开机自启管理 helper。
    /// 通过读写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 实现，
    /// 不需要管理员权限（HKCU 是当前用户 hive）。
    /// </summary>
    public static class AutoStartHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "Vincel";

        /// <summary>当前是否已设置开机自启。</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    var value = key.GetValue(AppName) as string;
                    return !string.IsNullOrEmpty(value)
                        && value.Equals('"' + Application.ExecutablePath + '"', StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>设置或取消开机自启。</summary>
        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;

                    if (enabled)
                    {
                        // 路径带空格，必须加引号，否则 Windows 解析会断错参数
                        key.SetValue(AppName, '"' + Application.ExecutablePath + '"');
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                // 写注册表失败（权限被策略锁住等），给用户一个提示，但不要崩程序
                MessageBox.Show(
                    $"设置开机自启失败：{ex.Message}\n\n" +
                    "你可以手动在「任务管理器 → 启动」里管理。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 首次运行时默认开启开机自启。
        /// 用一个本地标记文件判断是否已执行过，避免用户手动关闭后下次启动又被强行打开。
        /// </summary>
        public static void EnsureDefaultEnabled()
        {
            try
            {
                var flagPath = Path.Combine(Application.LocalUserAppDataPath, ".autostart_set");
                if (!File.Exists(flagPath))
                {
                    SetEnabled(true);
                    Directory.CreateDirectory(Application.LocalUserAppDataPath);
                    File.WriteAllText(flagPath, "1");
                }
            }
            catch
            {
                // 标记文件写失败不影响功能，最多是下次还会再尝试一次
            }
        }
    }
}
