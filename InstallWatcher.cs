using System;
using System.Management;
using System.Threading;
using System.Windows.Forms;

namespace WindowsFormsApp1
{
    /// <summary>
    /// 安装程序运行提醒（免费版功能）。
    /// 通过 WMI Win32_ProcessStartTrace 后台监控新进程创建，
    /// 检测到可能的安装程序时触发事件，由前端显示WebView2风格通知，只提醒不拦截。
    /// </summary>
    public class InstallWatcher : IDisposable
    {
        private ManagementEventWatcher _watcher;
        private readonly SynchronizationContext _uiContext;
        private bool _enabled;

        private int _todayCount;
        private int _totalCount;
        private DateTime _lastDate;
        private string _dataFile;

        // 检测到安装程序时触发的事件，主窗体订阅后执行JS显示通知
        public event Action InstallerDetected;

        /// <summary>今日检测到的安装程序数量</summary>
        public int TodayCount => _todayCount;

        /// <summary>累计检测到的安装程序数量</summary>
        public int TotalCount => _totalCount + _todayCount;

        // 常见安装程序文件名关键字（大小写不敏感）
        private static readonly string[] InstallerKeywords = new[]
        {
            "setup", "install", "installer", "setup_", "install_",
            "_setup", "_install", ".msi"
        };

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (value) Start();
                else Stop();
            }
        }

        public InstallWatcher()
        {
            // 捕获当前 UI 线程的 SynchronizationContext，WMI 事件回调里用它切回 UI 线程
            _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
            _dataFile = System.IO.Path.Combine(Application.LocalUserAppDataPath, "install_stats.txt");
            _lastDate = DateTime.Now.Date;
            LoadStats();
        }

        /// <summary>启动 WMI 进程监控。</summary>
        public void Start()
        {
            if (_watcher != null) return;

            try
            {
                // Win32_ProcessStartTrace 是系统内置的进程创建事件，不需要额外权限
                var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
                _watcher = new ManagementEventWatcher(query);
                _watcher.EventArrived += OnProcessStarted;
                _watcher.Start();
            }
            catch (Exception ex)
            {
                // WMI 初始化失败（某些精简版 Windows 可能阉割了 WMI），不影响主程序
                System.Diagnostics.Debug.WriteLine($"InstallWatcher 启动失败：{ex.Message}");
                _watcher?.Stop();
                _watcher = null;
                _enabled = false;
            }
        }

        /// <summary>停止 WMI 监控。</summary>
        public void Stop()
        {
            if (_watcher == null) return;
            try
            {
                _watcher.Stop();
                _watcher.EventArrived -= OnProcessStarted;
                _watcher.Dispose();
            }
            catch { }
            _watcher = null;
        }

        private void OnProcessStarted(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var processName = e.NewEvent.Properties["ProcessName"]?.Value as string ?? "";
                if (!LooksLikeInstaller(processName)) return;

                // 跨天清零
                if (DateTime.Now.Date != _lastDate)
                {
                    _totalCount += _todayCount;
                    _todayCount = 0;
                    _lastDate = DateTime.Now.Date;
                }
                _todayCount++;
                SaveStats();

                // WMI 事件在后台线程触发，切回 UI 线程触发事件
                _uiContext.Post(_ => InstallerDetected?.Invoke(), null);
            }
            catch
            {
                // 事件处理异常不能抛，否则会终止 WMI 事件流
            }
        }

        /// <summary>判断进程名是否像安装程序。</summary>
        private static bool LooksLikeInstaller(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            var lower = processName.ToLowerInvariant();
            foreach (var kw in InstallerKeywords)
            {
                if (lower.Contains(kw)) return true;
            }
            return false;
        }

        public void Dispose()
        {
            Stop();
            SaveStats();
        }

        private void LoadStats()
        {
            try
            {
                if (!System.IO.File.Exists(_dataFile)) return;
                var lines = System.IO.File.ReadAllLines(_dataFile);
                if (lines.Length < 2) return;

                DateTime savedDate;
                if (!DateTime.TryParseExact(lines[0], "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out savedDate))
                    return;

                if (savedDate == DateTime.Now.Date)
                {
                    _lastDate = savedDate;
                    if (int.TryParse(lines[1], out var total)) _totalCount = total;
                    if (lines.Length >= 3 && int.TryParse(lines[2], out var today)) _todayCount = today;
                }
                else
                {
                    _lastDate = DateTime.Now.Date;
                    _todayCount = 0;
                    if (int.TryParse(lines[1], out var total)) _totalCount = total;
                    if (lines.Length >= 3 && int.TryParse(lines[2], out var today)) _totalCount += today;
                }
            }
            catch { }
        }

        private void SaveStats()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_dataFile);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllLines(_dataFile, new[]
                {
                    _lastDate.ToString("yyyy-MM-dd"),
                    _totalCount.ToString(),
                    _todayCount.ToString()
                });
            }
            catch { }
        }
    }
}
