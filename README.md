# 深澈 Vincel

> 新机到手，一键清爽。专门清理国内捆绑软件和品牌预装冗余软件的系统优化工具。

![License](https://img.shields.io/badge/license-GPLv3-blue.svg)
![Platform](https://img.shields.io/badge/platform-Windows%207%2B-lightgrey.svg)
![.NET](https://img.shields.io/badge/.NET-Framework%204.7.2-informational.svg)

## 项目介绍

深澈（Vincel）是一款针对国内环境的系统清理工具，专门解决国内常见的捆绑安装与预装冗余软件问题，覆盖主流品牌电脑的预装软件和常见捆绑组件。

核心代码开源透明，无广告、无捆绑、无后门，所有清理操作自动备份，可一键撤销。

## 核心功能

### 免费版（GPLv3 开源）
- 🧹 **一键强制卸载**：深度扫描捆绑软件、品牌预装、后台服务、计划任务、注册表残留和文件垃圾，管理员权限彻底删除
- 💾 **自动备份可撤销**：清理前自动备份文件和注册表，误删可一键恢复
- 📦 **安装包监控**：实时监控安装包运行，提醒捆绑风险，只提醒不拦截
- 📊 **弹窗来源统计**：统计广告弹窗来源，只统计不关闭
- ⚡ **开机自启管理**：管理开机启动项，加快开机速度
- 🎈 **桌面悬浮球**：一键快速扫描，实时显示今日弹窗和安装包检测数
- 📚 **特征库手动更新**：300+ 捆绑与预装软件特征，持续更新

### 专业版（商业闭源，独立插件）
- 🛡️ **安装包实时拦截**：自动拦截捆绑软件偷偷安装
- 🔕 **智能弹窗拦截**：自动关闭广告弹窗
- 🏠 **主页篡改防护**：防止浏览器主页被篡改
- 👁️ **进程行为监控**：监控可疑进程行为
- ☁️ **云特征库实时更新**：特征库实时同步最新威胁
- 🎧 **优先技术支持**：专属技术支持通道

## 系统要求

- **操作系统**：Windows 7 / 8 / 10 / 11（32/64 位）
- **运行环境**：.NET Framework 4.7.2（Win10/11 自带，Win7/8 需安装）
- **WebView2**：微软 Edge WebView2 运行时（Win11 自带，其他系统安装包会自动安装）
- **磁盘空间**：≥ 50MB

## 构建说明

### 环境要求
- Visual Studio 2019 或更高版本（支持 .NET Framework 4.7.2）
- NuGet 包还原

### 构建步骤
1. 克隆仓库
   ```bash
   git clone https://github.com/你的用户名/vincel.git
   cd vincel
   ```
2. 用 Visual Studio 打开 `vincel.slnx`
3. 右键解决方案 → 还原 NuGet 程序包
4. 按 F5 编译运行，或右键项目 → 生成

### 发布构建
运行 `build-release.ps1` 或 `发版.bat` 生成发布版本（安装包 + 绿色版 zip）。

前置条件：
1. 安装 **Inno Setup 6**（https://jrsoftware.org/isdl.php）
2. 从微软官方下载 **WebView2 Evergreen 引导安装包**（https://go.microsoft.com/fwlink/p/?LinkId=2124703），命名为 `MicrosoftEdgeWebview2Setup.exe` 放入 `WebView2Runtime\` 目录（该文件不随仓库分发，已被 `.gitignore` 忽略；缺失时绿色版将不含 WebView2 引导，安装版编译会失败）

## 项目结构

```
├── Scanner.cs              # 核心扫描引擎
├── Cleaner.cs              # 强制卸载引擎
├── BackupManager.cs        # 备份与撤销管理
├── Signatures.cs           # 捆绑与预装软件特征库
├── InstallWatcher.cs       # 安装包监控
├── PopupCounter.cs         # 弹窗统计
├── FloatingBall.cs         # 桌面悬浮球
├── MainForm.cs             # 主窗口（WebView2 宿主）
├── JsBridge.cs             # 前后端通信桥
├── AutoStartHelper.cs      # 开机自启管理
├── TelemetryService.cs     # 匿名遥测服务
├── UpdateService.cs        # 自动更新服务
├── LocalWebServer.cs       # 本地 HTTP 服务
├── Program.cs              # 程序入口
├── wwwroot/                # 前端 UI（HTML/CSS/JS 单文件）
│   ├── index.html          # 主界面
│   └── lib/                # 第三方库
├── VincelSetup.iss         # Inno Setup 安装脚本
├── build-release.ps1       # 发布构建脚本
└── vincel.csproj # 项目文件
```

## 技术架构

- **混合架构**：C# 核心层 + WebView2 前端 UI 层
  - 核心层：纯 C# 实现扫描/清理/备份等业务逻辑，无 UI 依赖
  - UI 层：HTML/CSS/JS 单文件实现，macOS 风格设计
  - 通信层：WebView2 `AddHostObjectToScript` 实现前后端互调
  - 插件层：专业版功能做成独立 dll，动态加载，实现双授权

## 双授权说明

本项目采用**双授权模式**：

- **免费版**：GPLv3 协议开源，包含本仓库所有代码，可自由使用、修改、分发
- **专业版**：商业闭源授权，以独立插件包形式提供，不包含在本仓库中，动态加载，不属于 GPL 衍生作品

专业版插件与免费版核心通过公开 API 通信，是独立作品，不违反 GPL 协议。

## 隐私与安全

- 所有清理操作**本地执行**，不上传任何文件或扫描明细
- 匿名遥测仅上报：设备 ID、事件类型、计数、版本号、系统版本，用于改进产品
- 用户可在设置页随时关闭匿名统计
- 设备 ID 本地生成，不关联任何个人身份信息
- 云数据库权限设置为"所有用户不可读写"，仅开发者可通过云函数访问

## 云端服务说明

- 匿名遥测与样本提交使用腾讯云 CloudBase（文档数据库 + 云函数），云函数代码**不包含在本仓库中**（仅开发者通过云端控制台维护）
- 客户端通过 CloudBase SDK 匿名登录后调用云函数，环境 ID 等配置位于 `wwwroot/index.html`，属于公开信息
- 云端集合权限为"所有用户不可读写"，数据仅能通过云函数访问；云函数内置基础防刷限制
- 如需自行部署云端服务，可参照上述说明在腾讯云 CloudBase 创建环境并实现等价云函数（`telemetry` / `ping` / `stats` / `submitSample`）

## 贡献指南

欢迎提交 Issue 和 Pull Request！

提交代码前请注意：
1. 保持代码风格与现有代码一致
2. 不要提交 `bin/`、`obj/`、`.vs/` 等编译产物
3. 不要提交任何包含个人信息或密钥的配置文件
4. 大型功能建议先开 Issue 讨论

**重要**：由于项目采用双授权模式，所有贡献的代码将纳入 GPLv3 开源协议，专业版闭源插件不接受外部贡献。

## 免责声明

本软件按"现状"提供，开发者不对软件的正确性、可靠性做任何明示或默示的担保。使用本软件产生的所有风险由用户自行承担，清理前请务必备份重要数据。

本软件仅清理存在捆绑安装行为的软件和冗余程序，不会修改系统核心文件，但不保证 100% 无误删。

## 开源协议

本项目免费版代码基于 **GNU GPLv3** 协议开源，详见 [LICENSE](LICENSE) 文件。

专业版为商业闭源授权，不在本仓库范围内。
