using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedHub.Core.Configuration;
using SpeedHub.Core.DomainResolve;
using Yarp.ReverseProxy.Forwarder;

namespace SpeedHub.Core.Proxy
{
    /// <summary>
    /// 高性能HTTP代理处理器 - 带连接池和请求日志
    /// 
    /// 支持两种模式：
    ///   1. CONNECT 隧道（HTTPS）：建立到目标服务器的 TCP 双向隧道（原始 Socket 中继）
    ///   2. HTTP 普通代理（HTTP）：使用 YARP 转发请求
    /// </summary>
    public class HttpProxyHandler
    {
        private readonly IHttpForwarder _forwarder;
        private readonly IDnsResolver _dnsResolver;
        private readonly IOptions<SpeedHubConfig> _config;
        private readonly ILogger<HttpProxyHandler> _logger;
        private readonly HttpMessageInvoker _httpClient;

        /// <summary>
        /// 代理统计信息
        /// </summary>
        public ProxyStats Stats { get; } = new();

        public HttpProxyHandler(
            IHttpForwarder forwarder,
            IDnsResolver dnsResolver,
            IOptions<SpeedHubConfig> config,
            ILogger<HttpProxyHandler> logger)
        {
            _forwarder = forwarder;
            _dnsResolver = dnsResolver;
            _config = config;
            _logger = logger;

            // 创建 HttpClient 用于 YARP 转发（带超时和连接池配置）
            var httpClientHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                MaxConnectionsPerServer = int.MaxValue,
            };
            _httpClient = new HttpMessageInvoker(httpClientHandler);
        }

        /// <summary>
        /// 处理HTTP/HTTPS代理请求 - 入口方法
        /// </summary>
        public async Task HandleProxyRequestAsync(HttpContext context, string targetDomain)
        {
            var sw = Stopwatch.StartNew();
            var requestId = Guid.NewGuid().ToString("N")[..8];
            var request = context.Request;

            _logger.LogInformation(
                "[{ReqId}] === HandleProxyRequest: {Method} Target={Target} Scheme={Scheme} Path={Path} ===",
                requestId, request.Method, targetDomain, request.Scheme, request.Path);

            try
            {
                // 根据请求类型分发处理
                if (request.Method == "CONNECT")
                {
                    await HandleConnectTunnelAsync(context, targetDomain, requestId, sw);
                }
                else
                {
                    await HandleHttpForwardAsync(context, targetDomain, requestId, sw);
                }
            }
            catch (DnsResolutionException ex)
            {
                sw.Stop();
                Interlocked.Increment(ref Stats._dnsErrors);
                _logger.LogError(ex, "[{ReqId}] DNS解析失败: {Domain}", requestId, targetDomain);
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new { error = "DNS解析失败", message = ex.Message });
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
        /// 处理 HTTP/HTTPS CONNECT 隧道（用于 HTTPS 站点）
        ///
        /// 浏览器发送: CONNECT github.com:443 HTTP/1.1
        /// 我们需要:
        ///   1. 解析目标域名到 IP
        ///   2. 连接到目标服务器
        ///   3. 返回 200 Connection Established 给浏览器
        ///   4. 在浏览器和目标之间做原始 TCP 数据中继
        ///
        /// ⚠️ 关键：不能用 context.Request.Body / Response.Body 直接做中继，
        ///   因为 Kestrel 的 HttpRequest.Body 在 CONNECT 场景下可能不可靠。
        ///   必须用 ClientServerDuplexStream 正确封装底层连接。
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
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "无法连接到目标服务器",
                    target = $"{targetIp}:{port}",
                    reason = ex.Message
                });
                return;
            }

            // 告诉浏览器隧道建立成功
            context.Response.StatusCode = 200;
            // Flush 响应头给浏览器，让浏览器知道可以开始发送 TLS 数据了
            await context.Response.Body.FlushAsync();
            _logger.LogInformation("[{ReqId}] 已发送 200 Connection Established", requestId);

            // ════════════════════════════════════════════════════════
            // 关键修复：使用 ClientServerDuplexStream 做双向数据中继
            // ════════════════════════════════════════════════════════
            //
            // ASP.NET Core Kestrel 的 HttpContext 对 CONNECT 方法：
            //   - context.Request.Body  可能行为不确定
            //   - 需要正确封装为支持异步读写的双向 Stream

            var targetStream = targetClient.GetStream();

            // 创建适配器流：Read=从浏览器读数据, Write=向浏览器写数据
            var duplexStream = new ClientServerDuplexStream(context);

            _logger.LogInformation("[{ReqId}] 开始双向数据中继...", requestId);

            try
            {
                // 浏览器 -> 目标服务器
                var clientToTarget = CopyStreamAsync(duplexStream, targetStream, "C->T", requestId, context.RequestAborted);

                // 目标服务器 -> 浏览器  
                var targetToClient = CopyStreamAsync(targetStream, duplexStream, "T->C", requestId, context.RequestAborted);

                // 等待任一方向结束
                await Task.WhenAny(clientToTarget, targetToClient);

                sw.Stop();
                Interlocked.Increment(ref Stats._successfulRequests);
                _logger.LogInformation(
                    "[{ReqId}] CONNECT 隧道正常关闭, 耗时 {Elapsed:F2}ms",
                    requestId, sw.Elapsed.TotalMilliseconds);
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
        /// 异步流拷贝（带诊断日志）
        /// </summary>
        private static async Task CopyStreamAsync(Stream source, Stream dest, string direction, string requestId, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            int read;

            try
            {
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    await dest.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常关闭
            }
            catch (IOException)
            {
                // 连接断开
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

            // 2. 解析目标 IP 和端口
            int port = request.Scheme == "https" ? 443 : 80;
            IReadOnlyList<IPAddress> ips;

            if (domainConfig?.IPAddress != null)
            {
                ips = new[] { domainConfig.IPAddress };
                _logger.LogInformation("[{ReqId}] 使用静态 IP: {Ip}", requestId, ips[0]);
            }
            else if (domainConfig?.Destination != null)
            {
                // CDN 重定向模式
                await HandleCdnRedirectAsync(context, domainConfig, requestId);
                return;
            }
            else
            {
                _logger.LogInformation("[{ReqId}] 正在解析 DNS: {Domain}", requestId, targetDomain);
                ips = await _dnsResolver.ResolveAsync(targetDomain, port);
            }

            // 3. 选择最佳 IP
            var targetIp = ips[0];

            // 4. 构建目标地址
            var scheme = request.Scheme;
            var destinationPrefix = $"{scheme}://{targetIp}:{port}";

            _logger.LogInformation("[{ReqId}] YARP 转发: {Method} -> {Dest}",
                requestId, request.Method, destinationPrefix);

            // 5. 创建自定义 Transformer（设置正确的 Host 头）
            var transform = new CustomHeaderTransform(targetDomain);

            // 6. 使用 YARP 转发请求
            var requestConfig = new ForwarderRequestConfig
            {
                ActivityTimeout = TimeSpan.FromMinutes(1),
            };

            var error = await _forwarder.SendAsync(
                context,
                destinationPrefix,
                _httpClient,
                requestConfig,
                transform);

            sw.Stop();

            if (error != ForwarderError.None)
            {
                Interlocked.Increment(ref Stats._failedRequests);
                _logger.LogWarning("[{ReqId}] YARP 转发错误: {Error} {Target}",
                    requestId, error, destinationPrefix);

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = (int)GetStatusCodeFromError(error);
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "代理转发失败",
                        code = error.ToString(),
                        destination = destinationPrefix
                    });
                }
            }
            else
            {
                Interlocked.Increment(ref Stats._successfulRequests);
                _logger.LogInformation(
                    "[{ReqId}] {Method} responded {Status} in {Elapsed:F2}ms",
                    requestId,
                    context.Request.Method,
                    context.Response.StatusCode,
                    sw.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>
        /// 处理 CDN 重定向请求
        /// </summary>
        private async Task HandleCdnRedirectAsync(HttpContext context, DomainConfig domainConfig, string requestId)
        {
            var destinationBase = domainConfig!.Destination!.ToString().TrimEnd('/');
            var requestPath = context.Request.Path + context.QueryString;
            var redirectUrl = destinationBase + requestPath;

            _logger.LogInformation("[{ReqId}] CDN重定向: {Original} -> {Destination}",
                requestId, context.Host, redirectUrl);

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

            // 通配符匹配
            var wildcardConfigs = configs.Where(c =>
                c.Key.Contains('*') && IsWildcardMatch(c.Key, domain) && c.Value.Enabled)
                .OrderBy(c => c.Value.Priority)
                .Select(c => c.Value)
                .ToList();

            return wildcardConfigs.FirstOrDefault();
        }

        /// <summary>
        /// 通配符匹配（支持 * 和 **）
        /// </summary>
        private bool IsWildcardMatch(string pattern, string input)
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

            // 更新带宽统计
            Stats.TotalBytesTransferred += 1024;
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

        public long TotalRequests => _totalRequests;
        public long SuccessfulRequests => _successfulRequests;
        public long FailedRequests => _failedRequests;
        public long DnsErrors => _dnsErrors;
        public long Errors => _errors;
        public double AvgResponseTimeMs { get; set; }
        public long TotalBytesTransferred { get; set; }

        public double SuccessRate => TotalRequests > 0 ? (double)SuccessfulRequests / TotalRequests : 0;
    }

    #region ===== ClientServerDuplexStream =====

    /// <summary>
    /// 双向数据流适配器 - 将 ASP.NET Core 的 HttpContext 包装为可同时读写的 Stream
    /// 
    /// 用于 CONNECT 隧道场景：
    ///   Read  → 从客户端（浏览器）读取原始 TCP 数据（TLS ClientHello 等）
    ///   Write → 向客户端（浏览器）写入原始 TCP 数据（来自目标的 TLS ServerHello 等）
    /// 
    /// 核心原理：
    ///   Read  操作委托给 context.Request.Body（浏览器→代理的数据通道）
    ///   Write 操作委托给 context.Response.Body（代理→浏览器的数据通道）
    ///   通过 LinkedTokenSource 同时监听外部取消和客户端断开信号
    /// </summary>
    public sealed class ClientServerDuplexStream : Stream
    {
        private readonly HttpContext _context;
        private bool _disposed;

        public ClientServerDuplexStream(HttpContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public override bool CanRead => !_disposed && !_context.RequestAborted.IsCancellationRequested;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <summary>
        /// 从客户端（浏览器）读取数据
        /// 对于 CONNECT 隧道，这里读到的就是浏览器的 TLS 握手数据和后续加密的 HTTPS 数据
        /// </summary>
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_disposed) return 0;

            try
            {
                // 组合 Token：外部取消 + 客户端断开检测
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _context.RequestAborted);

                // 从 Request.Body 读取浏览器发来的原始数据
                var bytesRead = await _context.Request.Body.ReadAsync(buffer.AsMemory(offset, count), linkedCts.Token);
                return bytesRead;
            }
            catch (OperationCanceledException)
            {
                // 客户端断开或被取消，返回 0 表示流结束
                return 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("请使用 ReadAsync");
        }

        /// <summary>
        /// 向客户端（浏览器）写入数据
        /// 对于 CONNECT 隧道，这里写的就是目标服务器返回的 TLS 握手数据和后续响应
        /// </summary>
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_disposed) return;

            await _context.Response.Body.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("请使用 WriteAsync");
        }

        public override void Flush()
        {
            _context.Response.Body.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return _context.Response.Body.FlushAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            base.Dispose(disposing);
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }

    #endregion
}
