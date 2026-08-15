using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WindowsFormsApp1
{
    /// <summary>
    /// 广告弹窗统计（免费版功能）。
    /// 每小时枚举一次当前窗口，按特征库匹配广告弹窗，只统计不关闭，结果持久化。
    /// </summary>
    public static class PopupCounter
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private static readonly Dictionary<string, int> _todayStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static System.Windows.Forms.Timer _timer;
        private static string _dataFile;
        private static DateTime _lastResetDate;
        private static bool _enabled;
        private static int _totalCount = 0;

        /// <summary>今日检测到的弹窗总数</summary>
        public static int TotalToday => _todayStats.Values.Sum();

        /// <summary>累计检测总数（简单持久化，后续可扩展周/月统计）</summary>
        public static int TotalAll => _totalCount + TotalToday;

        /// <summary>今日弹窗数（悬浮球实时显示用）</summary>
        public static int TodayCount => TotalToday;

        /// <summary>各软件弹窗次数</summary>
        public static IReadOnlyDictionary<string, int> Stats => _todayStats;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (value) _timer?.Start();
                else _timer?.Stop();
            }
        }

        /// <summary>启动统计定时器，程序启动时调用一次</summary>
        public static void Start()
        {
            if (_timer != null) return;

            _dataFile = System.IO.Path.Combine(
                Application.LocalUserAppDataPath,
                "popup_stats.txt");
            _lastResetDate = DateTime.Now.Date;
            Load();

            _timer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 }; // 每小时统计一次
            _timer.Tick += (s, e) => DoCount();
            _enabled = true;
            _timer.Start();

            // 启动时立即执行一次
            DoCount();
        }

        /// <summary>立即执行一次统计</summary>
        public static void DoCount()
        {
            if (!_enabled) return;

            // 跨天自动清零
            if (DateTime.Now.Date != _lastResetDate)
            {
                _totalCount += TotalToday;
                _todayStats.Clear();
                _lastResetDate = DateTime.Now.Date;
            }

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;

                    var title = GetWindowTextSafe(hWnd);
                    var className = GetClassNameSafe(hWnd);
                    var combined = (title + " " + className).ToLowerInvariant();

                    foreach (var kw in Signatures.BadKeywords.Keys)
                    {
                        if (combined.Contains(kw.ToLowerInvariant()))
                        {
                            found.Add(kw);
                            break;
                        }
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            foreach (var name in found)
            {
                if (_todayStats.ContainsKey(name))
                    _todayStats[name]++;
                else
                    _todayStats[name] = 1;
            }

            Save();

            // 匿名遥测：弹窗统计（只报数字，不报关键词明细）
            TelemetryService.Report("popup", TotalToday);
        }

        private static string GetWindowTextSafe(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetClassNameSafe(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static void Save()
        {
            try
            {
                var lines = new List<string>
                {
                    _lastResetDate.ToString("yyyy-MM-dd"),
                    $"_total={_totalCount}"
                };
                foreach (var kv in _todayStats)
                    lines.Add($"{kv.Key}={kv.Value}");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_dataFile));
                System.IO.File.WriteAllLines(_dataFile, lines);
            }
            catch { }
        }

        private static void Load()
        {
            try
            {
                if (!System.IO.File.Exists(_dataFile)) return;
                var lines = System.IO.File.ReadAllLines(_dataFile);
                if (lines.Length == 0) return;

                DateTime savedDate;
                if (!DateTime.TryParseExact(lines[0], "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out savedDate))
                {
                    // 存档格式不对，保持内存状态不动
                    return;
                }

                if (savedDate == DateTime.Now.Date)
                {
                    // 今天的存档：累计总数 + 今日明细都恢复
                    _lastResetDate = savedDate;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var parts = lines[i].Split('=');
                        if (parts.Length == 2 && int.TryParse(parts[1], out var count) && count >= 0)
                        {
                            if (parts[0] == "_total")
                                _totalCount = count;
                            else
                                _todayStats[parts[0]] = count;
                        }
                    }
                }
                else
                {
                    // 旧日期的存档：今日明细清零，累计总数不能丢——
                    // 把存档里的"当日统计"也并入累计总数（相当于补做一次跨天滚动）
                    _lastResetDate = DateTime.Now.Date;
                    _todayStats.Clear();
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var parts = lines[i].Split('=');
                        if (parts.Length == 2 && int.TryParse(parts[1], out var count) && count >= 0)
                        {
                            _totalCount += count; // _total 行和旧统计行都并入累计
                        }
                    }
                }
            }
            catch { }
        }

        public static void Stop()
        {
            _timer?.Stop();
            Save();
        }
    }
}
