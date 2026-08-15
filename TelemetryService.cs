using System;
using System.IO;
using System.Windows.Forms;

namespace WindowsFormsApp1
{
    /// <summary>
    /// 匿名遥测（上报逻辑在前端 Telemetry 模块）。
    /// </summary>
    public static class TelemetryService
    {
        /// <summary>设备ID：和 JsBridge.GetDeviceId 同源（%LOCALAPPDATA%\RogueCleaner\device_id.txt）</summary>
        public static string GetDeviceId()
        {
            try
            {
                string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RogueCleaner", "device_id.txt");
                if (File.Exists(file))
                {
                    string id = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrWhiteSpace(id)) return id;
                }
                string newId = Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, newId);
                return newId;
            }
            catch { return Guid.NewGuid().ToString("N"); }
        }

        /// <summary>
        /// 遥测上报入口（保留签名兼容调用点，实现为空）。
        /// </summary>
        public static void Report(string eventName, int count = 1)
        {
            // 遥测由前端上报，此处为空实现。
        }
    }
}
