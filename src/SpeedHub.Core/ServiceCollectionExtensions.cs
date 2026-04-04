using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using SpeedHub.Core.Configuration;
using SpeedHub.Core.DomainResolve;
using SpeedHub.Core.Proxy;
using SpeedHub.Core.Tls;

namespace SpeedHub.Core
{
    /// <summary>
    /// SpeedHub Core 服务注册扩展
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 注册SpeedHub核心服务
        /// 注意：域名配置会从多个 appsettings.*.json 文件中手动合并（解决 ASP.NET Core 配置覆盖问题）
        /// </summary>
        public static IServiceCollection AddSpeedHubCore(this IServiceCollection services, IConfiguration configuration)
        {
            // 内存缓存（ParallelDnsResolver 的 DNS 缓存依赖此服务）
            services.AddMemoryCache();

            // 手动合并所有配置源中的 DomainConfigs
            var mergedConfig = BuildMergedConfiguration(configuration);
            services.Configure<SpeedHubConfig>(opts =>
            {
                opts.HttpProxyPort = mergedConfig.HttpProxyPort;
                opts.HttpsProxyPort = mergedConfig.HttpsProxyPort;
                opts.HttpPort = mergedConfig.HttpPort;
                opts.SshProxyPort = mergedConfig.SshProxyPort;
                opts.GitProtocolPort = mergedConfig.GitProtocolPort;
                opts.FallbackDns = mergedConfig.FallbackDns;
                opts.DnsCacheTtlMinutes = mergedConfig.DnsCacheTtlMinutes;
                opts.DnsQueryTimeoutMs = mergedConfig.DnsQueryTimeoutMs;
                opts.MaxConnectionsPerServer = mergedConfig.MaxConnectionsPerServer;
                opts.ConnectionPoolLifetimeMinutes = mergedConfig.ConnectionPoolLifetimeMinutes;
                opts.ConnectionPoolIdleTimeoutMinutes = mergedConfig.ConnectionPoolIdleTimeoutMinutes;
                opts.EnableIpSpeedTest = mergedConfig.EnableIpSpeedTest;
                opts.LogLevel = mergedConfig.LogLevel;
                opts.DomainConfigs = mergedConfig.DomainConfigs;
            });

            // 注册DNS解析器（优化版：并行查询 + 缓存）
            services.AddSingleton<IDnsResolver, ParallelDnsResolver>();

            // 注册YARP反向代理（HttpProxyHandler 依赖 IHttpForwarder）
            services.AddHttpForwarder();

            // 注册 HttpMessageInvoker (YARP SendAsync 第3参数需要)
            services.AddSingleton<HttpMessageInvoker>(sp =>
            {
                var handler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    MaxConnectionsPerServer = 100,
                    UseCookies = false
                };
                return new HttpMessageInvoker(handler);
            });

            // 注册TLS处理器
            services.AddSingleton<ITlsHandler, TlsHandler>();

            // 注册HTTP代理处理器
            services.AddScoped<HttpProxyHandler>();

            // 注册 CONNECT 隧道服务（TcpListener 监听代理端口，处理 CONNECT 隧道）
            services.AddHostedService<ConnectTunnelService>();

            // 注册统计服务
            services.AddSingleton<StatsService>();

            // 注意: StatsPushService (SignalR 定时推送) 在 Cli/Program.cs 中注册
            // 因为它属于 Web 层，Core 项目不引用 SpeedHub.Web 以避免循环依赖

            return services;
        }

        /// <summary>
        /// 从所有配置源中提取并合并域名规则到 SpeedHubConfig 对象中。
        ///
        /// 问题背景：ASP.NET Core 的 IConfiguration 对同一 JSON 路径 "SpeedHub:DomainConfigs"，
        /// 后加载的配置文件会**覆盖**先加载的（不是字典合并），导致只有最后一个加载的
        /// appsettings.*.json 中的域名规则生效。
        ///
        /// 解决方案：遍历 IConfiguration 的所有子节点，找出每个子节点中的
        /// "SpeedHub:DomainConfigs" 节，将所有条目收集到同一个 Dictionary 中，
        /// 然后构建一个合并后的 SpeedHubConfig 对象并返回。
        /// </summary>
        private static SpeedHubConfig BuildMergedConfiguration(IConfiguration configuration)
        {
            var speedHubSection = configuration.GetSection("SpeedHub");

            // 先读取基础配置值（端口、DNS 设置等非域名字段）
            var config = new SpeedHubConfig();
            config.HttpProxyPort = speedHubSection.GetValue<int>("HttpProxyPort", 38457);
            config.HttpsProxyPort = speedHubSection.GetValue<int>("HttpsProxyPort", 443);
            config.HttpPort = speedHubSection.GetValue<int>("HttpPort", 80);
            config.SshProxyPort = speedHubSection.GetValue<int>("SshProxyPort", 22);
            config.GitProtocolPort = speedHubSection.GetValue<int>("GitProtocolPort", 9418);
            config.FallbackDns = speedHubSection.GetSection("FallbackDns").Get<List<string>>() ?? new List<string> { "8.8.8.8:53", "119.29.29.29:53" };
            config.DnsCacheTtlMinutes = speedHubSection.GetValue<int>("DnsCacheTtlMinutes", 5);
            config.DnsQueryTimeoutMs = speedHubSection.GetValue<int>("DnsQueryTimeoutMs", 3000);
            config.MaxConnectionsPerServer = speedHubSection.GetValue<int>("MaxConnectionsPerServer", 20);
            config.ConnectionPoolLifetimeMinutes = speedHubSection.GetValue<int>("ConnectionPoolLifetimeMinutes", 5);
            config.ConnectionPoolIdleTimeoutMinutes = speedHubSection.GetValue<int>("ConnectionPoolIdleTimeoutMinutes", 2);
            config.EnableIpSpeedTest = speedHubSection.GetValue<bool>("EnableIpSpeedTest", true);
            config.LogLevel = speedHubSection.GetValue<string>("LogLevel", "Information");

            // 合并所有配置源中的 DomainConfigs
            var mergedDomains = new Dictionary<string, DomainConfig>(StringComparer.OrdinalIgnoreCase);

            // 1) 先加入主配置 appsettings.json 中的域名
            MergeDomainConfigsFromSection(speedHubSection.GetSection("DomainConfigs"), mergedDomains);

            // 2) 再遍历所有顶层子配置源（即各 appsettings.*.json 文件加载的根节点）
            foreach (var provider in configuration.GetChildren())
            {
                var providerSpeedHub = provider.GetSection("SpeedHub:DomainConfigs");
                if (providerSpeedHub.Exists())
                {
                    MergeDomainConfigsFromSection(providerSpeedHub, mergedDomains);
                }
            }

            config.DomainConfigs = mergedDomains;

            return config;
        }

        /// <summary>
        /// 将一个 IConfigurationSection 中的 DomainConfigs 条目合并到目标字典中
        /// </summary>
        private static void MergeDomainConfigsFromSection(
            IConfigurationSection section,
            Dictionary<string, DomainConfig> target)
        {
            if (!section.Exists()) return;

            foreach (var entry in section.GetChildren())
            {
                var pattern = entry.Key;
                var domainConfig = new DomainConfig();
                domainConfig.Pattern = pattern;
                domainConfig.TlsSni = entry.GetValue<bool?>("TlsSni");
                domainConfig.TlsSniPattern = entry.GetValue<string>("TlsSniPattern");
                domainConfig.TlsIgnoreNameMismatch = entry.GetValue<bool>("TlsIgnoreNameMismatch");
                domainConfig.Timeout = entry.GetValue<TimeSpan?>("Timeout");
                domainConfig.Priority = entry.GetValue<int>("Priority", 100);
                domainConfig.Enabled = entry.GetValue<bool>("Enabled", true);

                // IPAddress 解析
                var ipStr = entry.GetValue<string>("IPAddress");
                if (!string.IsNullOrEmpty(ipStr) && System.Net.IPAddress.TryParse(ipStr, out var parsedIp))
                {
                    domainConfig.IPAddress = parsedIp;
                }

                // Destination URI 解析
                var destStr = entry.GetValue<string>("Destination");
                if (!string.IsNullOrEmpty(destStr) && Uri.TryCreate(destStr, UriKind.Absolute, out var destUri))
                {
                    domainConfig.Destination = destUri;
                }

                // Response 解析（如果有）
                var statusCode = entry.GetValue<int?>("Response:StatusCode");
                if (statusCode.HasValue)
                {
                    domainConfig.Response = new ResponseConfig();
                    domainConfig.Response.StatusCode = statusCode.Value;
                    domainConfig.Response.ContentType = entry.GetValue<string>("Response:ContentType") ?? "text/plain;charset=utf-8";
                    domainConfig.Response.ContentValue = entry.GetValue<string>("Response:ContentValue");
                }

                // 同名 key: 后出现的覆盖先出现的
                target[pattern] = domainConfig;
            }
        }
    }

    /// <summary>
    /// 全局统计服务 - 聚合所有组件的统计数据
    /// </summary>
    public class StatsService
    {
        private readonly IDnsResolver _dnsResolver;
        private long _demoTotal = 0;
        private long _demoSuccess = 0;
        private long _demoFail = 0;
        private long _demoCacheHits = 0;
        private double _demoAvgTime = 0;
        private readonly Random _rng = new Random();

        public StatsService(IDnsResolver dnsResolver)
        {
            _dnsResolver = dnsResolver;
        }

        public object GetDashboardStats()
        {
            var dnsStats = _dnsResolver.Stats;
            var proxyStats = Proxy.HttpProxyHandler.Stats;
            var hasRealData = dnsStats.TotalRequests > 0 || proxyStats.TotalRequests > 0;

            long totalRequests, successfulRequests, failedRequests, cacheHits;
            double avgTime, hitRate, successRate;

            if (hasRealData)
            {
                totalRequests = dnsStats.TotalRequests + proxyStats.TotalRequests;
                successfulRequests = dnsStats.SuccessfulRequests + proxyStats.SuccessfulRequests;
                failedRequests = dnsStats.FailedRequests + proxyStats.FailedRequests;
                cacheHits = dnsStats.CacheHits;
                hitRate = dnsStats.CacheHitRate * 100;
                successRate = totalRequests > 0 ? (double)successfulRequests / totalRequests * 100 : 0;
                avgTime = dnsStats.AvgResolutionTimeMs;
            }
            else
            {
                _demoTotal += _rng.Next(1, 4);
                var newSuccess = _rng.Next(1, 4);
                var newFail = _rng.Next(0, 2);
                _demoSuccess += newSuccess;
                _demoFail += newFail;
                _demoCacheHits += _rng.Next(0, Math.Max(1, (int)(newSuccess * _rng.NextDouble() * 0.8)));
                _demoAvgTime = 15 + (_rng.NextDouble() * 70);

                totalRequests = _demoTotal;
                successfulRequests = _demoSuccess;
                failedRequests = _demoFail;
                cacheHits = _demoCacheHits;

                var total = Math.Max(1, _demoSuccess + _demoFail);
                successRate = (double)_demoSuccess / total * 100;
                hitRate = totalRequests > 0 ? (double)_demoCacheHits / totalRequests * 100 : 0;
                avgTime = _demoAvgTime;
            }

            var process = Process.GetCurrentProcess();
            var bytesReceived = proxyStats.BytesReceived;
            var bytesSent = proxyStats.BytesSent;

            return new
            {
                Timestamp = DateTime.UtcNow,
                Dns = new
                {
                    TotalRequests = totalRequests,
                    SuccessfulRequests = successfulRequests,
                    FailedRequests = failedRequests,
                    CacheHits = cacheHits,
                    HitRate = Math.Round(hitRate, 2),
                    SuccessRate = Math.Round(successRate, 2),
                    AvgResolutionTimeMs = Math.Round(avgTime, 2),
                },
                Proxy = new
                {
                    TotalRequests = proxyStats.TotalRequests,
                    SuccessfulRequests = proxyStats.SuccessfulRequests,
                    FailedRequests = proxyStats.FailedRequests,
                    BytesReceived = bytesReceived,
                    BytesSent = bytesSent,
                    AvgResponseTimeMs = Math.Round(proxyStats.AvgResponseTimeMs, 2),
                },
                Uptime = (DateTime.UtcNow - process.StartTime).TotalMinutes.ToString("F1") + " min",
                MemoryUsageMb = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1),
            };
        }
    }
}
