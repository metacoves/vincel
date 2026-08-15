using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace WindowsFormsApp1{
 /// <summary>
 ///自动更新：检查更新清单、下载安装包、校验哈希后交给安装器静默安装。
 ///更新清单(update.json)和安装包(VincelSetup.exe)放在官网仓库根目录。
 /// </summary>
 public static class UpdateService {
 //更新清单地址（与官网域名保持一致）
 private const string UpdateUrl = "https://vincel.netlify.app/update.json";

 private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();

 /// <summary>
 ///检查更新，返回JSON：hasUpdate/version/notes/force/url/sha256/error。
 /// </summary>
 public static async Task<string> CheckAsync()
 {
 try {
 string json;
 using (var client = new WebClient())
 {
 client.Headers[HttpRequestHeader.UserAgent] = "Vincel/" + Application.ProductVersion;
 json = await client.DownloadStringTaskAsync(UpdateUrl);
 }

 var info = _json.Deserialize<UpdateInfo>(json);
 if (info == null || string.IsNullOrEmpty(info.version) || string.IsNullOrEmpty(info.url))
 {
 return _json.Serialize(new { hasUpdate = false });
 }

 bool hasUpdate = ParseVersion(info.version) > ParseVersion(Application.ProductVersion);
 return _json.Serialize(new {
 hasUpdate = hasUpdate,
 version = info.version,
 notes = info.notes ?? "",
 force = info.force,
 url = info.url,
 sha256 = info.sha256 ?? ""
 });
 }
 catch (Exception e)
 {
 return _json.Serialize(new { hasUpdate = false, error = "检查更新失败：" + e.Message });
 }
 }

 /// <summary>
 ///下载安装包并静默安装，返回JSON：ok/msg。安装完成后程序退出，安装器负责重启应用。
 /// </summary>
 public static async Task<string> InstallAsync(string url, string sha256)
 {
 string dir = Path.Combine(Path.GetTempPath(), "VincelUpdate");
 try {
 if (Directory.Exists(dir)) Directory.Delete(dir, true);
 Directory.CreateDirectory(dir);

 string setupPath = Path.Combine(dir, "VincelSetup.exe");
 using (var client = new WebClient())
 {
 client.Headers[HttpRequestHeader.UserAgent] = "Vincel/" + Application.ProductVersion;
 await client.DownloadFileTaskAsync(new Uri(url), setupPath);
 }

 //校验哈希，防止下载损坏或文件被篡改 if (!string.IsNullOrEmpty(sha256))
 {
 string actual = GetSha256(setupPath);
 if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
 {
 return _json.Serialize(new { ok = false, msg = "安装包校验失败，请稍后重试" });
 }
 }

 Process.Start(new ProcessStartInfo {
 FileName = setupPath,
 Arguments = "/SILENT",
 UseShellExecute = true });

 return _json.Serialize(new { ok = true, msg = "更新完成，应用即将重启" });
 }
 catch (Exception e)
 {
 return _json.Serialize(new { ok = false, msg = "更新失败：" + e.Message });
 }
 }

 /// <summary>
 ///读取上次更新失败信息（安装器自行处理错误，这里恒为空）。
 /// </summary>
 public static string ConsumeLastUpdateError()
 {
 return null;
 }

 private static Version ParseVersion(string v)
 {
 try { return new Version(v); }
 catch { return new Version(0,0,0,0); }
 }

 private static string GetSha256(string file)
 {
 using (var sha = SHA256.Create())
 using (var stream = File.OpenRead(file))
 {
 byte[] hash = sha.ComputeHash(stream);
 var sb = new StringBuilder();
 foreach (byte b in hash) sb.Append(b.ToString("x2"));
 return sb.ToString();
 }
 }

 private class UpdateInfo {
 public string version { get; set; }
 public string url { get; set; }
 public string sha256 { get; set; }
 public string notes { get; set; }
 public bool force { get; set; }
 }
 }
}
