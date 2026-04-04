using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedHub.Core.Configuration;
using SpeedHub.Core.DomainResolve;

namespace SpeedHub.Core.Proxy
{
    /// <summary>
    /// CONNECT 隧道服务 - 独立的 TcpListener 处理代理端口的所有入站连接
    /// 
    /// 为什么需要这个服务？
    /// Kestrel 不会把 CONNECT 请求后的原始 TCP 数据路由到 Request.Body，
    /// 所以无法通过 HttpContext 做 TLS 字节中继。
    /// 这个服务直接在原始 TCP 层面处理 CONNECT 隧道，完全绕过 Kestrel。
    /// 
    /// 工作流程：
    ///   1. 监听 HttpProxyPort 端口（38457）
    ///   2. 接受客户端 TCP 连接
    ///   3. 读取第一行判断请求类型
    ///   4. CONNECT → 建立 TLS 隧道（本服务处理）
    ///   5. HTTP → 转发到 Kestrel 的内部 HTTP 端口（5000）
    /// </summary>
    public class ConnectTunnelService : BackgroundService
    {
        private readonly IDnsResolver _dnsResolver;
        private readonly ILogger<ConnectTunnelService> _logger;
        private readonly IOptions<SpeedHubConfig> _config;
        private TcpListener? _listener;
        
        // Kestrel 内部 HTTP 端口（用于转发普通 HTTP 请求）
        private const int KestrelInternalPort = 5000;

        public ConnectTunnelService(
            IDnsResolver dnsResolver,
            ILogger<ConnectTunnelService> logger,
            IOptions<SpeedHubConfig> config)
        {
            _dnsResolver = dnsResolver;
            _logger = logger;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var port = _config.Value.HttpProxyPort;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start(100);

            _logger.LogInformation("[TUNNEL] 代理隧道服务已启动，监听端口 {Port}", port);
            _logger.LogInformation("[TUNNEL] CONNECT 隧道由本服务处理，HTTP 请求转发到 Kestrel :{KestrelPort}", KestrelInternalPort);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                        _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[TUNNEL] 接受客户端连接失败");
                    }
                }
            }
            finally
            {
                _listener.Stop();
                _logger.LogInformation("[TUNNEL] 代理隧道服务已停止");
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
        {
            var requestId = Guid.NewGuid().ToString("N")[..8];
            var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

            try
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];

                // 读取 HTTP 请求行
                var requestLine = await ReadLineAsync(stream, buffer, stoppingToken);
                if (string.IsNullOrEmpty(requestLine))
                {
                    _logger.LogDebug("[{ReqId}] 空连接，关闭: {Remote}", requestId, remoteEp);
                    client.Close();
                    return;
                }

                var parts = requestLine.Split(' ', 3);
                if (parts.Length < 2)
                {
                    _logger.LogWarning("[{ReqId}] 无效的请求行: {Line}", requestId, requestLine);
                    client.Close();
                    return;
                }

                var method = parts[0];
                var target = parts[1];

                if (method == "CONNECT")
                {
                    // HTTPS CONNECT 隧道 - 本服务处理
                    await HandleConnectAsync(client, stream, target, requestId, stoppingToken);
                }
                else
                {
                    // 普通 HTTP 请求 - 转发到 Kestrel
                    await ForwardToKestrelAsync(client, stream, requestLine, requestId, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("[{ReqId}] 连接被取消", requestId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ReqId}] 处理客户端连接异常", requestId);
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private async Task HandleConnectAsync(TcpClient client, NetworkStream clientStream, string target, string requestId, CancellationToken cancellationToken)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 解析目标域名和端口
            int port = 443;
            var colonIndex = target.LastIndexOf(':');
            if (colonIndex > 0)
            {
                int.TryParse(target.Substring(colonIndex + 1), out port);
                target = target.Substring(0, colonIndex);
            }

            _logger.LogInformation("[{ReqId}] CONNECT 隧道 -> {Target}:{Port}", requestId, target, port);

            // 获取域名配置
            var domainConfig = GetDomainConfig(target);

            // DNS 解析
            IReadOnlyList<IPAddress> ips;
            if (domainConfig?.IPAddress != null)
            {
                ips = new[] { domainConfig.IPAddress };
                _logger.LogInformation("[{ReqId}] 使用静态 IP: {Ip}", requestId, ips[0]);
            }
            else
            {
                _logger.LogInformation("[{ReqId}] 正在解析 DNS: {Domain}:{Port}", requestId, target, port);
                ips = await _dnsResolver.ResolveAsync(target, port);
                _logger.LogInformation("[{ReqId}] DNS 解析结果: {@Ips}", requestId, ips.Select(ip => ip.ToString()));
            }

            if (ips.Count == 0)
            {
                _logger.LogError("[{ReqId}] DNS 解析失败: {Domain}", requestId, target);
                var errorMsg = Encoding.UTF8.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\nDNS 解析失败");
                await clientStream.WriteAsync(errorMsg, cancellationToken);
                return;
            }

            // 遍历所有 IP 尝试连接，失败自动轮换下一个
            TcpClient? targetClient = null;
            IPAddress? connectedIp = null;
            var connectTimeout = TimeSpan.FromSeconds(5); // 单个 IP 超时缩短到 5 秒

            for (int i = 0; i < ips.Count; i++)
            {
                var ip = ips[i];
                var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(connectTimeout);

                try
                {
                    _logger.LogInformation("[{ReqId}] 尝试连接 [{Index}/{Total}] {Ip}:{Port}...", 
                        requestId, i + 1, ips.Count, ip, port);
                    
                    targetClient = new TcpClient();
                    await targetClient.ConnectAsync(ip, port, attemptCts.Token);
                    
                    connectedIp = ip;
                    _logger.LogInformation("[{ReqId}] TCP 连接成功: {Ip}:{Port}", requestId, ip, port);
                    break; // 连接成功，退出循环
                }
                catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException)
                {
                    _logger.LogWarning("[{ReqId}] 连接 {Ip}:{Port} 失败: {Msg}", requestId, ip, port, ex.Message);
                    targetClient?.Dispose();
                    targetClient = null;
                }
                finally
                {
                    attemptCts.Dispose();
                }
            }

            if (targetClient == null || connectedIp == null)
            {
                _logger.LogError("[{ReqId}] 所有 IP 都连接失败: {Ips}", requestId, string.Join(", ", ips));
                var errorMsg = Encoding.UTF8.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n无法连接到目标服务器（所有 IP 均失败）");
                await clientStream.WriteAsync(errorMsg, cancellationToken);
                return;
            }

            // 发送 200 Connection Established
            var responseBytes = Encoding.UTF8.GetBytes(
                "HTTP/1.1 200 Connection Established\r\n" +
                "Connection: keep-alive\r\n" +
                "\r\n");
            await clientStream.WriteAsync(responseBytes, cancellationToken);
            await clientStream.FlushAsync(cancellationToken);

            _logger.LogInformation("[{ReqId}] 已发送 200 Connection Established (通过 {Ip})", requestId, connectedIp);

            // 双向原始字节中继
            var targetStream = targetClient.GetStream();

            _logger.LogInformation("[{ReqId}] 开始双向原始字节中继（TLS 模式）...", requestId);

            // 浏览器 -> 目标服务器
            var clientToTarget = RawRelayAsync(clientStream, targetStream, "C->T", requestId, cancellationToken);

            // 目标服务器 -> 浏览器
            var targetToClient = RawRelayAsync(targetStream, clientStream, "T->C", requestId, cancellationToken);

            // 等待任一方向结束
            await Task.WhenAny(clientToTarget, targetToClient);

            sw.Stop();
            _logger.LogInformation(
                "[{ReqId}] CONNECT 隧道正常关闭, 耗时 {Elapsed:F2}ms",
                requestId, sw.Elapsed.TotalMilliseconds);
        }

        private async Task RawRelayAsync(Stream source, Stream dest, string direction, string requestId, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            int totalBytes = 0;
            int packetCount = 0;

            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    totalBytes += read;
                    packetCount++;
                    await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    await dest.FlushAsync(cancellationToken);
                }

                if (totalBytes > 0)
                {
                    _logger.LogInformation("[{ReqId}] {Dir} 中继完成: {Packets} 包, {TotalBytes} 字节",
                        requestId, direction, packetCount, totalBytes);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("[{ReqId}] {Dir} 中继取消", requestId, direction);
            }
            catch (IOException ioEx)
            {
                _logger.LogInformation("[{ReqId}] {Dir} 连接关闭: {Msg}", requestId, direction, ioEx.Message);
            }
        }

        /// <summary>
        /// 将普通 HTTP 请求转发到 Kestrel 内部端口
        /// </summary>
        private async Task ForwardToKestrelAsync(TcpClient client, NetworkStream clientStream, string firstLine, string requestId, CancellationToken cancellationToken)
        {
            try
            {
                // 连接到 Kestrel 内部端口
                using var kestrelClient = new TcpClient();
                await kestrelClient.ConnectAsync(IPAddress.Loopback, KestrelInternalPort, cancellationToken);
                var kestrelStream = kestrelClient.GetStream();

                // 发送第一行（已经读取的）
                var firstLineBytes = Encoding.UTF8.GetBytes(firstLine + "\r\n");
                await kestrelStream.WriteAsync(firstLineBytes, cancellationToken);

                // 中继剩余请求头
                await RelayHeadersAsync(clientStream, kestrelStream, requestId, cancellationToken);

                // 双向中继
                var clientToKestrel = RawRelayAsync(clientStream, kestrelStream, "C->K", requestId, cancellationToken);
                var kestrelToClient = RawRelayAsync(kestrelStream, clientStream, "K->C", requestId, cancellationToken);

                await Task.WhenAny(clientToKestrel, kestrelToClient);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ReqId}] 转发到 Kestrel 失败", requestId);
            }
        }

        private async Task RelayHeadersAsync(NetworkStream clientStream, NetworkStream kestrelStream, string requestId, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            
            while (true)
            {
                int read = await clientStream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                
                // 检查是否读到了完整的 headers（\r\n\r\n）
                if (sb.ToString().Contains("\r\n\r\n"))
                {
                    var headersBytes = Encoding.UTF8.GetBytes(sb.ToString());
                    await kestrelStream.WriteAsync(headersBytes, cancellationToken);
                    break;
                }
            }
        }

        private async Task<string?> ReadLineAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            int offset = 0;
            int totalRead = 0;

            while (totalRead < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
                if (read == 0) break;

                totalRead += read;

                // 查找 \r\n
                for (int i = offset; i < offset + read; i++)
                {
                    if (buffer[i] == '\r' && i + 1 < offset + read && buffer[i + 1] == '\n')
                    {
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, i));
                        // 把剩余数据放回缓冲区（如果需要的话）
                        return sb.ToString();
                    }
                }

                sb.Append(Encoding.UTF8.GetString(buffer, offset, read));
                offset = offset + read;
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private DomainConfig? GetDomainConfig(string domain)
        {
            var configs = _config.Value.DomainConfigs;

            // 精确匹配
            if (configs.TryGetValue(domain, out var exactConfig))
            {
                return exactConfig.Enabled ? exactConfig : null;
            }

            // 通配符匹配
            foreach (var entry in configs)
            {
                var pattern = entry.Key;
                if (pattern.Contains("*") && IsWildcardMatch(domain, pattern))
                {
                    return entry.Value.Enabled ? entry.Value : null;
                }
            }

            return null;
        }

        private static bool IsWildcardMatch(string input, string pattern)
        {
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace(@"\*", ".*")
                .Replace(@"\.", @"\.") + "$";

            return System.Text.RegularExpressions.Regex.IsMatch(input, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[TUNNEL] 正在停止 CONNECT 隧道服务...");
            _listener?.Stop();
            await base.StopAsync(cancellationToken);
        }
    }
}
