using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsFormsApp1
{
    /// <summary>
    /// 本地HTTP静态文件服务器，为WebView2提供wwwroot页面（仅监听本机回环地址）。
    /// </summary>
    public class LocalWebServer : IDisposable
    {
        public const int Port = 17531;
        public const string RootUrl = "http://127.0.0.1:17531/";

        private readonly TcpListener _listener;
        private readonly string _root;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public LocalWebServer(string wwwRoot)
        {
            _root = Path.GetFullPath(wwwRoot);
            _listener = new TcpListener(IPAddress.Loopback, Port);
        }

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(() => AcceptLoop(_cts.Token));
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleClient(client));
            }
        }

        private async Task HandleClient(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    // 只解析请求行（GET/HEAD），极简够用
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                    string requestLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(requestLine)) return;

                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2) return;
                    string method = parts[0];
                    string path = parts[1];

                    if (method != "GET" && method != "HEAD")
                    {
                        await WriteStatus(stream, 405, "Method Not Allowed");
                        return;
                    }

                    if (path == "/" || path == "/index.html") path = "/index.html";
                    string file = Path.GetFullPath(Path.Combine(_root, path.TrimStart('/')));
                    if (!file.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteStatus(stream, 403, "Forbidden");
                        return;
                    }
                    if (!File.Exists(file))
                    {
                        await WriteStatus(stream, 404, "Not Found");
                        return;
                    }

                    byte[] body = await Task.Run(() => File.ReadAllBytes(file));
                    await WriteResponse(stream, 200, "OK", GetMime(file), body, method == "HEAD");
                }
                catch
                {
                    // 单次请求失败不影响服务器
                }
            }
        }

        private static async Task WriteStatus(Stream s, int code, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            await WriteResponse(s, code, text, "text/plain; charset=utf-8", body, false);
        }

        private static async Task WriteResponse(Stream s, int code, string text, string mime, byte[] body, bool headOnly)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(text).Append("\r\n");
            sb.Append("Content-Type: ").Append(mime).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
            await s.WriteAsync(header, 0, header.Length);
            if (!headOnly) await s.WriteAsync(body, 0, body.Length);
            await s.FlushAsync();
        }

        private static string GetMime(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".png": return "image/png";
                case ".json": return "application/json; charset=utf-8";
                case ".ico": return "image/x-icon";
                default: return "application/octet-stream";
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }
}
