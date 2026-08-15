using System.Collections.Generic;

namespace WindowsFormsApp1
{
    /// <summary>
    /// 流氓软件特征库。
    ///
    /// 收录原则（很重要，后续加词请遵守）：
    ///   1. 只收录"后台服务 / 更新器 / 捆绑安装器 / 主页劫持"这类用户没主动装的东西；
    ///      用户自己装来用的应用（播放器、浏览器主程序、办公软件）不收录。
    ///   2. 不收录 4 个字符以下的关键词，子串误撞概率太高。
    ///   3. 不收录 autoupdate / checkupdate / adservice 这类"看着像"的通用词——
    ///      Windows 自带服务 tzautoupdate 就是因为撞上 autoupdate 被误判成高危的。
    ///   4. 每条都应该来自真实确认过的样本，不确定就别加。
    /// </summary>
    public static class Signatures
    {
        public static readonly Dictionary<string, string> BadKeywords = new Dictionary<string, string>
        {
            // ===== 360 系列 =====
            {"360safe", "360安全卫士，带全家桶和广告"},
            {"360se", "360安全浏览器，带广告弹窗"},
            {"360chrome", "360极速浏览器"},
            {"360zip", "360压缩，带广告弹窗"},
            {"360desktop", "360桌面助手"},
            {"360wallpaper", "360壁纸"},
            {"360antivirus", "360杀毒"},
            {"360sd", "360杀毒"},
            {"360tray", "360托盘程序"},
            {"360speedld", "360加速球"},
            {"360bdoctor", "360浏览器医生"},
            {"360rp", "360杀毒实时防护"},
            {"360leakfixer", "360漏洞修复"},
            {"softmgr", "360软件管家，捆绑安装"},
            {"360softmgr", "360软件管家，捆绑安装"},
            {"360update", "360更新程序"},
            {"360doctor", "360电脑专家"},
            {"360appmgr", "360应用管理器"},
            {"360game", "360游戏大厅"},
            {"360webshield", "360网页防护"},
            {"360safeupdate", "360安全卫士更新"},
            {"360safemon", "360安全卫士监控"},
            {"360liveupd", "360升级程序"},
            {"360seupd", "360浏览器更新"},
            {"360entclient", "360企业版客户端"},
            {"360totalsecurity", "360 Total Security"},
            {"360yunpan", "360云盘"},
            {"360wifi", "360免费WiFi"},
            {"360driver", "360驱动大师"},
            {"360drm", "360DRM保护"},
            {"360netmon", "360网络监控"},
            {"360wpsrv", "360后台服务"},

            // ===== 2345 系列 =====
            {"2345explorer", "2345浏览器，难以卸载"},
            {"2345soft", "2345软件大全"},
            {"2345chrome", "2345加速浏览器"},
            {"2345zip", "2345好压，带广告"},
            {"2345input", "2345输入法"},
            {"2345pic", "2345看图王"},
            {"2345video", "2345影视大全"},
            {"2345safe", "2345安全卫士"},
            {"2345speed", "2345加速浏览器"},
            {"2345browser", "2345浏览器"},
            {"2345homepage", "2345主页锁定"},
            {"2345desktop", "2345桌面"},
            {"2345protect", "2345保护服务"},
            {"2345update", "2345更新程序"},
            {"2345mp", "2345王牌输入法"},
            {"2345pdf", "2345PDF阅读器"},
            {"2345pinyin", "2345拼音输入法"},
            {"2345security", "2345安全防护"},
            {"2345tray", "2345托盘程序"},
            {"2345news", "2345新闻弹窗"},
            {"2345helper", "2345浏览器助手"},
            {"2345urlproc", "2345网址监控"},
            {"2345weather", "2345天气"},
            {"2345game", "2345游戏中心"},
            {"2345mobilemgr", "2345手机助手"},
            {"2345appmgr", "2345应用管理"},
            {"haozip", "2345好压压缩"},

            // ===== 金山 / 猎豹 =====
            {"kingsoft antivirus", "金山毒霸，带广告"},
            {"kingsoft internet security", "金山毒霸安全套装"},
            {"dubakernel", "金山毒霸内核"},
            {"dubainetshield", "金山毒霸网盾"},
            {"dubausbshield", "金山毒霸U盘防护"},
            {"duba", "金山毒霸"},
            {"ksafe", "金山卫士"},
            {"ksafetray", "金山卫士托盘"},
            {"kxetray", "金山毒霸托盘"},
            {"kxecore", "金山毒霸核心"},
            {"kupdata", "金山毒霸更新"},
            {"kupdatetray", "金山更新托盘"},
            {"liebao", "猎豹浏览器，带广告"},
            {"liebaosafe", "猎豹安全浏览器"},
            {"cheetah", "猎豹浏览器"},

            // ===== 百度 =====
            {"baiduan", "百度杀毒"},
            {"baidusd", "百度杀毒"},
            {"baidu antivirus", "百度杀毒"},
            {"baidubrowser", "百度浏览器"},
            {"baidubrowserupdate", "百度浏览器更新"},
            {"baiduplayer", "百度影音，带弹窗"},
            {"baidup2pservice", "百度P2P服务"},
            {"baiduansvc", "百度安全服务"},
            {"baidujp", "百度日语输入法"},
            {"baiduhotspot", "百度WiFi热点"},
            {"baiduprotect", "百度保护服务"},
            {"baiduupdate", "百度更新服务"},
            {"baiduguard", "百度卫士"},
            {"baidutray", "百度托盘"},
            {"baidunetdiskupdate", "百度网盘更新组件（非网盘本身）"},
            {"baiduhomepage", "百度主页锁定"},
            {"baiduads", "百度广告插件"},
            {"baidusvc", "百度后台服务"},
            {"hao123", "hao123导航，篡改主页"},

            // ===== 腾讯（后台组件，不含QQ/微信本体） =====
            {"qqpcmgr", "腾讯电脑管家"},
            {"qqpctray", "电脑管家托盘"},
            {"qqpcrtp", "电脑管家实时防护"},
            {"qqpcnetflow", "电脑管家网络监控"},
            {"qqpcmgrsetup", "电脑管家安装器"},
            {"qqpcmgrsvc", "电脑管家服务"},
            {"qqpcupdate", "电脑管家更新"},
            {"qqpcleakscan", "电脑管家漏洞扫描"},
            {"qqpcsoftmgr", "电脑管家软件管理"},
            {"tencentpcmgr", "腾讯电脑管家"},
            {"qqbrowser", "QQ浏览器，带广告"},
            {"qqbrowserservice", "QQ浏览器后台服务"},
            {"qqbrowserupdate", "QQ浏览器更新"},
            {"tencentdl", "腾讯下载组件"},
            {"teniodl", "腾讯P2P下载器"},

            // ===== 驱动类（普遍带捆绑） =====
            {"ludashi", "鲁大师，带广告捆绑"},
            {"驱动精灵", "驱动精灵，带捆绑"},
            {"驱动人生", "驱动人生，带捆绑"},
            {"mydrivers", "驱动精灵"},
            {"drivethelife", "驱动人生"},
            {"drivergenius", "驱动精灵"},
            {"driverbooster", "驱动精灵国际版"},
            {"easydrv", "EasyDrv驱动总裁，带捆绑"},
            {"itiankong", "IT天空驱动包，带捆绑"},
            {"wandrv", "WanDrv驱动助理"},

            // ===== 压缩工具（广告型） =====
            {"kuaizip", "快压压缩，带广告"},
            {"快压", "快压压缩，带广告"},
            {"布丁压缩", "布丁压缩，广告软件"},
            {"压搜", "压搜压缩软件"},
            {"万能压缩", "万能压缩，广告软件"},
            {"压缩宝", "压缩宝，广告软件"},
            {"jzip", "JZip压缩，带捆绑"},
            {"zipgenius", "ZipGenius带广告"},

            // ===== 捆绑型浏览器（通常非用户主动安装） =====
            {"桔子浏览器", "桔子浏览器，捆绑安装"},
            {"极速浏览器", "极速浏览器，捆绑安装"},
            {"超速浏览器", "超速浏览器，捆绑安装"},
            {"小白浏览器", "小白浏览器，广告软件"},
            {"七星浏览器", "七星浏览器，捆绑安装"},
            {"彩云浏览器", "彩云浏览器，广告"},
            {"蚂蚁浏览器", "蚂蚁浏览器，带广告"},
            {"115浏览器", "115浏览器，捆绑"},
            {"糖果浏览器", "糖果浏览器，广告"},
            {"飞鱼浏览器", "飞鱼浏览器，捆绑"},
            {"瑞星浏览器", "瑞星浏览器，广告"},
            {"h5browser", "H5浏览器，捆绑"},
            {"yabrowser", "Yandex浏览器（国内捆绑版）"},
            {"ucbrowser", "UC浏览器电脑版，带广告"},

            // ===== 输入法广告组件（不含输入法本体） =====
            {"sogoutray", "搜狗输入法托盘广告"},
            {"sogoucloud", "搜狗云计算，弹窗"},

            // ===== 壁纸 / 桌面 / 屏保类 =====
            {"布丁桌面", "布丁桌面，广告软件"},
            {"小黑壁纸", "小黑壁纸，广告软件"},
            {"多多壁纸", "多多壁纸，广告软件"},
            {"桔子壁纸", "桔子壁纸，广告软件"},
            {"多惠屏保", "多惠屏保，广告弹窗"},
            {"duohuipingbao", "多惠屏保"},
            {"dhpingbao", "多惠屏保"},
            {"小黑记事本", "小黑记事本，广告软件"},
            {"迷你记事本", "迷你记事本，广告软件"},
            {"酷点桌面", "酷点桌面，广告"},
            {"好桌道壁纸", "好桌道壁纸，捆绑"},
            {"搜狗壁纸", "搜狗壁纸，广告"},
            {"百度壁纸", "百度壁纸，广告"},
            {"元气桌面", "元气桌面，会员弹窗"},
            {"小鸟壁纸", "小鸟壁纸，广告"},
            {"飞火动态壁纸", "飞火动态壁纸，捆绑"},

            // ===== PDF / 看图（广告型） =====
            {"小黑PDF", "小黑PDF阅读器"},
            {"华军PDF", "华军PDF阅读器"},
            {"huajunpdf", "华军PDF"},
            {"极速PDF", "极速PDF，广告"},
            {"闪电PDF", "闪电PDF，捆绑"},
            {"看图王", "2345看图王，广告"},
            {"美图看看", "美图看看，捆绑"},
            {"极速看图", "极速看图，广告"},
            {"万能看图王", "万能看图王，广告"},

            // ===== 下载器 / 播放器（难卸载、弹窗骚扰型） =====
            {"thunder", "迅雷，弹窗广告"},
            {"thunderplatform", "迅雷后台服务组件"},
            {"xunleiupdate", "迅雷更新组件"},
            {"迅雷影音", "迅雷影音，广告"},
            {"kugou", "酷狗音乐，带弹窗"},
            {"kuwo", "酷我音乐，带弹窗"},
            {"qqlive", "腾讯视频，弹窗广告"},
            {"iqiyi", "爱奇艺，弹窗广告"},
            {"youku", "优酷，弹窗广告"},
            {"letv", "乐视视频，广告"},
            {"pptv", "PPTV，广告弹窗"},
            {"ppstream", "PPS，广告弹窗"},
            {"funshion", "风行视频，广告"},
            {"baofeng", "暴风影音，广告捆绑"},
            {"stormplayer", "暴风影音"},
            {"verycd", "VeryCD电驴，捆绑"},

            // ===== 输入法（广告弹窗型） =====
            {"sogouinput", "搜狗输入法，广告弹窗"},
            {"baiduinput", "百度输入法，带广告"},

            // ===== 国产杀软（难卸载型） =====
            {"rising", "瑞星杀毒，难以卸载"},
            {"ravmond", "瑞星监控"},
            {"rsmain", "瑞星主程序"},
            {"ravtray", "瑞星托盘"},
            {"ravupdate", "瑞星更新"},
            {"jiangmin", "江民杀毒"},
            {"kvmon", "江民监控"},
            {"kvxp", "江民杀毒"},
            {"mcafee", "迈克菲预装版，弹窗"},
            {"norton", "诺顿预装版，弹窗"},

            // ===== 工具箱 / 下载站捆绑器 =====
            {"wintoolbox", "Win工具箱，带广告弹窗"},
            {"wintoolsrv", "Win工具箱服务"},
            {"winlauncher", "Win工具箱启动器"},
            {"huajun", "华军下载器，带捆绑"},
            {"duote", "多特下载器，带捆绑"},
            {"downza", "下载之家捆绑器"},
            {"cr173", "统一下载站捆绑器"},
            {"xiazaiba", "下载吧捆绑器"},
            {"xitongzhijia", "系统之家捆绑器"},
            {"xitongcheng", "系统城捆绑器"},
            {"win7china", "Win7之家捆绑器"},
            {"zhaodll", "找DLL站捆绑器"},
            {"dllzj", "DLL之家捆绑器"},
            {"softonic", "Softonic下载器，捆绑"},
            {"hcpatchinstaller", "补丁安装器残留"},

            // ===== 主页劫持 / 锁定类 =====
            {"browsemngr", "浏览器主页锁定"},
            {"urlguard", "网址锁定"},
            {"homepageguard", "主页卫士（篡改主页）"},
            {"homepagelock", "主页锁定"},
            {"browserprotect", "浏览器保护（篡改主页）"},
            {"browsersafeguard", "浏览器卫士（广告）"},
            {"websafeguard", "网页卫士（广告）"},
            {"internetprotector", "上网保护器（广告）"},
            {"onlinenetguard", "网络卫士（广告）"},

            // ===== 游戏盒子（捆绑型） =====
            {"yxdown", "游迅网，捆绑安装"},
            {"youxibao", "游戏盒子，广告"},
            {"4399box", "4399游戏盒，弹窗"},
            {"7k7kbox", "7k7k游戏盒，弹窗"},
            {"duowanbox", "多玩游戏盒，广告"},
            {"17173box", "17173游戏盒，捆绑"},
            {"wanmei", "完美游戏盒子，弹窗"},
            {"37game", "37游戏盒子，广告"},
            {"9377", "9377游戏盒子，弹窗"},
            {"youxikuang", "游戏狂盒子，捆绑"},
            {"xygame", "XY游戏盒子，广告"},
            {"leihuo", "雷火游戏盒子，弹窗"}
        };

        /// <summary>
        /// 白名单。命中后不会立刻放行——见 Scanner.IsBadSoftware 的"最长匹配优先"逻辑：
        /// 只有当白名单匹配到的词 >= 黑名单匹配到的词长度时，才判定为安全。
        /// 所以这里可以放 "qq" 这种短词，不会再挡住 "qqpcmgr" 的检测。
        /// </summary>
        public static readonly string[] WhiteList = {
            // 系统与核心组件
            "microsoft", "windows", "win32", "windows nt",
            "svchost", "taskhostw", "service host", "local service", "network service",
            "activex", "device setup", "windows update", "modules installer",
            "network connections", "windows defender", "securityhealth", "firewall",
            "scheduleddefrag", "defrag", "chkdsk", "systemrestore",
            "tzautoupdate", "时区更新",

            // 硬件厂商 / 驱动
            "nvidia", "nvidia container", "amd", "amd software", "amd radeon",
            "intel", "intel graphics", "realtek", "realtek audio",
            "nahimic", "nahimic audio", "synaptics", "synaptics pointing",
            "honor", "honor pc manager", "huawei", "huawei pc manager",
            "lenovo", "lenovo utility", "dell", "dell support",
            "hp support", "asus", "asus armoury",

            // 常用正版软件
            "kingsoft wps", "wps office", "wps", "wpscloudsvr", "kingsoft pdf",
            "google", "google update", "google drive", "chrome", "edge", "microsoftedge",
            // Thunderbird 邮箱软件：黑名单 thunder 会误命中它，白名单 thunderbird(11字符) > thunder(7字符)，最长匹配判安全
            "firefox", "mozilla", "thunderbird", "libreoffice",
            "adobe", "photoshop", "acrobat", "office",
            "7-zip", "7zip", "winrar", "bandizip",
            "notepad++", "everything", "geek", "geek uninstaller",
            "huorong", "sandboxie", "vmware", "vmware workstation",
            "virtualbox", "virtualbox guest",

            // 开发工具
            "visual studio", "vs code", "vscode", "git", "git bash",
            "node", "node.exe", "npm", "python",
            "docker", "docker desktop", "postman", "mysql", "redis", "redis server",

            // 社交 / 办公 / 网盘
            "wechat", "weixin", "微信", "企业微信", "钉钉", "qq",
            "腾讯会议", "tencent meeting", "tencentmeeting",
            "discord", "telegram", "zoom",
            "onedrive", "dropbox", "baidunetdisk", "百度网盘",
            "doubao", "bytedance", "alibaba",

            // 用户主动安装、且没有弹窗骚扰问题的媒体应用
            // 注意：迅雷/酷狗/酷我/爱奇艺/优酷/腾讯视频/搜狗输入法/百度输入法
            // 已按"难卸载或弹窗骚扰"标准移入黑名单，这里不能再出现同名词，
            // 否则最长匹配时会两边打平、白名单胜出，黑名单条目直接失效。
            "qqmusic", "qq音乐", "网易云音乐", "spotify",
            "vlc", "vlc media", "potplayer", "mpc-hc",
            "steam", "epic", "ubisoft", "blender",

            // 其他
            "驾考宝典", "jxedt", "multipad",
            "biubiu", "bluestacks",
            "汽水音乐", "zoogvpn", "elevoc", "大象声科"
        };

        public static readonly string[] BadHomepages = {
            "2345.com", "www.2345.com", "2345.net", "2345.cc", "2345.org",
            "i.2345.com", "se.2345.com", "ie.2345.com", "chrome.2345.com",
            "home.2345.com", "2345ex.com", "2345ie.com", "2345chrome.com",
            "2345daohang.com", "2345tv.com",
            "hao123.com", "www.hao123.com", "i.hao123.com", "hao222.com",
            "hao.360.cn", "t.360.cn", "360.com", "360kan.com",
            "123.sogou.com", "sogou123.com", "sogouhome.com", "sougou.com",
            "baidudaohang.com", "daohang.com", "u360.com",
            "114la.com", "23456.com", "114680.com", "7322.com", "9991.com",
            "6700.cn", "kuku123.com", "qq5.com", "tao123.com", "1616.net",
            "tt98.com", "you2000.com", "wysgsb.com",
            "bendi123.com", "diannaozhijia.com", "xitongzhijia.net",
            "verycd.com", "duote.com", "pc6.com", "xiazaiba.com", "crsky.com"
        };
    }
}