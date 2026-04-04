using System.Net;
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

            // ========== 普通 HTTP 代理请求 ==========
            // 浏览器发送格式: GET http://github.com/path HTTP/1.1
            // 注意：CONNECT 请求由 ConnectTunnelService 处理，不会到达这里

            // 策略 1: 检查 Proxy-Connection 头（HTTP 代理请求的标志性头）
            bool isProxyRequest = !string.IsNullOrEmpty(request.Headers["Proxy-Connection"]);

            // 策略 2: 从 Host 头推断目标域名（最常见情况）
            if (!isProxyRequest)
            {
                var host = request.Headers["Host"].FirstOrDefault();
                if (!string.IsNullOrEmpty(host))
                {
                    // 移除端口号（如果有）
                    var colonIndex = host.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        host = host.Substring(0, colonIndex);
                    }
                    
                    // 如果 Host 不是本地地址，说明是代理请求
                    if (!IsLocalAddress(host))
                    {
                        targetDomain = host;
                        isProxyRequest = true;
                        _logger.LogInformation("[PROXY] HTTP 代理目标 (from Host): {Target}", targetDomain);
                    }
                }
            }

            // 策略 3: 从 X-Original-URL 头解析目标
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
