using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SpeedHub.Core.Configuration;
using SpeedHub.Core.Proxy;

namespace SpeedHub.Core.Middleware;

/// <summary>
/// HTTP 代理中间件 - 拦截 38457 端口入站请求并转发给 HttpProxyHandler
/// 
/// 工作流程：
///   浏览器 -> :38457 (本中间件) -> 解析目标域名 -> DNS/YARP -> 目标服务器
/// 
/// 支持两种代理协议：
///   1. CONNECT 隧道（HTTPS 请求）：用于 HTTPS 站点的 TLS 隧道
///   2. 普通代理转发（HTTP 请求）：直接提取 Host 头中的目标域名
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
        // 只处理来自代理端口（38457）的请求，其他请求放行给后续中间件
        var request = context.Request;
        string? targetDomain = null;

        if (request.Method == "CONNECT")
        {
            // HTTPS CONNECT 隧道请求
            // 格式: CONNECT github.com:443 HTTP/1.1
            var connectTarget = request.Path.Value?.Trim('/');
            if (!string.IsNullOrEmpty(connectTarget))
            {
                targetDomain = connectTarget;
                _logger.LogInformation("[PROXY] CONNECT 隧道: {Target}", targetDomain);
            }
        }
        else
        {
            // 普通 HTTP 代理请求 - 从 Request.Host 或原始请求行获取目标
            // HTTP 代理请求的格式：
            //   GET http://github.com/path HTTP/1.1
            //   Host: github.com
            
            // 尝试从原始 URL 获取目标（HTTP 代理标准方式）
            var rawUrl = context.Request.Headers["X-Original-URL"].FirstOrDefault()
                      ?? request.Scheme + "://" + request.Host.Value + request.PathBase + request.Path + request.QueryString;

            // 从完整 URL 中提取 host
            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            {
                targetDomain = uri.Host;
                
                // 如果目标域名和当前服务不同，说明是代理请求
                if (!string.Equals(targetDomain, request.Host.Host, StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(request.Headers["Proxy-Connection"]))
                {
                    _logger.LogDebug("[PROXY] HTTP 代理请求: {Method} {Scheme}://{Host}{Path}",
                        request.Method, uri.Scheme, targetDomain, uri.PathAndQuery);
                }
                else
                {
                    // 可能是对代理端口自身的 API 请求（如 /api/health），放行
                    targetDomain = null;
                }
            }

            // 备用：从 Request.Target（ASP.NET Core 的原始请求目标）解析
            if (string.IsNullOrEmpty(targetDomain))
            {
                var rawTarget = request.Headers[":authority"].FirstOrDefault();
                if (!string.IsNullOrEmpty(rawTarget))
                {
                    var match = HostPortRegex.Match(rawTarget);
                    targetDomain = match.Success ? match.Groups[1].Value : rawTarget;
                }
            }
        }

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

            // 委托给 HttpProxyHandler 处理
            _logger.LogInformation("[PROXY] 转发请求: {Method} -> {Target}",
                request.Method, targetDomain);

            await proxyHandler.HandleProxyRequestAsync(context, targetDomain);
            return;
        }

        // 不是代理请求，交给管道下一个中间件处理
        await _next(context);
    }

    /// <summary>
    /// 检查是否为本地回环地址
    /// </summary>
    private static bool IsLocalAddress(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;

        var lower = host.ToLowerInvariant();
        return lower is "localhost"
            or "127.0.0.1"
            or "::1"
            or "[::1]"
            || lower.StartsWith("127.")
            || lower.StartsWith("10.")
            || lower.StartsWith("172.16.")
            || lower.StartsWith("172.17.")
            || lower.StartsWith("172.18.")
            || lower.StartsWith("172.19.")
            || lower.StartsWith("172.2")
            || lower.StartsWith("172.3")
            || lower.StartsWith("192.168.");
    }
}
