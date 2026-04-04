using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedHub.Core.Configuration;
using SpeedHub.Core.DomainResolve;
using Yarp.ReverseProxy.Forwarder;

namespace SpeedHub.Core.Proxy;

/// <summary>
/// 高性能HTTP代理处理器 - 带连接池和请求日志
/// </summary>
public class HttpProxyHandler
{
    private readonly IHttpForwarder _forwarder;
    private readonly IDnsResolver _dnsResolver;
    private readonly IOptions<SpeedHubConfig> _config;
    private readonly ILogger<HttpProxyHandler> _logger;
    
    // 连接池 - 使用SocketsHttpHandler实现连接复用
    private static readonly ConcurrentDictionary<string, SocketsHttpHandler> _connectionPools = new();
    private static readonly SemaphoreSlim _poolLock = new(1, 1);
    
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
    }

    /// <summary>
    /// 处理HTTP/HTTPS代理请求
    /// </summary>
    public async Task HandleProxyRequestAsync(HttpContext context, string targetDomain)
    {
        var sw = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString("N")[..8];
        
        try
        {
            // 1. 获取域名配置
            var domainConfig = GetDomainConfig(targetDomain);
            
            // 2. 解析目标IP
            int port = context.Request.Scheme == "https" ? 443 : 80;
            IReadOnlyList<IPAddress> ips;
            
            if (domainConfig?.IPAddress != null)
            {
                ips = new[] { domainConfig.IPAddress };
            }
            else if (domainConfig?.Destination != null)
            {
                // CDN重定向模式
                await HandleCdnRedirectAsync(context, domainConfig, requestId);
                return;
            }
            else
            {
                ips = await _dnsResolver.ResolveAsync(targetDomain, port);
            }
            
            // 3. 选择最佳IP
            var targetIp = ips[0];
            
            // 4. 构建目标地址
            var scheme = context.Request.Scheme;
            var destinationPrefix = $"{scheme}://{targetIp}:{port}";
            
            // 5. 使用YARP转发请求（2参数版本）
            var error = await _forwarder.SendAsync(context, destinationPrefix, 
                CreateForwarderRequestConfig(targetDomain, domainConfig));
            
            sw.Stop();
            
            if (error != ForwarderError.None)
            {
                Interlocked.Increment(ref Stats._failedRequests);
                _logger.LogWarning("[{RequestId}] 代理转发错误: {Error} {Target}",
                    requestId, error, destinationPrefix);
                
                context.Response.StatusCode = (int)GetStatusCodeFromError(error);
                await context.Response.WriteAsJsonAsync(new
                    { error = "代理转发失败", code = error.ToString() });
            }
            else
            {
                Interlocked.Increment(ref Stats._successfulRequests);
                _logger.LogInformation(
                    "[{RequestId}] {Method} {Url} responded {Status} in {Elapsed:F2}ms",
                    requestId,
                    context.Request.Method,
                    context.Request.Path + context.Request.QueryString,
                    context.Response.StatusCode,
                    sw.Elapsed.TotalMilliseconds);
            }
        }
        catch (DnsResolutionException ex)
        {
            sw.Stop();
            Interlocked.Increment(ref Stats._dnsErrors);
            
            _logger.LogError(ex, "[{RequestId}] DNS解析失败: {Domain}", requestId, targetDomain);
            context.Response.StatusCode = 502;
            await context.Response.WriteAsJsonAsync(new { error = "DNS解析失败", message = ex.Message });
        }
        catch (Exception ex)
        {
            sw.Stop();
            Interlocked.Increment(ref Stats._errors);
            
            _logger.LogError(ex, "[{RequestId}] 代理异常: {Target}", requestId, targetDomain);
            context.Response.StatusCode = 502;
            await context.Response.WriteAsJsonAsync(new { error = "服务器内部错误" });
        }
        finally
        {
            RecordRequestStats(sw.ElapsedMilliseconds, context.Response.StatusCode);
        }
    }
    
    /// <summary>
    /// 处理CDN重定向请求
    /// </summary>
    private async Task HandleCdnRedirectAsync(HttpContext context, DomainConfig domainConfig, string requestId)
    {
        var destinationBase = domainConfig!.Destination!.ToString().TrimEnd('/');
        var requestPath = context.Request.Path + context.Request.QueryString;
        
        // 构建新的目标URL
        var redirectUrl = destinationBase + requestPath;
        
        _logger.LogInformation("[{RequestId}] CDN重定向: {Original} → {Destination}",
            requestId, context.Request.Host, redirectUrl);
        
        // 使用302临时重定向到CDN镜像
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
        // 将通配符转换为正则表达式
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\.", @"\.") + "$";
        
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }
    
    /// <summary>
    /// 创建转发器配置
    /// </summary>
    private ForwarderRequestConfig CreateForwarderRequestConfig(string host, DomainConfig? config)
    {
        // YARP 2.x: 使用 HttpTransformer 替代 HttpRequestOptions
        var transform = new CustomHeaderTransform(host);
        var requestConfig = new ForwarderRequestConfig
        {
            Version = new Version(2, 0),  // HTTP/2
            Transform = transform,
        };
        
        return requestConfig;
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
        Stats.TotalBytesTransferred += 1024; // 估算值
    }
}

/// <summary>
/// 自定义HTTP转换器 - 设置正确的Host头
/// </summary>
internal class CustomHeaderTransform : HttpTransformer
{
    private readonly string _targetHost;

    public CustomHeaderTransform(string targetHost)
    {
        _targetHost = targetHost;
    }

    public override async ValueTask TransformRequestAsync(HttpContext httpRequestContext,
        HttpRequestMessage proxyRequest, string destinationPrefix)
    {
        // 调用基类默认转换（拷贝请求头、方法等）
        await base.TransformRequestAsync(httpRequestContext, proxyRequest, destinationPrefix);

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
