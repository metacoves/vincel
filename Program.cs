using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;

namespace WindowsFormsApp1
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // 未处理异常一律弹框显示，不能让程序"静默消失"——调试时踩过这个坑：
            // 提权环境下 .NET 默认的崩溃对话框有时候显示不出来，程序看着就像正常退出了，
            // 实际是哪里抛了异常。小白用户遇到这种"用着用着突然没了"更是完全摸不着头脑。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrash(e.ExceptionObject as Exception);

            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                {
                    var processInfo = new ProcessStartInfo
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Environment.CurrentDirectory,
                        FileName = Application.ExecutablePath,
                        Verb = "runas"
                    };

                    try
                    {
                        Process.Start(processInfo);
                    }
                    catch
                    {
                        // 用户在 UAC 弹窗点了"否"，或提权被策略阻止。
                        // 原来这里是静默 return，程序一闪而过，用户完全不知道发生了什么。
                        MessageBox.Show(
                            "本工具需要管理员权限才能清理系统服务、启动项和注册表。\n" +
                            "请右键选择「以管理员身份运行」后重试。",
                            "需要管理员权限",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                    return;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 首次运行默认开启开机自启
            AutoStartHelper.EnsureDefaultEnabled();

            // 启动WebView2混合架构主窗口，原WinForm UI保留但不启动
            Application.Run(new MainForm());
        }

        private static void ShowCrash(Exception ex)
        {
            try
            {
                MessageBox.Show(
                    "程序遇到了一个没有处理的错误，即将退出。\n\n" +
                    "把下面这段信息截图发给开发者，能帮上大忙：\n\n" +
                    (ex?.ToString() ?? "(没有异常详情)"),
                    "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* 连弹框都失败就没办法了，至少不能再往上抛导致二次崩溃 */ }
        }
    }
}