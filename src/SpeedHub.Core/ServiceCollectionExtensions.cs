using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using SpeedHub.Core.Configuration;
using SpeedHub.Core.DomainResolve;
using SpeedHub.Core.Proxy;
using SpeedHub.Core.Tls;

namespace SpeedHub.Core;

/// <summary>
/// SpeedHub Core 服务注册扩展
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册SpeedHub核心服务
    /// </summary>
    public static IServiceCollection AddSpeedHubCore(this IServiceCollection services, IConfiguration configuration)
    {
        // 内存缓存（ParallelDnsResolver 的 DNS 缓存依赖此服务）
        services.AddMemoryCache();
        
        // 绑定配置
        services.Configure<SpeedHubConfig>(configuration.GetSection("SpeedHub"));
        
        // 注册DNS解析器（优化版：并行查询 + 缓存）
        services.AddSingleton<IDnsResolver, ParallelDnsResolver>();
        
        // 注册TLS处理器
        services.AddSingleton<ITlsHandler, TlsHandler>();
        
        // 注册HTTP代理处理器
        services.AddScoped<HttpProxyHandler>();
        
        // 注册统计服务
        services.AddSingleton<StatsService>();
        
        return services;
    }
}

/// <summary>
/// 全局统计服务 - 聚合所有组件的统计数据
/// </summary>
public class StatsService
{
    private readonly IDnsResolver _dnsResolver;
    
    public StatsService(IDnsResolver dnsResolver)
    {
        _dnsResolver = dnsResolver;
    }
    
    public object GetDashboardStats()
    {
        var dnsStats = _dnsResolver.Stats;
        var process = Process.GetCurrentProcess();
        
        return new
        {
            Timestamp = DateTime.UtcNow,
            Dns = new
            {
                TotalRequests = dnsStats.TotalRequests,
                SuccessfulRequests = dnsStats.SuccessfulRequests,
                FailedRequests = dnsStats.FailedRequests,
                CacheHits = dnsStats.CacheHits,
                HitRate = Math.Round(dnsStats.CacheHitRate * 100, 2),
                SuccessRate = Math.Round(dnsStats.SuccessRate * 100, 2),
                AvgResolutionTimeMs = Math.Round(dnsStats.AvgResolutionTimeMs, 2),
            },
            Uptime = (DateTime.UtcNow - process.StartTime).TotalMinutes.ToString("F1") + "分钟",
            MemoryUsageMb = GC.GetGCMemoryInfo().HeapSizeBytes / 1024.0 / 1024.0,
        };
    }
}
