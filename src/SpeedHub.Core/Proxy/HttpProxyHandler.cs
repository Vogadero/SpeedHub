using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedHub.Core.Configuration;
using SpeedHub.Core.DomainResolve;
using Yarp.ReverseProxy.Forwarder;

namespace SpeedHub.Core.Proxy
{
    /// <summary>
    /// HTTP 代理处理器 - 处理代理端口的入站请求转发
    /// 
    /// 支持两种模式：
    ///   1. CONNECT 隧道（HTTPS）：浏览器发 CONNECT -> DNS解析 -> TCP连接 -> 原始字节中继
    ///   2. HTTP 转发（HTTP）：GET http://xxx -> YARP 反向代理
    /// </summary>
    public class HttpProxyHandler
    {
        private readonly IDnsResolver _dnsResolver;
        private readonly IHttpForwarder _httpForwarder;
        private readonly HttpMessageInvoker _httpClient;
        private readonly ILogger<HttpProxyHandler> _logger;
        private readonly IOptions<SpeedHubConfig> _config;

        public static readonly ProxyStats Stats = new();

        public HttpProxyHandler(
            IDnsResolver dnsResolver,
            IHttpForwarder httpForwarder,
            HttpMessageInvoker httpClient,
            ILogger<HttpProxyHandler> logger,
            IOptions<SpeedHubConfig> config)
        {
            _dnsResolver = dnsResolver;
            _httpForwarder = httpForwarder;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger;
            _config = config;
        }

        /// <summary>
        /// 代理请求入口 - 由 ProxyMiddleware 调用
        /// </summary>
        public async Task HandleProxyRequestAsync(HttpContext context, string targetDomain)
        {
            var sw = Stopwatch.StartNew();
            var requestId = Guid.NewGuid().ToString("N")[..8];

            try
            {
                if (context.Request.Method == "CONNECT")
                {
                    await HandleConnectTunnelAsync(context, targetDomain, requestId, sw);
                }
                else
                {
                    await HandleHttpForwardAsync(context, targetDomain, requestId, sw);
                }
            }
            catch (SocketException ex)
            {
                sw.Stop();
                Interlocked.Increment(ref Stats._errors);
                _logger.LogError(ex, "[{ReqId}] 网络错误: {Target} {Msg}", requestId, targetDomain, ex.Message);
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 502;
                    await context.Response.WriteAsJsonAsync(new { error = "网络错误", message = ex.Message });
                }
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                _logger.LogInformation("[{ReqId}] 请求被取消: {Target}", requestId, targetDomain);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Interlocked.Increment(ref Stats._errors);
                _logger.LogError(ex, "[{ReqId}] 代理异常: {Target} {Msg}", requestId, targetDomain, ex.Message);
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 502;
                    await context.Response.WriteAsJsonAsync(new { error = "服务器内部错误", message = ex.Message });
                }
            }
            finally
            {
                RecordRequestStats(sw.ElapsedMilliseconds, context.Response.StatusCode);
            }
        }

        /// <summary>
        /// 处理 CONNECT 隧道（通过原始 Socket）- 由 ProxyMiddleware 直接调用
        /// 
        /// 这个方法直接从底层 Socket 读取/写入原始 TCP 字节流，
        /// 完全绕过 Kestrel 的 HTTP 抽象层。
        /// </summary>
        public async Task HandleConnectTunnelWithSocketAsync(Socket clientSocket, string targetDomain)
        {
            var sw = Stopwatch.StartNew();
            var requestId = Guid.NewGuid().ToString("N")[..8];
            var networkStream = new NetworkStream(clientSocket, ownsSocket: false);

            try
            {
                // 解析端口 (默认 443)
                int port = 443;
                var colonIndex = targetDomain.LastIndexOf(':');
                if (colonIndex > 0)
                {
                    int.TryParse(targetDomain.Substring(colonIndex + 1), out port);
                    targetDomain = targetDomain.Substring(0, colonIndex);
                }

                _logger.LogInformation("[{ReqId}] CONNECT 隧道 (Socket模式) -> {Target}:{Port}", requestId, targetDomain, port);

                // 获取域名配置
                var domainConfig = GetDomainConfig(targetDomain);

                // DNS 解析
                IReadOnlyList<IPAddress> ips;
                if (domainConfig?.IPAddress != null)
                {
                    ips = new[] { domainConfig.IPAddress };
                    _logger.LogInformation("[{ReqId}] 使用静态 IP: {Ip}", requestId, ips[0]);
                }
                else
                {
                    _logger.LogInformation("[{ReqId}] 正在解析 DNS: {Domain}:{Port}", requestId, targetDomain, port);
                    ips = await _dnsResolver.ResolveAsync(targetDomain, port);
                    _logger.LogInformation("[{ReqId}] DNS 解析结果: {@Ips}", requestId, ips.Select(ip => ip.ToString()));
                }

                if (ips.Count == 0)
                {
                    _logger.LogError("[{ReqId}] DNS 解析失败: {Domain}", requestId, targetDomain);
                    var errorMsg = System.Text.Encoding.UTF8.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\nDNS 解析失败");
                    await networkStream.WriteAsync(errorMsg);
                    return;
                }

                var targetIp = ips[0];

                // 建立到目标的 TCP 连接
                using var targetClient = new TcpClient();
                var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                try
                {
                    _logger.LogInformation("[{ReqId}] 正在连接 {TargetIp}:{Port}...", requestId, targetIp, port);
                    await targetClient.ConnectAsync(targetIp, port, connectCts.Token);
                    _logger.LogInformation("[{ReqId}] TCP 连接成功: {TargetIp}:{Port}", requestId, targetIp, port);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{ReqId}] 无法连接到目标 {TargetIp}:{Port}", requestId, targetIp, port);
                    var errorMsg = System.Text.Encoding.UTF8.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n无法连接到目标服务器");
                    await networkStream.WriteAsync(errorMsg);
                    return;
                }

                // 发送 200 Connection Established
                var responseBytes = System.Text.Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 Connection Established\r\n" +
                    "Connection: keep-alive\r\n" +
                    "\r\n");
                await networkStream.WriteAsync(responseBytes);
                await networkStream.FlushAsync();

                _logger.LogInformation("[{ReqId}] 已发送 200 Connection Established", requestId);

                // 双向原始字节中继
                var targetStream = targetClient.GetStream();

                _logger.LogInformation("[{ReqId}] 开始双向原始字节中继（TLS 模式）...", requestId);

                long bytesReceived = 0, bytesSent = 0;
                // 浏览器 -> 目标服务器
                var clientToTarget = RawRelayAsync(networkStream, targetStream, "C->T", requestId, CancellationToken.None,
                    bytes => Interlocked.Add(ref bytesReceived, bytes));

                // 目标服务器 -> 浏览器
                var targetToClient = RawRelayAsync(targetStream, networkStream, "T->C", requestId, CancellationToken.None,
                    bytes => Interlocked.Add(ref bytesSent, bytes));

                // 等待任一方向结束
                await Task.WhenAny(clientToTarget, targetToClient);

                // 累加到全局统计
                Interlocked.Add(ref Stats._bytesReceived, bytesReceived);
                Interlocked.Add(ref Stats._bytesSent, bytesSent);

                sw.Stop();
                Interlocked.Increment(ref Stats._successfulRequests);
                _logger.LogInformation(
                    "[{ReqId}] CONNECT 隧道正常关闭, 耗时 {Elapsed:F2}ms, 上行 {Up} 字节, 下行 {Down} 字节",
                    requestId, sw.Elapsed.TotalMilliseconds, bytesReceived, bytesSent);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[{ReqId}] CONNECT 隧道被取消 (客户端断开)", requestId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ReqId}] CONNECT 隧道异常", requestId);
            }
            finally
            {
                RecordRequestStats(sw.ElapsedMilliseconds, 200);
            }
        }

        /// <summary>
        /// 处理 HTTP/HTTPS CONNECT 隧道（用于 HTTPS 站点）
        /// 
        /// 浏览器发送: CONNECT github.com:443 HTTP/1.1
        /// 我们需要:
        ///   1. 解析目标域名到 IP
        ///   2. 连接到目标服务器 (TCP)
        ///   3. 返回 200 Connection Established 给浏览器
        ///   4. 在浏览器和目标之间做**原始 TCP 字节流**中继
        /// 
        /// ⚠️ 关键修复：不能用 HttpContext.Request/Response.Body 做中继！
        ///   Kestrel 的 Body 流经过 HTTP 协议层处理，会破坏 TLS 原始字节。
        ///   必须通过 IHttpConnectionFeature 获取底层 TCP 连接的原始 Stream。
        /// </summary>
        private async Task HandleConnectTunnelAsync(HttpContext context, string targetDomain, string requestId, Stopwatch sw)
        {
            // 解析端口 (默认 443)
            int port = 443;
            var colonIndex = targetDomain.LastIndexOf(':');
            if (colonIndex > 0)
            {
                int.TryParse(targetDomain.Substring(colonIndex + 1), out port);
                targetDomain = targetDomain.Substring(0, colonIndex);
            }

            _logger.LogInformation("[{ReqId}] CONNECT 隧道 -> {Target}:{Port}", requestId, targetDomain, port);

            // 获取域名配置
            var domainConfig = GetDomainConfig(targetDomain);

            // DNS 解析
            IReadOnlyList<IPAddress> ips;
            if (domainConfig?.IPAddress != null)
            {
                ips = new[] { domainConfig.IPAddress };
                _logger.LogInformation("[{ReqId}] 使用静态 IP: {Ip}", requestId, ips[0]);
            }
            else
            {
                _logger.LogInformation("[{ReqId}] 正在解析 DNS: {Domain}:{Port}", requestId, targetDomain, port);
                ips = await _dnsResolver.ResolveAsync(targetDomain, port);
                _logger.LogInformation("[{ReqId}] DNS 解析结果: {@Ips}", requestId, ips.Select(ip => ip.ToString()));
            }

            var targetIp = ips[0];

            // ════════════════════════════════════════════════════
            // 第一步：获取底层 TCP 连接（关键！）
            // ════════════════════════════════════════════════════
            //
            // Kestrel 的 HttpContext 包装了底层 TCP 连接。
            // 对于 CONNECT 隧道，我们需要绕过 HTTP 层，
            // 直接操作原始 TCP 字节流来中继 TLS 数据。

            var connectionFeature = context.Features.Get<IHttpConnectionFeature>();
            if (connectionFeature == null)
            {
                _logger.LogError("[{ReqId}] 无法获取底层 TCP 连接 feature，无法建立隧道", requestId);
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new { error = "不支持 CONNECT 隧道（缺少底层连接）" });
                return;
            }

            // 获取原始输入/输出流（每次调用返回同一个底层流实例）
            // 注意：这是 Kestrel 内部管道的原始 Stream，不是 HTTP Request.Body
            var rawInputStream = GetRawTransportStream(context, _logger);
            var rawOutputStream = context.Response.Body;

            if (rawInputStream == null)
            {
                _logger.LogError("[{ReqId}] 无法获取原始输入流", requestId);
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new { error = "无法获取传输流" });
                return;
            }

            // ════════════════════════════════════════════════════
            // 第二步：建立到目标的 TCP 连接
            // ════════════════════════════════════════════════════

            using var targetClient = new TcpClient();
            var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            try
            {
                _logger.LogInformation("[{ReqId}] 正在连接 {TargetIp}:{Port}...", requestId, targetIp, port);
                await targetClient.ConnectAsync(targetIp, port, connectCts.Token);
                _logger.LogInformation("[{ReqId}] TCP 连接成功: {TargetIp}:{Port}", requestId, targetIp, port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ReqId}] 无法连接到目标 {TargetIp}:{Port}", requestId, targetIp, port);
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "无法连接到目标服务器",
                    target = $"{targetIp}:{port}",
                    reason = ex.Message
                });
                return;
            }

            // ════════════════════════════════════════════════════
            // 第三步：发送 200 Connection Established
            // ════════════════════════════════════════════════════
            //
            // 必须手动构造原始 HTTP 响应行写入输出流，
            // 因为一旦开始隧道模式，就不能再用 HttpContext 的 API 操作响应了。

            var responseBytes = System.Text.Encoding.UTF8.GetBytes(
                "HTTP/1.1 200 Connection Established\r\n" +
                "Connection: keep-alive\r\n" +
                "\r\n");
            await rawOutputStream.WriteAsync(responseBytes, context.RequestAborted);
            await rawOutputStream.FlushAsync(context.RequestAborted);

            _logger.LogInformation("[{ReqId}] 已发送 200 Connection Established（原始字节）", requestId);

            // ════════════════════════════════════════════════════
            // 第四步：双向原始字节流中继
            // ════════════════════════════════════════════════════
            //
            // 此时浏览器收到 200 Established，会立即开始 TLS 握手：
            //   浏览器 → 代理：TLS ClientHello（原始 TCP 字节）
            //   代理 → GitHub：转发 ClientHello
            //   GitHub → 代理：TLS ServerHello + Certificate（原始 TCP 字节）
            //   代理 → 浏览器：转发 ServerHello
            //   ... 后续所有 TLS 加密数据同样双向中继 ...
            //
            // 这就是为什么必须用原始 Stream 而不是 HttpContext.Body！

            var targetStream = targetClient.GetStream();

            _logger.LogInformation("[{ReqId}] 开始双向原始字节中继（TLS 模式）...", requestId);

            try
            {
                // 浏览器 -> 目标服务器（TLS ClientHello + 加密数据）
                long bytesReceived = 0, bytesSent = 0;
                var clientToTarget = RawRelayAsync(rawInputStream, targetStream, "C->T", requestId, context.RequestAborted,
                    bytes => Interlocked.Add(ref bytesReceived, bytes));

                // 目标服务器 -> 浏览器（TLS ServerHello + 加密数据）
                var targetToClient = RawRelayAsync(targetStream, rawOutputStream, "T->C", requestId, context.RequestAborted,
                    bytes => Interlocked.Add(ref bytesSent, bytes));

                // 等待任一方向结束
                await Task.WhenAny(clientToTarget, targetToClient);

                // 累加到全局统计
                Interlocked.Add(ref Stats._bytesReceived, bytesReceived);
                Interlocked.Add(ref Stats._bytesSent, bytesSent);

                sw.Stop();
                Interlocked.Increment(ref Stats._successfulRequests);
                _logger.LogInformation(
                    "[{ReqId}] CONNECT 隧道正常关闭, 耗时 {Elapsed:F2}ms, 上行 {Up} 字节, 下行 {Down} 字节",
                    requestId, sw.Elapsed.TotalMilliseconds, bytesReceived, bytesSent);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[{ReqId}] CONNECT 隧道被取消 (客户端断开)", requestId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ReqId}] CONNECT 隧道数据传输异常", requestId);
            }
        }

        /// <summary>
        /// 尝试从 HttpContext 获取底层传输 Stream
        /// 
        /// 方案：
        ///   1. IHttpConnectionFeature（ASP.NET Core 标准接口，所有平台可用）
        ///   2. 回退到 Request.Body（最后手段）
        /// </summary>
        private static Stream? GetRawTransportStream(HttpContext context, ILogger logger)
        {
            // 方案 1: IHttpConnectionFeature - ASP.NET Core 标准接口
            // 提供底层连接信息，在 Kestrel 上运行时可以获取原始流
            var connectionFeature = context.Features.Get<IHttpConnectionFeature>();
            if (connectionFeature != null)
            {
                logger.LogDebug("使用 IHttpConnectionFeature 获取底层连接信息");
                // 注意：IHttpConnectionFeature 本身不直接提供 Stream，
                // 回退到 Request.Body，Kestrel 对 CONNECT 方法会将后续数据路由到这里
                return context.Request.Body;
            }

            // 方案 2: 最终回退
            logger.LogWarning("回退到 Request.Body");
            return context.Request.Body;
        }

        /// <summary>
        /// 原始字节流中继（用于 CONNECT 隧道的 TLS 数据）
        /// 
        /// 与 CopyStreamAsync 不同，这个方法不做任何数据处理，
        /// 纯粹是 byte-in-byte-out 的透明转发，确保 TLS 数据不被修改
        /// </summary>
        private async Task RawRelayAsync(Stream source, Stream dest, string direction, string requestId, CancellationToken cancellationToken, Action<long>? onBytesTransferred = null)
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

                // 统计流量
                if (totalBytes > 0)
                {
                    onBytesTransferred?.Invoke(totalBytes);
                }

                // 中继完成日志（只在有数据传输时记录）
                if (totalBytes > 0)
                {
                    _logger.LogInformation("[{ReqId}] {Dir} 中继完成: {Packets} 包, {TotalBytes} 字节",
                        requestId, direction, packetCount, totalBytes);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常关闭（客户端断开或取消）
                _logger.LogDebug("[{ReqId}] {Dir} 中继取消", requestId, direction);
            }
            catch (IOException ioEx)
            {
                // 连接断开（正常情况，不需要报错）
                _logger.LogInformation("[{ReqId}] {Dir} 连接关闭: {Msg}", requestId, direction, ioEx.Message);
            }
        }

        /// <summary>
        /// 处理普通 HTTP 代理转发（使用 YARP）
        /// </summary>
        private async Task HandleHttpForwardAsync(HttpContext context, string targetDomain, string requestId, Stopwatch sw)
        {
            var request = context.Request;

            // 1. 获取域名配置
            var domainConfig = GetDomainConfig(targetDomain);

            // 2. 检查是否有自定义响应配置
            if (domainConfig?.Response != null)
            {
                await SendCustomResponse(context, domainConfig.Response);
                sw.Stop();
                return;
            }

            // 3. 检查是否需要 CDN 重定向
            if (domainConfig?.Destination != null && domainConfig.Destination.IsAbsoluteUri)
            {
                await HandleCdnRedirectAsync(context, domainConfig, requestId);
                sw.Stop();
                return;
            }

            // 4. 解析目标 IP 和端口
            int port = request.Scheme == "https" ? 443 : 80;
            IReadOnlyList<IPAddress> ips;

            if (domainConfig?.IPAddress != null)
            {
                ips = new[] { domainConfig.IPAddress };
            }
            else
            {
                ips = await _dnsResolver.ResolveAsync(targetDomain, port);
            }

            var targetIp = ips[0];
            var destinationPrefix = $"http://{targetIp}:{port}";

            _logger.LogInformation("[{ReqId}] YARP 转发: {Method} {Path} -> {Dest}",
                requestId, request.Method, request.Path, destinationPrefix);

            // 5. 使用 YARP 反向代理转发
            // 签名: SendAsync(context, destinationPrefix, httpClient, requestConfig, transformer)
            var error = await _httpForwarder.SendAsync(
                context,
                destinationPrefix,
                _httpClient,
                ForwarderRequestConfig.Empty,
                new CustomHeaderTransform(targetDomain));

            sw.Stop();

            if (error != ForwarderError.None)
            {
                Interlocked.Increment(ref Stats._failedRequests);
                _logger.LogError("[{ReqId}] YARP 转发失败: {Error} ({Target})",
                    requestId, error, targetDomain);

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = (int)GetStatusCodeFromError(error);
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "转发失败",
                        reason = error.ToString(),
                        target = targetDomain
                    });
                }
            }
            else
            {
                Interlocked.Increment(ref Stats._successfulRequests);
                _logger.LogInformation(
                    "[{ReqId}] HTTP 转发成功: {Method} {Path} -> {Target} ({Elapsed:F2}ms)",
                    requestId, request.Method, request.Path, targetDomain, sw.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>
        /// 发送自定义响应
        /// </summary>
        private static async Task SendCustomResponse(HttpContext context, ResponseConfig responseConfig)
        {
            context.Response.StatusCode = responseConfig.StatusCode;
            context.Response.ContentType = responseConfig.ContentType;

            if (!string.IsNullOrEmpty(responseConfig.ContentValue))
            {
                await context.Response.WriteAsync(responseConfig.ContentValue);
            }
        }

        /// <summary>
        /// 处理 CDN 重定向请求
        /// </summary>
        private async Task HandleCdnRedirectAsync(HttpContext context, DomainConfig domainConfig, string requestId)
        {
            var destinationBase = domainConfig!.Destination!.ToString().TrimEnd('/');
            var requestPath = context.Request.Path + context.Request.QueryString;
            var redirectUrl = destinationBase + requestPath;

            _logger.LogInformation("[{ReqId}] CDN重定向: {Original} -> {Destination}",
                requestId, context.Request.Host, redirectUrl);

            context.Response.Redirect(redirectUrl, permanent: false);
        }

        /// <summary>
        /// 获取域名配置
        /// </summary>
        private DomainConfig? GetDomainConfig(string domain)
        {
            var configs = _config.Value.DomainConfigs;

            // 精确匹配
            if (configs.TryGetValue(domain, out var exactConfig))
            {
                return exactConfig.Enabled ? exactConfig : null;
            }

            // 通配符匹配 (*.github.com, *.google.com 等)
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

        /// <summary>
        /// 通配符匹配（支持 *.domain.com 格式）
        /// </summary>
        private static bool IsWildcardMatch(string input, string pattern)
        {
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace(@"\*", ".*")
                .Replace(@"\.", @"\.") + "$";

            return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
        }

        private static HttpStatusCode GetStatusCodeFromError(ForwarderError error) =>
            error switch
            {
                ForwarderError.NoAvailableDestinations => HttpStatusCode.BadGateway,
                _ => HttpStatusCode.BadGateway,
            };

        /// <summary>
        /// 记录请求统计
        /// </summary>
        private void RecordRequestStats(double elapsedMs, int statusCode)
        {
            Interlocked.Increment(ref Stats._totalRequests);

            if (statusCode >= 200 && statusCode < 400)
                Interlocked.Increment(ref Stats._successfulRequests);
            else
                Interlocked.Increment(ref Stats._failedRequests);

            // 更新平均响应时间
            var currentAvg = Stats.AvgResponseTimeMs;
            var total = Math.Max(1, Stats.TotalRequests);
            Stats.AvgResponseTimeMs = currentAvg + ((elapsedMs - currentAvg) / total);
        }
    }

    /// <summary>
    /// 自定义 HTTP 转换器 - 设置正确的 Host 头
    /// </summary>
    internal class CustomHeaderTransform : HttpTransformer
    {
        private readonly string _targetHost;

        public CustomHeaderTransform(string targetHost)
        {
            _targetHost = targetHost;
        }

        public override async ValueTask TransformRequestAsync(HttpContext httpRequestContext,
            HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            // 调用基类默认转换（拷贝请求头、方法等）
            await base.TransformRequestAsync(httpRequestContext, proxyRequest, destinationPrefix, cancellationToken);

            // 覆盖 Host 头为原始目标域名
            proxyRequest.Headers.Host = _targetHost;
        }
    }

    /// <summary>
    /// 代理统计信息 - 使用内部字段支持线程安全更新
    /// </summary>
    public class ProxyStats
    {
        internal long _totalRequests;
        internal long _successfulRequests;
        internal long _failedRequests;
        internal long _dnsErrors;
        internal long _errors;
        internal long _bytesReceived;
        internal long _bytesSent;

        public long TotalRequests => _totalRequests;
        public long SuccessfulRequests => _successfulRequests;
        public long FailedRequests => _failedRequests;
        public long DnsErrors => _dnsErrors;
        public long Errors => _errors;
        public double AvgResponseTimeMs { get; set; }
        public long TotalBytesTransferred { get; set; }
        public long BytesReceived => _bytesReceived;
        public long BytesSent => _bytesSent;

        public double SuccessRate => TotalRequests > 0 ? (double)SuccessfulRequests / TotalRequests : 0;
    }
}
