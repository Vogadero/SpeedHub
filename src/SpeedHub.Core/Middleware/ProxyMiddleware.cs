using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SpeedHub.Core.Configuration;
using SpeedHub.Core.Proxy;

namespace SpeedHub.Core.Middleware
{
    /// <summary>
    /// HTTP 代理中间件 - 拦截代理端口(38457)的入站请求并转发给 HttpProxyHandler
    ///
    /// 工作流程：
    ///   浏览器 -> :38457 (本中间件) -> 解析目标域名 -> DNS/YARP -> 目标服务器
    ///
    /// 支持两种代理协议：
    ///   1. CONNECT 隧道（HTTPS 请求）：浏览器发 CONNECT 建立到目标的 TLS 隧道
    ///   2. 普通代理转发（HTTP 请求）：GET http://github.com/... 格式
    /// </summary>
    public class ProxyMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ProxyMiddleware> _logger;
        private static readonly Regex HostPortRegex = new(@"^(.+?):(\d+)$", RegexOptions.Compiled);

        public ProxyMiddleware(RequestDelegate next, ILogger<ProxyMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, HttpProxyHandler proxyHandler)
        {
            var request = context.Request;

            // === 诊断：记录所有到达此中间件的请求 ===
            var localPort = context.Connection.LocalPort;
            _logger.LogInformation(
                "[PROXY-IN] {Method} {Scheme}://{Host}{Path} | LocalPort={LocalPort} RemoteIP={RemoteIP}",
                request.Method, request.Scheme, request.Host.Value,
                request.Path + request.QueryString,
                localPort, context.Connection.RemoteIpAddress);

            string? targetDomain = null;

            if (request.Method == "CONNECT")
            {
                // ========== HTTPS CONNECT 隧道请求 ==========
                // 浏览器发送格式: CONNECT github.com:443 HTTP/1.1
                //
                // ⚠️ 关键：Kestrel 不会把 CONNECT 后的数据路由到 Request.Body
                // 必须直接从底层 Socket 读取原始 TCP 字节流
                //
                // 策略：尝试从 Features 获取底层 Socket
                var socket = GetClientSocket(context);
                if (socket != null)
                {
                    // 提取目标域名
                    var connectTarget = request.Path.Value?.Trim('/');
                    if (string.IsNullOrEmpty(connectTarget) || connectTarget == "" || connectTarget.StartsWith("/"))
                    {
                        var authority = request.Headers["Host"].FirstOrDefault()
                                     ?? request.Headers[":authority"].FirstOrDefault();
                        if (!string.IsNullOrEmpty(authority))
                        {
                            var match = HostPortRegex.Match(authority);
                            targetDomain = match.Success ? match.Groups[1].Value : authority;
                        }
                    }
                    else
                    {
                        targetDomain = connectTarget;
                    }

                    if (!string.IsNullOrEmpty(targetDomain))
                    {
                        _logger.LogInformation("[PROXY] CONNECT 隧道目标: {Target} (通过底层 Socket)", targetDomain);
                        
                        // 直接处理 CONNECT 隧道（绕过 Kestrel 的 HTTP 管道）
                        await proxyHandler.HandleConnectTunnelWithSocketAsync(socket, targetDomain);
                        return; // 隧道完成后直接返回，不走 Kestrel 的响应管道
                    }
                }

                // 如果无法获取底层 Socket，回退到旧方案（可能不工作）
                _logger.LogWarning("[PROXY] 无法获取底层 Socket，回退到 HttpContext 方案");
                
                // 从 Path 或 Host 提取目标
                var connectTarget2 = request.Path.Value?.Trim('/');
                if (!string.IsNullOrEmpty(connectTarget2) && connectTarget2 != "" && !connectTarget2.StartsWith("/"))
                {
                    targetDomain = connectTarget2;
                }
                if (string.IsNullOrEmpty(targetDomain))
                {
                    var authority = request.Headers["Host"].FirstOrDefault()
                                 ?? request.Headers[":authority"].FirstOrDefault();
                    if (!string.IsNullOrEmpty(authority))
                    {
                        var match = HostPortRegex.Match(authority);
                        targetDomain = match.Success ? match.Groups[1].Value : authority;
                    }
                }
            }
            else
            {
                // ========== 普通 HTTP 代理请求 ==========
                // 浏览器发送格式: GET http://github.com/path HTTP/1.1

                // 策略 1: 检查 Proxy-Connection 头（HTTP 代理请求的标志性头）
                bool isProxyRequest = !string.IsNullOrEmpty(request.Headers["Proxy-Connection"]);

                // 策略 2: 从 X-Original-URL 头解析目标
                if (!isProxyRequest)
                {
                    var rawUrl = request.Headers["X-Original-URL"].FirstOrDefault();
                    if (!string.IsNullOrEmpty(rawUrl) && Uri.TryCreate(rawUrl, UriKind.Absolute, out var proxyUri))
                    {
                        targetDomain = proxyUri.Host;
                        isProxyRequest = true;
                        _logger.LogInformation("[PROXY] HTTP 代理目标 (from X-Original-URL): {Target}", targetDomain);
                    }
                }

                // 如果不是代理请求，放行
                if (!isProxyRequest || string.IsNullOrEmpty(targetDomain))
                {
                    _logger.LogInformation("[PROXY] 非代理请求，放行: {Method} {Path}", request.Method, request.Path);
                    await _next(context);
                    return;
                }
            }

            // ========== 执行代理转发 ==========
            if (!string.IsNullOrEmpty(targetDomain))
            {
                // 验证目标不是本地地址
                if (IsLocalAddress(targetDomain))
                {
                    _logger.LogWarning("[PROXY] 拒绝代理到本地地址: {Target}", targetDomain);
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("{\"error\":\"不允许代理到本地地址\"}");
                    return;
                }

                _logger.LogInformation("[PROXY] >>> 转发请求: {Method} -> {Target} (LocalPort={Port})",
                    request.Method, targetDomain, localPort);

                try
                {
                    await proxyHandler.HandleProxyRequestAsync(context, targetDomain);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[PROXY] 转发异常: {Target}", targetDomain);
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = 502;
                        await context.Response.WriteAsJsonAsync(new { error = "代理转发异常", message = ex.Message });
                    }
                }
                return;
            }

            // 无法解析目标，放行
            _logger.LogWarning("[PROXY] 无法解析代理目标，放行: {Method} {Path}", request.Method, request.Path);
            await _next(context);
        }

        /// <summary>
        /// 尝试从 HttpContext 获取客户端底层 Socket
        /// 
        /// 这是 CONNECT 隧道工作的关键——需要从原始 TCP 连接读取/写入数据，
        /// 而不是通过 Kestrel 的 HTTP 抽象层（Request.Body / Response.Body）
        /// </summary>
        private static Socket? GetClientSocket(HttpContext context)
        {
            // 遍历所有 Features 查找包含 Socket 属性的对象
            foreach (var feature in context.Features)
            {
                if (feature.Value == null) continue;
                
                var type = feature.Value.GetType();
                
                // 查找名为 "Socket" 的属性
                var socketProp = type.GetProperty("Socket");
                if (socketProp?.PropertyType == typeof(Socket))
                {
                    return socketProp.GetValue(feature.Value) as Socket;
                }
                
                // 查找类型为 Socket 的属性
                foreach (var prop in type.GetProperties())
                {
                    if (prop.PropertyType == typeof(Socket))
                    {
                        return prop.GetValue(feature.Value) as Socket;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 检查是否为本地回环/私有地址
        /// </summary>
        private static bool IsLocalAddress(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;

            var lower = host.ToLowerInvariant();

            // 精确匹配
            if (lower is "localhost"
                or "127.0.0.1"
                or "::1"
                or "[::1]")
                return true;

            // 前缀匹配
            if (lower.StartsWith("127.")
                || lower.StartsWith("10.")
                || lower.StartsWith("172.16.")
                || lower.StartsWith("172.17.")
                || lower.StartsWith("172.18.")
                || lower.StartsWith("172.19.")
                || lower.StartsWith("172.2")
                || lower.StartsWith("172.3")
                || lower.StartsWith("192.168.")
                || lower.StartsWith("0.")
                || lower.StartsWith("100.64.")  // CGNAT
                || lower.StartsWith("169.254.")) // link-local
                return true;

            return false;
        }
    }
}
