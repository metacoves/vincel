using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsFormsApp1
{
    /// <summary>
    /// 桌面悬浮球（深澈 Vincel）。
    ///
    /// 设计铁律（改动前务必看完，历史上这里被改坏过很多次）：
    /// 1. 窗体尺寸恒定 FORM_W×FORM_H，永不 resize；球半径恒等于 BALL_RADIUS。
    ///    全文件不存在任何"随交互状态变化的缩放计算"，这是球不跳动的根本原因。
    /// 2. hover 只做两件事：阴影透明度 70→120、图标透明度 0.9→1.0；
    ///    按下只做一件事：图标透明度 0.8。位置和尺寸一律不动。
    /// 3. 球体圆形之外的区域通过 WM_NCHITTEST 返回 HTTRANSPARENT 实现点击穿透，
    ///    否则窗体那一大圈透明区域会挡住桌面。
    /// 4. 球内 V 图标、右键菜单图标全部用 GDI+ 矢量绘制：
    ///    不读图片文件（避免白底方块），不使用 emoji（开发者机器上不出彩色）。
    /// </summary>
    public class FloatingBall : Form
    {
        #region Win32 API

        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int AC_SRC_OVER = 0x00;
        private const int AC_SRC_ALPHA = 0x01;
        private const int ULW_ALPHA = 0x02;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;
        private const int HTCLIENT = 1;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        #endregion

        #region 固定常量（全部编译期常量，运行时永不改变）

        /// <summary>球直径。验收标准写死 112px。</summary>
        private const int BALL_SIZE = 112;
        private const float BALL_RADIUS = BALL_SIZE / 2f;

        /// <summary>
        /// 窗体尺寸恒定。左右留白用来放红角标，上下各留一条提示条的高度，
        /// 这样提示条不论画在球上方还是下方都在窗体内，永远不会被裁掉。
        /// </summary>
        private const int FORM_W = 320;
        private const int FORM_H = 252;

        /// <summary>球在窗体内的位置（居中），窗体坐标系。</summary>
        private const int BALL_LEFT = (FORM_W - BALL_SIZE) / 2;   // 84
        private const int BALL_TOP = (FORM_H - BALL_SIZE) / 2;    // 60
        private const float BALL_CX = BALL_LEFT + BALL_RADIUS;    // 140
        private const float BALL_CY = BALL_TOP + BALL_RADIUS;     // 116

        /// <summary>V 图标高度（球直径的 52%，视觉最稳）。</summary>
        private const float ICON_H = 58f;

        /// <summary>提示条：20px 字号，高 54，与球间隔 12。
        /// 改这里要同步确认 FORM_H >= BALL_SIZE + 2*(TIP_GAP + TIP_H) + 4。</summary>
        private const float TIP_FONT_PX = 20f;
        private const float TIP_H = 54f;
        private const float TIP_GAP = 12f;
        private const float TIP_RADIUS = 14f;

        /// <summary>hover 多久之后弹提示条（毫秒）。</summary>
        private const int HOVER_DELAY_MS = 300;
        private const int HOVER_TICK_MS = 100;

        // 交互只允许改这几个透明度，别的什么都不许动
        private const int SHADOW_ALPHA_NORMAL = 70;
        private const int SHADOW_ALPHA_HOVER = 120;
        private const int ICON_ALPHA_NORMAL = 230;   // 0.9
        private const int ICON_ALPHA_HOVER = 255;    // 1.0
        private const int ICON_ALPHA_PRESSED = 204;  // 0.8

        private const float MENU_RADIUS = 10f;
        /// <summary>
        /// 右键菜单的字号 / 图标 / 内边距，都是【100% DPI 下的设计值】，
        /// 运行时统一乘 _uiScale，高分屏上不会缩水。
        /// </summary>
        private const float MENU_FONT_PX = 15f;
        private const int MENU_ICON_SIZE = 18;
        /// <summary>图标设计稿的坐标系边长。所有 MakeXxxIcon 里的坐标都按这个尺寸写，
        /// 与实际输出尺寸(MENU_ICON_SIZE×DPI)解耦，改大小不用重画图标。</summary>
        private const float ICON_DESIGN = 24f;
        private const int DRAG_THRESHOLD = 4;
        private const int EDGE_MARGIN = 6;

        private static readonly Color BrandBlue = Color.FromArgb(0x16, 0x5D, 0xFF);
        private static readonly Color RedDanger = Color.FromArgb(0xF5, 0x3F, 0x3F);
        private static readonly Color TextMain = Color.FromArgb(0x0B, 0x20, 0x36);
        private static readonly Color MenuHover = Color.FromArgb(0xE8, 0xF3, 0xFF);
        private static readonly Color HairLine = Color.FromArgb(0xE5, 0xEA, 0xF2);

        /// <summary>
        /// 品牌 logo 的两支笔画颜色，取自 Vincel 标志（深宝蓝 #0626AC）。
        /// 左笔画稍深、右笔画稍亮，制造一点体积感。
        /// </summary>
        private static readonly Color LogoDark = Color.FromArgb(0x04, 0x1E, 0x93);
        private static readonly Color LogoLite = Color.FromArgb(0x0A, 0x2E, 0xBE);

        // 右键菜单图标配色（emoji 观感：一个图标里多种颜色，不是单色线框）
        private static readonly Color LensGlass = Color.FromArgb(0xCD, 0xE8, 0xFB);   // 镜片浅蓝
        private static readonly Color LensRim = Color.FromArgb(0x2E, 0x7B, 0xD6);     // 镜框钢蓝
        private static readonly Color LensGripDark = Color.FromArgb(0x69, 0x76, 0x8C);// 手柄深灰
        private static readonly Color LensGripLite = Color.FromArgb(0x8C, 0x99, 0xAC);// 手柄浅灰
        private static readonly Color RoofRed = Color.FromArgb(0xE8, 0x61, 0x3C);     // 屋顶砖红
        private static readonly Color RoofShade = Color.FromArgb(0xC9, 0x4A, 0x28);   // 屋顶背光面
        private static readonly Color WallCream = Color.FromArgb(0xF7, 0xE7, 0xC8);   // 墙体米色
        private static readonly Color WallEdge = Color.FromArgb(0xD7, 0xB6, 0x86);    // 墙体描边
        private static readonly Color DoorBrown = Color.FromArgb(0x8B, 0x5A, 0x2B);   // 木门棕
        private static readonly Color WinBlue = Color.FromArgb(0x7F, 0xC4, 0xE8);     // 窗户天蓝
        private static readonly Color CheckGreen = Color.FromArgb(0x34, 0xC7, 0x59);  // 勾选绿
        private static readonly Color CheckOffFill = Color.FromArgb(0xEC, 0xEF, 0xF4); // 未勾选底
        private static readonly Color CheckOffEdge = Color.FromArgb(0xC2, 0xC9, 0xD6); // 未勾选描边

        /// <summary>
        /// 球边缘的品牌色描边（左上亮、右下深）。同一套颜色也用在提示条和右键菜单的描边上，
        /// 三者视觉成套。改品牌色只需要动这两行。
        /// </summary>
        private static readonly Color RingLite = Color.FromArgb(0x3D, 0x63, 0xF0);
        private static readonly Color RingDark = Color.FromArgb(0x06, 0x26, 0xAC);

        /// <summary>
        /// 品牌 V 标志的矢量轮廓。坐标从官方 logo 图逐像素量出来后，
        /// 以"图形面积质心"为原点、按图形高度归一化得到（所以视觉上天然居中）。
        /// 乘以 ICON_H 再加上球心坐标就是实际绘制点，全程没有状态相关的缩放。
        /// </summary>
        private static readonly PointF[] LogoStrokeLeft =
        {
            new PointF(-0.4828f, -0.2824f),
            new PointF(-0.2652f, -0.2824f),
            new PointF( 0.1422f,  0.5232f),
            new PointF(-0.0800f,  0.5232f)
        };

        private static readonly PointF[] LogoStrokeRight =
        {
            new PointF( 0.2719f, -0.4768f),
            new PointF( 0.4871f, -0.4768f),
            new PointF( 0.1052f,  0.2639f),
            new PointF(-0.0036f,  0.0463f)
        };

        /// <summary>界面字体族，找不到微软雅黑时逐级降级，绝不抛异常。</summary>
        private static readonly FontFamily UiFamily = ResolveUiFamily();

        /// <summary>
        /// 界面缩放系数 = 当前 DPI / 96，构建菜单时取一次。
        /// ⚠️ 这个缩放【只作用于右键菜单】（字号/图标/内边距/圆角）。
        /// 悬浮球本体（球径、窗体、V 图标、提示条）一律不参与任何缩放，
        /// "禁止缩放计算"的铁律针对的是球，没有被打破。
        /// </summary>
        private static float _uiScale = 1f;

        #endregion

        #region 私有字段

        private readonly MainForm _main;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private readonly System.Windows.Forms.Timer _clickTimer;
        private readonly System.Windows.Forms.Timer _hoverTimer;

        private ContextMenuStrip _menu;
        private Font _menuFont;
        private Bitmap _icoScan;
        private Bitmap _icoHome;
        private Bitmap _icoCheckOn;
        private Bitmap _icoCheckOff;
        private Bitmap _icoExit;

        private int _count;
        private int _installCount;
        private bool _hovered;
        private bool _pressed;
        private bool _showTip;
        private int _hoverTicks;

        private bool _mouseDown;
        private bool _dragging;
        private bool _suppressClick;
        private bool _menuOpen;
        private bool _applyingRule;
        private Point _dragOffset;
        private Point _downPos;

        private bool _hideWhenMainOpen;
        private int _savedX = int.MinValue;
        private int _savedY = int.MinValue;

        #endregion

        public FloatingBall(MainForm main)
        {
            _main = main;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;

            // 尺寸三重上锁：窗体永远不可能被 resize
            Size = new Size(FORM_W, FORM_H);
            MinimumSize = new Size(FORM_W, FORM_H);
            MaximumSize = new Size(FORM_W, FORM_H);

            LoadConfig();
            RestorePosition();
            BuildMenu();

            _count = SafeTodayCount();
            _installCount = SafeInstallTodayCount();

            // 定时刷新弹窗计数，数字没变就不重画
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 30 * 1000 };
            _refreshTimer.Tick += (s, e) =>
            {
                int c = SafeTodayCount();
                int ic = SafeInstallTodayCount();
                if (c == _count && ic == _installCount) return;
                _count = c;
                _installCount = ic;
                Redraw();
            };
            _refreshTimer.Start();

            // 单击延迟，用来和双击区分开
            _clickTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _clickTimer.Tick += (s, e) =>
            {
                _clickTimer.Stop();
                _main.ShowMainWindow();
            };

            // hover 计时 + 光标看门狗（防止 WM_MOUSELEAVE 漏发导致提示条卡住不消失）
            _hoverTimer = new System.Windows.Forms.Timer { Interval = HOVER_TICK_MS };
            _hoverTimer.Tick += (s, e) => OnHoverTick();

            _main.VisibleChanged += (s, e) => ApplyVisibilityRule();
            _main.Resize += (s, e) => ApplyVisibilityRule();
        }

        #region 窗体基础行为

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED;
                return cp;
            }
        }

        /// <summary>球以外的区域点击穿透，让桌面和其它窗口能正常接收鼠标。</summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                long lp = m.LParam.ToInt64();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);
                float dx = sx - (Left + BALL_CX);
                float dy = sy - (Top + BALL_CY);
                bool inBall = dx * dx + dy * dy <= BALL_RADIUS * BALL_RADIUS;
                m.Result = (IntPtr)(inBall ? HTCLIENT : HTTRANSPARENT);
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>兜底：任何来源的尺寸变化都立刻还原，尺寸恒定是不跳动的前提。</summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width != FORM_W || Height != FORM_H)
                Size = new Size(FORM_W, FORM_H);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Redraw();
        }

        /// <summary>
        /// MainForm 的设置开关会直接调 Show()/Hide()，绕过 ApplyVisibilityRule。
        /// 这里补一次规则判断，保证「总开关」和「打开主窗口时隐藏」两条规则永远一致，
        /// 不会出现"设置里关了球，主窗口一动球又自己冒出来"。
        /// 用 BeginInvoke 延后执行，避免在 Show()/Hide() 内部递归。
        /// </summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (_applyingRule || !IsHandleCreated || IsDisposed) return;

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    ApplyVisibilityRule();
                    if (Visible) Redraw();
                }));
            }
            catch
            {
                // 句柄正在销毁等边界情况，忽略即可
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _refreshTimer.Stop();
            _clickTimer.Stop();
            _hoverTimer.Stop();
            SaveConfig();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_refreshTimer != null) _refreshTimer.Dispose();
                if (_clickTimer != null) _clickTimer.Dispose();
                if (_hoverTimer != null) _hoverTimer.Dispose();
                if (_menu != null) { _menu.Dispose(); _menu = null; }
                if (_menuFont != null) { _menuFont.Dispose(); _menuFont = null; }
                if (_icoScan != null) { _icoScan.Dispose(); _icoScan = null; }
                if (_icoHome != null) { _icoHome.Dispose(); _icoHome = null; }
                if (_icoCheckOn != null) { _icoCheckOn.Dispose(); _icoCheckOn = null; }
                if (_icoCheckOff != null) { _icoCheckOff.Dispose(); _icoCheckOff = null; }
                if (_icoExit != null) { _icoExit.Dispose(); _icoExit = null; }
            }
            base.Dispose(disposing);
        }

        #endregion

        #region 鼠标交互

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            BeginHover();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_mouseDown || _menuOpen) return;   // 拖动中、菜单开着，都不算离开
            EndHover();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            _mouseDown = true;
            _dragging = false;
            _downPos = e.Location;
            _pressed = true;
            Redraw();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_mouseDown) return;

            if (!_dragging)
            {
                if (Math.Abs(e.X - _downPos.X) + Math.Abs(e.Y - _downPos.Y) <= DRAG_THRESHOLD) return;

                // 开始拖动：收起提示条，避免提示条跟着球满屏飞
                _dragging = true;
                _pressed = false;
                _showTip = false;
                _hoverTicks = 0;
                _dragOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
                Redraw();
                return;
            }

            // 拖动时不重绘，只移动窗口（分层窗口的画面会跟着窗口一起走）
            MoveBallTo(Cursor.Position.X - _dragOffset.X, Cursor.Position.Y - _dragOffset.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                ShowMenuNextToBall();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            _mouseDown = false;
            _pressed = false;

            if (_dragging)
            {
                _dragging = false;
                SaveConfig();
                if (IsCursorOnBall()) BeginHover(); else EndHover();
                Redraw();
                return;
            }

            Redraw();

            // 双击已经处理过了，这一下 MouseUp 不能再触发单击
            if (_suppressClick) { _suppressClick = false; return; }

            _clickTimer.Stop();
            _clickTimer.Start();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;

            _clickTimer.Stop();
            _suppressClick = true;
            _main.TriggerQuickScan();
        }

        /// <summary>
        /// 把菜单贴着球弹出：优先放球的右侧，右边放不下就翻到左侧，
        /// 纵向和球心对齐。这样一眼就看得出"这个列表是这个球打开的"。
        /// </summary>
        private void ShowMenuNextToBall()
        {
            if (_menu == null || _menu.IsDisposed) return;

            int gap = Sc(8);
            var ball = new Rectangle(Left + BALL_LEFT, Top + BALL_TOP, BALL_SIZE, BALL_SIZE);

            var size = _menu.Size;
            if (size.Width <= 1 || size.Height <= 1)
                size = _menu.GetPreferredSize(Size.Empty);
            if (size.Width <= 1 || size.Height <= 1) return;

            Rectangle wa;
            try { wa = Screen.FromPoint(new Point(ball.Left + BALL_SIZE / 2, ball.Top + BALL_SIZE / 2)).WorkingArea; }
            catch { wa = Screen.PrimaryScreen.WorkingArea; }

            int x = ball.Right + gap;
            if (x + size.Width > wa.Right) x = ball.Left - gap - size.Width;
            if (x < wa.Left) x = wa.Left;
            if (x + size.Width > wa.Right) x = wa.Right - size.Width;

            int y = ball.Top + (BALL_SIZE - size.Height) / 2;
            if (y + size.Height > wa.Bottom) y = wa.Bottom - size.Height;
            if (y < wa.Top) y = wa.Top;

            _menu.Show(new Point(x, y));
        }

        private void BeginHover()
        {
            if (_hovered) return;
            _hovered = true;
            _showTip = false;
            _hoverTicks = 0;
            _hoverTimer.Start();
            Redraw();
        }

        private void EndHover()
        {
            _hoverTimer.Stop();
            if (!_hovered && !_showTip && !_pressed) return;
            _hovered = false;
            _pressed = false;
            _showTip = false;
            _hoverTicks = 0;
            Redraw();
        }

        private void OnHoverTick()
        {
            // 光标已经不在球上了（右键菜单弹出、窗口被移开等情况会漏掉 MouseLeave）
            if (_menuOpen) return;
            if (!IsCursorOnBall() && !_mouseDown)
            {
                EndHover();
                return;
            }
            if (_showTip || _dragging) return;

            _hoverTicks++;
            if (_hoverTicks * HOVER_TICK_MS < HOVER_DELAY_MS) return;

            _showTip = true;
            Redraw();
        }

        private bool IsCursorOnBall()
        {
            var p = Cursor.Position;
            float dx = p.X - (Left + BALL_CX);
            float dy = p.Y - (Top + BALL_CY);
            return dx * dx + dy * dy <= BALL_RADIUS * BALL_RADIUS;
        }

        #endregion

        #region 渲染

        private void Redraw()
        {
            if (!IsHandleCreated || IsDisposed) return;

            using (var bmp = new Bitmap(FORM_W, FORM_H, PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);

                DrawShadow(g);
                DrawBall(g);
                DrawLogo(g);
                DrawBadge(g);
                if (_showTip) DrawTip(g);

                UpdateLayer(bmp);
            }
        }

        /// <summary>投影。只有透明度随 hover 变化，几何形状是常量。</summary>
        private void DrawShadow(Graphics g)
        {
            const float blur = 10f;
            const float offsetY = 4f;
            const float sr = BALL_RADIUS + blur;

            int alpha = _hovered ? SHADOW_ALPHA_HOVER : SHADOW_ALPHA_NORMAL;

            using (var path = new GraphicsPath())
            {
                path.AddEllipse(BALL_CX - sr, BALL_CY - sr + offsetY, sr * 2f, sr * 2f);
                using (var pgb = new PathGradientBrush(path))
                {
                    pgb.CenterPoint = new PointF(BALL_CX, BALL_CY + offsetY);
                    pgb.CenterColor = Color.FromArgb(alpha, 16, 32, 64);
                    pgb.SurroundColors = new[] { Color.FromArgb(0, 16, 32, 64) };
                    const float focus = (BALL_RADIUS - 2f) / sr;
                    pgb.FocusScales = new PointF(focus, focus);
                    g.FillPath(pgb, path);
                }
            }
        }

        /// <summary>白色毛玻璃球体：竖向微渐变 + 外发丝边 + 内高光边，macOS 质感。</summary>
        private void DrawBall(Graphics g)
        {
            var ballRect = new RectangleF(BALL_LEFT, BALL_TOP, BALL_SIZE, BALL_SIZE);

            using (var ballPath = new GraphicsPath())
            {
                ballPath.AddEllipse(ballRect);

                // 渐变矩形上下各撑 1px，避免 LinearGradientBrush 边缘回绕出现色带
                var gradRect = new RectangleF(ballRect.X, ballRect.Y - 1f, ballRect.Width, ballRect.Height + 2f);
                using (var lg = new LinearGradientBrush(gradRect,
                    Color.FromArgb(253, 255, 255, 255),
                    Color.FromArgb(247, 243, 247, 252),
                    LinearGradientMode.Vertical))
                {
                    g.FillPath(lg, ballPath);
                }
            }

            // 品牌色渐变圆环：白色桌面上球不再"看不见"。
            // 环的位置和粗细都是常量，不随交互变化，不违反"禁止缩放"的铁律。
            const float ringWidth = 3f;
            const float ringInset = 2.6f;
            var ringRect = new RectangleF(ballRect.X + ringInset, ballRect.Y + ringInset,
                                          ballRect.Width - ringInset * 2f, ballRect.Height - ringInset * 2f);
            using (var ringBrush = new LinearGradientBrush(
                new RectangleF(ringRect.X - 1f, ringRect.Y - 1f, ringRect.Width + 2f, ringRect.Height + 2f),
                RingLite, RingDark, LinearGradientMode.ForwardDiagonal))
            using (var ringPen = new Pen(ringBrush, ringWidth))
            {
                g.DrawEllipse(ringPen, ringRect);
            }

            // 圆环外侧留一圈白，让描边和投影之间有呼吸感（"硬币"质感）
            using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1.4f))
                g.DrawEllipse(pen, ballRect.X + 0.7f, ballRect.Y + 0.7f, ballRect.Width - 1.4f, ballRect.Height - 1.4f);

            // 圆环内侧一圈极淡的品牌色晕，过渡更柔
            using (var pen = new Pen(Color.FromArgb(30, 10, 46, 190), 2f))
                g.DrawEllipse(pen, ballRect.X + 5.6f, ballRect.Y + 5.6f, ballRect.Width - 11.2f, ballRect.Height - 11.2f);

            // 顶部柔光
            using (var glowPath = new GraphicsPath())
            {
                glowPath.AddEllipse(BALL_CX - BALL_RADIUS * 0.66f, BALL_TOP + 4f,
                                BALL_RADIUS * 1.32f, BALL_RADIUS * 0.60f);
                using (var pgb = new PathGradientBrush(glowPath))
                {
                    pgb.CenterColor = Color.FromArgb(96, 255, 255, 255);
                    pgb.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                    g.FillPath(pgb, glowPath);
                }
            }
        }

        /// <summary>品牌 V 标志。只有透明度随状态变化，顶点坐标是常量。</summary>
        private void DrawLogo(Graphics g)
        {
            int alpha = _pressed ? ICON_ALPHA_PRESSED : (_hovered ? ICON_ALPHA_HOVER : ICON_ALPHA_NORMAL);

            using (var left = BuildLogoPath(LogoStrokeLeft))
            using (var right = BuildLogoPath(LogoStrokeRight))
            using (var brushLeft = new SolidBrush(Color.FromArgb(alpha, LogoDark)))
            using (var brushRight = new SolidBrush(Color.FromArgb(alpha, LogoLite)))
            {
                g.FillPath(brushLeft, left);
                g.FillPath(brushRight, right);
            }
        }

        private static GraphicsPath BuildLogoPath(PointF[] normalized)
        {
            var pts = new PointF[normalized.Length];
            for (int i = 0; i < normalized.Length; i++)
            {
                pts[i] = new PointF(BALL_CX + normalized[i].X * ICON_H,
                                    BALL_CY + normalized[i].Y * ICON_H);
            }
            var path = new GraphicsPath();
            path.AddPolygon(pts);
            return path;
        }

        /// <summary>右上角红色数字角标，仅在今日检测数 &gt; 0 时出现。</summary>
        private void DrawBadge(Graphics g)
        {
            if (_count <= 0) return;

            const float size = 26f;
            // 球心的右上 45° 方向，正好压在球边缘上
            var rect = new RectangleF(BALL_CX + 44f - size / 2f, BALL_CY - 44f - size / 2f, size, size);

            using (var badgeBrush = new SolidBrush(RedDanger))
                g.FillEllipse(badgeBrush, rect);
            using (var pen = new Pen(Color.White, 2f))
                g.DrawEllipse(pen, rect);

            string text = _count > 99 ? "99+" : _count.ToString();
            float px = text.Length >= 3 ? 11f : 13f;

            using (var font = new Font(UiFamily, px, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            })
            using (var textBrush = new SolidBrush(Color.White))
            {
                var textRect = new RectangleF(rect.X, rect.Y - 0.5f, rect.Width, rect.Height);
                g.DrawString(text, font, textBrush, textRect, sf);
            }
        }

        /// <summary>
        /// 悬停提示条：16px 字号。默认画在球上方，
        /// 球贴到屏幕顶部时自动翻到球下方（窗体上下都预留了位置，不需要改尺寸）。
        /// </summary>
        private void DrawTip(Graphics g)
        {
            string text;
            if (_count > 0 && _installCount > 0)
                text = string.Format("今日检测到 {0} 个弹窗，{1} 个安装包", _count, _installCount);
            else if (_count > 0)
                text = string.Format("今日检测到 {0} 个广告弹窗", _count);
            else if (_installCount > 0)
                text = string.Format("今日检测到 {0} 个安装包运行", _installCount);
            else
                text = "今日暂未检测到弹窗和安装包";

            using (var font = new Font(UiFamily, TIP_FONT_PX, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                float textW = g.MeasureString(text, font).Width;
                float tipW = Math.Min(FORM_W - 8f, textW + 34f);
                float tipX = BALL_CX - tipW / 2f;
                float tipY = TipGoesBelow()
                    ? BALL_TOP + BALL_SIZE + TIP_GAP
                    : BALL_TOP - TIP_GAP - TIP_H;

                var rect = new RectangleF(tipX, tipY, tipW, TIP_H);

                // 提示条自己的一层浅投影
                using (var shadow = RoundedRect(new RectangleF(rect.X, rect.Y + 2f, rect.Width, rect.Height), TIP_RADIUS))
                using (var shadowBrush = new SolidBrush(Color.FromArgb(28, 16, 32, 64)))
                {
                    g.FillPath(shadowBrush, shadow);
                }

                using (var path = RoundedRect(rect, TIP_RADIUS))
                using (var fillBrush = new SolidBrush(Color.FromArgb(250, 255, 255, 255)))
                {
                    g.FillPath(fillBrush, path);
                }

                // 和球一样的品牌色渐变描边，两者视觉上成套
                var borderRect = new RectangleF(rect.X + 1f, rect.Y + 1f, rect.Width - 2f, rect.Height - 2f);
                using (var borderPath = RoundedRect(borderRect, TIP_RADIUS - 1f))
                using (var borderBrush = new LinearGradientBrush(
                    new RectangleF(borderRect.X - 1f, borderRect.Y - 1f, borderRect.Width + 2f, borderRect.Height + 2f),
                    RingLite, RingDark, LinearGradientMode.ForwardDiagonal))
                using (var borderPen = new Pen(borderBrush, 2f))
                {
                    g.DrawPath(borderPen, borderPath);
                }

                using (var tipTextBrush = new SolidBrush(TextMain))
                    g.DrawString(text, font, tipTextBrush, rect, sf);
            }
        }

        /// <summary>球贴着屏幕顶部时，提示条画到球下方去。</summary>
        private bool TipGoesBelow()
        {
            try
            {
                var center = new Point(Left + BALL_LEFT + (int)BALL_RADIUS, Top + BALL_TOP + (int)BALL_RADIUS);
                var wa = Screen.FromPoint(center).WorkingArea;
                return Top < wa.Top;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateLayer(Bitmap bmp)
        {
            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldObj = IntPtr.Zero;
            try
            {
                screenDc = GetDC(IntPtr.Zero);
                memDc = CreateCompatibleDC(screenDc);
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                oldObj = SelectObject(memDc, hBitmap);

                var size = new SIZE { cx = FORM_W, cy = FORM_H };
                var src = new POINT { x = 0, y = 0 };
                var dst = new POINT { x = Left, y = Top };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            catch
            {
                // 绘制失败不能让程序崩，下一次 Redraw 会重试
            }
            finally
            {
                if (memDc != IntPtr.Zero)
                {
                    if (oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
                    DeleteDC(memDc);
                }
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            var path = new GraphicsPath();
            if (r.Width <= d || r.Height <= d)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        private static FontFamily ResolveUiFamily()
        {
            string[] candidates = { "微软雅黑", "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "SimSun" };
            foreach (var name in candidates)
            {
                try { return new FontFamily(name); }
                catch (ArgumentException) { }
            }
            return FontFamily.GenericSansSerif;
        }

        /// <summary>取当前 DPI 缩放系数，失败或异常值一律退回 1.0。</summary>
        private static float GetDpiScale()
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float s = g.DpiX / 96f;
                    if (s < 1f) s = 1f;
                    if (s > 4f) s = 4f;
                    return s;
                }
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>把 100% DPI 下的设计值换算成当前 DPI 的实际像素，最小 1。</summary>
        private static int Sc(float designValue)
        {
            int n = (int)Math.Round(designValue * _uiScale);
            return n < 1 ? 1 : n;
        }

        private static int SafeTodayCount()
        {
            try
            {
                int c = PopupCounter.TodayCount;
                return c < 0 ? 0 : c;
            }
            catch
            {
                return 0;
            }
        }

        private int SafeInstallTodayCount()
        {
            try
            {
                return _main.GetInstallTodayCount();
            }
            catch
            {
                return 0;
            }
        }

        #endregion

        #region 位置与配置

        private static string ConfigPath
        {
            get { return Path.Combine(Application.LocalUserAppDataPath, "ball_config.txt"); }
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                foreach (var line in File.ReadAllLines(ConfigPath))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || eq >= line.Length - 1) continue;

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    int n;

                    if (key == "x" && int.TryParse(val, out n)) _savedX = n;
                    else if (key == "y" && int.TryParse(val, out n)) _savedY = n;
                    else if (key == "hideWhenMainOpen") _hideWhenMainOpen = val == "1";
                }
            }
            catch
            {
                // 配置坏了就用默认位置，不影响使用
            }
        }

        private void SaveConfig()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(ConfigPath, new[]
                {
                    "x=" + Left,
                    "y=" + Top,
                    "hideWhenMainOpen=" + (_hideWhenMainOpen ? "1" : "0")
                });
            }
            catch
            {
                // 存不下就算了，不能因为记位置失败弹错误
            }
        }

        /// <summary>
        /// 注意：窗体比球大一圈（透明区），所有位置计算都必须以"球"为准，
        /// 否则球会被挡在离屏幕边缘很远的地方贴不上去。
        /// </summary>
        private void RestorePosition()
        {
            if (_savedX != int.MinValue && _savedY != int.MinValue)
            {
                var ball = new Rectangle(_savedX + BALL_LEFT, _savedY + BALL_TOP, BALL_SIZE, BALL_SIZE);
                foreach (var screen in Screen.AllScreens)
                {
                    if (screen.WorkingArea.Contains(ball))
                    {
                        Location = new Point(_savedX, _savedY);
                        return;
                    }
                }
            }

            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - 24 - (BALL_LEFT + BALL_SIZE),
                                 wa.Bottom - 24 - (BALL_TOP + BALL_SIZE));
        }

        private void MoveBallTo(int x, int y)
        {
            Rectangle wa;
            try { wa = Screen.FromPoint(Cursor.Position).WorkingArea; }
            catch { wa = Screen.PrimaryScreen.WorkingArea; }

            int minL = wa.Left + EDGE_MARGIN - BALL_LEFT;
            int maxL = wa.Right - EDGE_MARGIN - (BALL_LEFT + BALL_SIZE);
            int minT = wa.Top + EDGE_MARGIN - BALL_TOP;
            int maxT = wa.Bottom - EDGE_MARGIN - (BALL_TOP + BALL_SIZE);

            if (maxL < minL) maxL = minL;
            if (maxT < minT) maxT = minT;

            Location = new Point(Math.Max(minL, Math.Min(x, maxL)),
                                 Math.Max(minT, Math.Min(y, maxT)));
        }

        /// <summary>
        /// 供 MainForm 调用：设置页里的悬浮球开关改变后调一下，让显示状态立刻按规则重算。
        /// </summary>
        public void RefreshVisibility()
        {
            ApplyVisibilityRule();
        }

        /// <summary>
        /// 决定球此刻该不该显示。两条规则，优先级从高到低：
        /// 1. 设置页里的「显示悬浮球」总开关 —— 关掉就一律不显示；
        /// 2. 右键菜单里的「打开主窗口时隐藏」 —— 主窗口在前台时临时藏起来。
        /// </summary>
        private void ApplyVisibilityRule()
        {
            if (_applyingRule) return;
            _applyingRule = true;
            try
            {
                ApplyVisibilityRuleCore();
            }
            finally
            {
                _applyingRule = false;
            }
        }

        private void ApplyVisibilityRuleCore()
        {
            // 总开关关掉时，后面的规则一概不看
            bool enabled = true;
            try { enabled = _main.GetFloatBallEnabled(); }
            catch { enabled = true; }

            if (!enabled)
            {
                if (Visible) { EndHover(); Hide(); }
                return;
            }

            bool shouldHide = _hideWhenMainOpen && _main.Visible && _main.WindowState != FormWindowState.Minimized;
            if (shouldHide)
            {
                if (Visible) { EndHover(); Hide(); }
            }
            else if (!Visible)
            {
                Show();
                _count = SafeTodayCount();
                _installCount = SafeInstallTodayCount();
                Redraw();
            }
        }

        #endregion

        #region 右键菜单（圆角 + 彩色矢量图标 + 浅蓝悬停）

        private void BuildMenu()
        {
            _uiScale = GetDpiScale();

            _icoScan = MakeScanIcon();
            _icoHome = MakeHomeIcon();
            _icoCheckOn = MakeCheckIcon(true);
            _icoCheckOff = MakeCheckIcon(false);
            _icoExit = MakeExitIcon();

            _menuFont = new Font(UiFamily, MENU_FONT_PX * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel);

            _menu = new RoundedMenu
            {
                Renderer = new MacMenuRenderer(),
                ShowImageMargin = true,
                ShowCheckMargin = false,
                DropShadowEnabled = true,
                BackColor = Color.White,
                ForeColor = TextMain,
                Font = _menuFont,
                Padding = new Padding(Sc(5)),
                ImageScalingSize = new Size(Sc(MENU_ICON_SIZE), Sc(MENU_ICON_SIZE))
            };

            var miScan = NewItem("快速扫描", _icoScan);
            miScan.Click += (s, e) => _main.TriggerQuickScan();

            var miOpen = NewItem("打开主窗口", _icoHome);
            miOpen.Click += (s, e) => _main.ShowMainWindow();

            // 自己管理勾选状态：用 CheckOnClick 的话 WinForms 会在图标上盖一个黑方块
            var miHide = NewItem("打开主窗口时隐藏", _hideWhenMainOpen ? _icoCheckOn : _icoCheckOff);
            miHide.Click += (s, e) =>
            {
                _hideWhenMainOpen = !_hideWhenMainOpen;
                miHide.Image = _hideWhenMainOpen ? _icoCheckOn : _icoCheckOff;
                SaveConfig();
                ApplyVisibilityRule();
            };

            var miExit = NewItem("退出程序", _icoExit);
            miExit.Click += (s, e) => Application.Exit();

            _menu.Items.Add(miScan);
            _menu.Items.Add(miOpen);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miHide);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miExit);

            // 菜单弹出期间 MouseLeave 会漏发，关闭后重新判断一次悬停状态
            // 菜单打开期间球保持高亮，收起提示条；MouseLeave 这时会漏发，用 _menuOpen 顶住
            _menu.Opened += (s, e) =>
            {
                _menuOpen = true;
                _hovered = true;
                _showTip = false;
                _hoverTicks = 0;
                Redraw();
            };
            _menu.Closed += (s, e) =>
            {
                _menuOpen = false;
                if (IsCursorOnBall()) BeginHover(); else EndHover();
            };

            // 注意：这里【不】设置 ContextMenuStrip。
            // 设了的话框架会在鼠标位置弹菜单，看着像个跟球无关的系统右键菜单；
            // 改成 OnMouseUp 里自己调 ShowMenuNextToBall()，让菜单贴着球出现。
        }

        private static ToolStripMenuItem NewItem(string text, Image icon)
        {
            return new ToolStripMenuItem(text, icon)
            {
                ImageScaling = ToolStripItemImageScaling.None,
                Padding = new Padding(Sc(9), Sc(5), Sc(22), Sc(5)),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private static Bitmap NewIconCanvas(out Graphics g)
        {
            int px = Sc(MENU_ICON_SIZE);
            var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            // 后面所有图标的绘制坐标一律按 ICON_DESIGN(24) 的设计稿写，
            // 这里一次性换算到实际画布：高分屏上是"用更多像素重画一遍"，不是把小图拉大，不会糊；
            // 想调图标大小只改 MENU_ICON_SIZE，图标代码一行都不用动。
            float k = px / ICON_DESIGN;
            g.ScaleTransform(k, k);
            return bmp;
        }

        /// <summary>🔍 蓝色放大镜（快速扫描）。24px 画布，实心镜片 + 粗描边，远看接近 emoji 的分量感。</summary>
        private static Bitmap MakeScanIcon()
        {
            Graphics g;
            var bmp = NewIconCanvas(out g);
            using (g)
            {
                var lens = new RectangleF(2.4f, 2.4f, 14.4f, 14.4f);

                // 镜片：浅蓝玻璃 + 左上角一道白色高光
                using (var glass = new SolidBrush(LensGlass))
                    g.FillEllipse(glass, lens);
                using (var shine = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
                    g.FillPie(shine, lens.X, lens.Y, lens.Width, lens.Height, 150f, 85f);

                // 手柄：深灰打底，再叠一段浅灰做金属反光
                using (var pen = new Pen(LensGripDark, 4.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, 16.6f, 16.6f, 21.4f, 21.4f);
                using (var pen = new Pen(LensGripLite, 4.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, 16.6f, 16.6f, 19.2f, 19.2f);

                // 镜框最后画，压住手柄根部
                using (var pen = new Pen(LensRim, 2.9f))
                    g.DrawEllipse(pen, lens);
            }
            return bmp;
        }

        /// <summary>🏠 蓝色小房子（打开主窗口）。实心屋顶 + 淡蓝墙体 + 实心门，双色调实心风格。</summary>
        private static Bitmap MakeHomeIcon()
        {
            Graphics g;
            var bmp = NewIconCanvas(out g);
            using (g)
            {
                // 墙体（先画，屋顶盖住上沿）
                using (var path = RoundedRect(new RectangleF(4.6f, 10.4f, 14.8f, 11.5f), 1.8f))
                {
                    using (var fill = new SolidBrush(WallCream))
                        g.FillPath(fill, path);
                    using (var pen = new Pen(WallEdge, 1.6f))
                        g.DrawPath(pen, path);
                }

                // 屋顶：整体砖红，右半边压一层暗色做体积感
                using (var roof = new SolidBrush(RoofRed))
                {
                    g.FillPolygon(roof, new[]
                    {
                        new PointF(12f, 1.6f),
                        new PointF(23.2f, 11.6f),
                        new PointF(0.8f, 11.6f)
                    });
                }
                using (var shade = new SolidBrush(RoofShade))
                {
                    g.FillPolygon(shade, new[]
                    {
                        new PointF(12f, 1.6f),
                        new PointF(23.2f, 11.6f),
                        new PointF(12f, 11.6f)
                    });
                }

                // 木门（放大居中）。原来还画了两扇 2.8px 的小窗，
                // 在 24px 画布上太碎，远看只是两个灰点，删掉更干净。
                using (var path = RoundedRect(new RectangleF(9.4f, 14.4f, 5.2f, 7.5f), 1.6f))
                {
                    using (var fill = new SolidBrush(DoorBrown))
                        g.FillPath(fill, path);
                    using (var knob = new SolidBrush(WinBlue))
                        g.FillEllipse(knob, 13f, 18.1f, 1.3f, 1.3f);
                }
            }
            return bmp;
        }

        /// <summary>☑️ 对勾（隐藏设置）。勾上绿底白勾，没勾灰色空框，开关状态一眼可辨。</summary>
        private static Bitmap MakeCheckIcon(bool on)
        {
            Graphics g;
            var bmp = NewIconCanvas(out g);
            using (g)
            {
                if (on)
                {
                    using (var path = RoundedRect(new RectangleF(2.4f, 2.4f, 19.2f, 19.2f), 6f))
                    using (var fill = new SolidBrush(CheckGreen))
                        g.FillPath(fill, path);

                    using (var pen = new Pen(Color.White, 3f)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round,
                        LineJoin = LineJoin.Round
                    })
                    {
                        g.DrawLines(pen, new[]
                        {
                            new PointF(6.9f, 12.2f),
                            new PointF(10.4f, 15.8f),
                            new PointF(17.1f, 8f)
                        });
                    }
                }
                else
                {
                    // 未勾选：浅灰实心方框 + 稍深描边。
                    // 之前只画一圈细线，在高分屏上又细又淡，看着像"没画完"而不是"没勾选"。
                    using (var path = RoundedRect(new RectangleF(2.4f, 2.4f, 19.2f, 19.2f), 6f))
                    {
                        using (var fill = new SolidBrush(CheckOffFill))
                            g.FillPath(fill, path);
                        using (var pen = new Pen(CheckOffEdge, 1.8f))
                            g.DrawPath(pen, path);
                    }
                }
            }
            return bmp;
        }

        /// <summary>❌ 红色叉号（退出程序）。</summary>
        private static Bitmap MakeExitIcon()
        {
            Graphics g;
            var bmp = NewIconCanvas(out g);
            using (g)
            {
                // 淡红底盘：万一图标被外部逻辑缩放或裁切，"红色"这个信号也丢不掉
                using (var disc = new SolidBrush(Color.FromArgb(38, 245, 63, 63)))
                    g.FillEllipse(disc, 1.2f, 1.2f, 21.6f, 21.6f);

                using (var pen = new Pen(RedDanger, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, 6.6f, 6.6f, 17.4f, 17.4f);
                    g.DrawLine(pen, 17.4f, 6.6f, 6.6f, 17.4f);
                }
            }
            return bmp;
        }

        /// <summary>
        /// 圆角菜单窗体。Win11 优先用 DWM 原生圆角（边缘平滑、带系统阴影）；
        /// DWM 不支持（Win10 及更早）时退回 Region 裁剪，保证一定是圆角。
        /// </summary>
        private class RoundedMenu : ContextMenuStrip
        {
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ApplyShape();
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                ApplyShape();
            }

            private void ApplyShape()
            {
                if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

                int hr;
                try
                {
                    int preference = DWMWCP_ROUND;
                    hr = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                }
                catch
                {
                    hr = -1;
                }

                if (hr == 0)
                {
                    // 系统已经把窗口圆角化了，自己再裁一次反而会有锯齿
                    var previous = Region;
                    if (previous != null) { Region = null; previous.Dispose(); }
                    return;
                }

                using (var path = RoundedRect(new RectangleF(0f, 0f, Width, Height), MENU_RADIUS * _uiScale))
                {
                    var previous = Region;
                    Region = new Region(path);
                    if (previous != null) previous.Dispose();
                }
            }
        }

        /// <summary>macOS 风格菜单绘制：白底微渐变、圆角选中块、浅蓝底品牌蓝字。</summary>
        private class MacMenuRenderer : ToolStripProfessionalRenderer
        {
            public MacMenuRenderer() : base(new MacColorTable()) { }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                var g = e.Graphics;
                int h = e.ToolStrip.Height;
                if (h <= 0) { base.OnRenderToolStripBackground(e); return; }

                using (var lg = new LinearGradientBrush(
                    new Rectangle(0, -1, Math.Max(1, e.ToolStrip.Width), h + 2),
                    Color.White,
                    Color.FromArgb(0xF7, 0xF9, 0xFC),
                    LinearGradientMode.Vertical))
                {
                    g.FillRectangle(lg, e.AffectedBounds);
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 和球、提示条同一套品牌色渐变描边，一眼看出是同一个东西弹出来的
                float w = 1.8f * _uiScale;
                var rect = new RectangleF(w / 2f, w / 2f,
                                          e.ToolStrip.Width - w, e.ToolStrip.Height - w);
                if (rect.Width <= 0f || rect.Height <= 0f) return;

                using (var path = RoundedRect(rect, MENU_RADIUS * _uiScale))
                using (var brush = new LinearGradientBrush(
                    new RectangleF(rect.X - 1f, rect.Y - 1f, rect.Width + 2f, rect.Height + 2f),
                    RingLite, RingDark, LinearGradientMode.ForwardDiagonal))
                using (var pen = new Pen(brush, w))
                {
                    g.DrawPath(pen, path);
                }
            }

            /// <summary>不画左边那条灰色图标栏，整片都是白的。</summary>
            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
            }

            /// <summary>
            /// 图标完全自己画，绕开框架自带的那条绘制路径。
            /// 走单位颜色矩阵 = 不做任何颜色变换；按位图原尺寸居中 1:1 输出 = 不缩放、不裁切。
            /// 这样红叉一定是红的、蓝房子一定是蓝的，跟渲染器、跟文字颜色都没有关系。
            /// （框架默认路径在 ImageScaling=None 时会用"图标框尺寸"当源矩形，
            ///   图标框比位图小的时候只会画出位图左上角那一块，这里一并规避掉。）
            /// </summary>
            protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
            {
                var image = e.Image;
                if (image == null) return;

                var box = e.ImageRectangle;
                if (box.Width <= 0 || box.Height <= 0) return;

                int x = box.X + (box.Width - image.Width) / 2;
                int y = box.Y + (box.Height - image.Height) / 2;

                using (var attr = new ImageAttributes())
                {
                    attr.SetColorMatrix(new ColorMatrix());   // 单位矩阵：原色进、原色出
                    e.Graphics.DrawImage(image,
                        new Rectangle(x, y, image.Width, image.Height),
                        0, 0, image.Width, image.Height,
                        GraphicsUnit.Pixel, attr);
                }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var item = e.Item;
                if (item == null || !item.Selected || !item.Enabled) return;

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                float inset = 3.5f * _uiScale;
                var rect = new RectangleF(inset, inset * 0.4f,
                                          item.Width - inset * 2f, item.Height - inset * 0.8f);
                if (rect.Width <= 0f || rect.Height <= 0f) return;

                using (var path = RoundedRect(rect, 7f * _uiScale))
                using (var brush = new SolidBrush(MenuHover))
                {
                    g.FillPath(brush, path);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item != null && e.Item.Selected && e.Item.Enabled)
                    e.TextColor = BrandBlue;
                else
                    e.TextColor = TextMain;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.None;
                int y = e.Item.Height / 2;
                int left = Sc(10);
                int right = e.Item.Width - left;
                if (right <= left) return;

                using (var pen = new Pen(Color.FromArgb(0xEE, 0xF1, 0xF6), Sc(1)))
                {
                    g.DrawLine(pen, left, y, right, y);
                }
            }
        }

        private class MacColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return MenuHover; } }
            public override Color MenuItemSelectedGradientBegin { get { return MenuHover; } }
            public override Color MenuItemSelectedGradientEnd { get { return MenuHover; } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color MenuBorder { get { return HairLine; } }
            public override Color ToolStripDropDownBackground { get { return Color.White; } }
            public override Color ImageMarginGradientBegin { get { return Color.White; } }
            public override Color ImageMarginGradientMiddle { get { return Color.White; } }
            public override Color ImageMarginGradientEnd { get { return Color.White; } }
            public override Color SeparatorDark { get { return Color.FromArgb(0xEE, 0xF1, 0xF6); } }
            public override Color SeparatorLight { get { return Color.Transparent; } }
        }

        #endregion
    }
}
