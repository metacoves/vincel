using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WindowsFormsApp1
{
    public partial class MainForm : Form
    {
        private WebView2 webView;
        private JsBridge _jsBridge;
        private InstallWatcher _installWatcher;
        private FloatingBall _floatingBall;
        private bool _floatBallEnabled = true;
        private LocalWebServer _localServer;
        private static readonly string ConfigPath = Path.Combine(Application.LocalUserAppDataPath, "vincel_config.txt");

        // 窗口圆角和阴影API
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        public MainForm()
        {
            InitializeComponent();
            _jsBridge = new JsBridge(this);

            // 初始化安装包监控，默认开启
            _installWatcher = new InstallWatcher();
            _installWatcher.InstallerDetected += () =>
            {
                // 检测到安装包，调用前端显示通知
                if (webView?.CoreWebView2 != null)
                {
                    webView.CoreWebView2.ExecuteScriptAsync("showInstallNotice()");
                }
            };
            _installWatcher.Enabled = true;

            // 启动弹窗统计
            PopupCounter.Start();

            // 匿名遥测：启动事件
            TelemetryService.Report("start");

            // 读配置
            LoadConfig();

            // 桌面悬浮球（置顶、不抢焦点）
            _floatingBall = new FloatingBall(this);
            if (_floatBallEnabled) _floatingBall.Show();

            InitWindow();
            InitWebView();
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.AutoScaleDimensions = new SizeF(96F, 96F);
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.ClientSize = new Size(1180, 760);
            this.MinimumSize = new Size(1000, 660);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "深澈 Vincel";
            this.Name = "MainForm";
            this.FormBorderStyle = FormBorderStyle.None;
            this.BackColor = Color.White;
            this.ResumeLayout(false);
        }

        private void InitWindow()
        {
            // 窗口圆角
            HandleCreated += (s, e) =>
            {
                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            };

            // 窗口拖动（无边框）
            MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };

            // 窗口关闭释放资源
            FormClosed += (s, e) =>
            {
                // 关闭时保存弹窗统计数据，避免最后一次统计丢失
                PopupCounter.Stop();
                _floatingBall?.Dispose();
                _installWatcher?.Dispose();
                _localServer?.Dispose();
            };
        }

        private async void InitWebView()
        {
            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };
            Controls.Add(webView);

            // 本地HTTP服务提供页面（http来源，满足CloudBase SDK的跨域要求）
            _localServer = new LocalWebServer(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot"));
            _localServer.Start();

            // 开发期绕过CloudBase SDK的跨域限制（发布前替换为C#同源代理方案）
            var options = new CoreWebView2EnvironmentOptions("--enable-gpu-rasterization --enable-zero-copy --disable-software-rasterizer --disable-features=CalculateNativeWinOcclusion --disable-web-security");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetTempPath(), "VincelWebView"), options);
            await webView.EnsureCoreWebView2Async(env);

            // 暴露C#对象给JS
            webView.CoreWebView2.AddHostObjectToScript("jsBridge", _jsBridge);

            // 加载本地页面（本地HTTP服务）
            webView.CoreWebView2.Navigate(LocalWebServer.RootUrl);

            // 禁用不需要的功能，更像原生应用
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            // 强制100%缩放，解决发糊
            webView.ZoomFactor = 1.0;
        }

        /// <summary>打开/显示主窗口（悬浮球左键单击 / 右键菜单）</summary>
        public void ShowMainWindow()
        {
            ShowWindowCore();
        }

        /// <summary>快速扫描：显示主窗口并触发扫描（悬浮球双击 / 右键菜单）</summary>
        public void TriggerQuickScan()
        {
            ShowWindowCore();
            if (webView?.CoreWebView2 != null)
            {
                try { webView.CoreWebView2.ExecuteScriptAsync("startScan()"); }
                catch { }
            }
        }

        private void ShowWindowCore()
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        /// <summary>设置悬浮球开关</summary>
        public void SetFloatBallEnabled(bool enabled)
        {
            _floatBallEnabled = enabled;
            SaveConfig();
            // 不直接 Show()/Hide()。交给悬浮球自己按规则算，
            // 否则会绕过「打开主窗口时隐藏」那条规则，出现两个开关互相打架。
            _floatingBall?.RefreshVisibility();
        }

        /// <summary>获取悬浮球开关状态</summary>
        public bool GetFloatBallEnabled()
        {
            return _floatBallEnabled;
        }

        /// <summary>获取今日检测到的安装包数量（悬浮球提示用）</summary>
        public int GetInstallTodayCount()
        {
            return _installWatcher?.TodayCount ?? 0;
        }

        /// <summary>读配置</summary>
        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                foreach (var line in File.ReadAllLines(ConfigPath))
                {
                    var parts = line.Split('=');
                    if (parts.Length != 2) continue;
                    if (parts[0] == "floatBall") _floatBallEnabled = parts[1] == "1";
                }
            }
            catch { }
        }

        /// <summary>保存配置</summary>
        private void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllLines(ConfigPath, new[] { "floatBall=" + (_floatBallEnabled ? "1" : "0") });
            }
            catch { }
        }
    }
}
