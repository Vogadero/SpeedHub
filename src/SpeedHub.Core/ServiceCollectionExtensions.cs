using System.Diagnostics;
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

            // 手动合并所有配置源中的 DomainConfigs，然后绑定
            var mergedConfig = BuildMergedConfiguration(configuration);
            services.Configure<SpeedHubConfig>(mergedConfig);

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

        /// <summary>
        /// 从所有配置源中提取并合并域名规则。
        ///
        /// 问题背景：ASP.NET Core 的 IConfiguration 对同一 JSON 路径 "SpeedHub:DomainConfigs"，
        /// 后加载的配置文件会**覆盖**先加载的（不是字典合并），导致只有最后一个加载的
        /// appsettings.*.json 中的域名规则生效。
        ///
        /// 解决方案：遍历 IConfiguration 的所有子节点，找出每个子节点中的
        /// "SpeedHub:DomainConfigs" 节，将所有条目收集到同一个 Dictionary 中，
        /// 然后构建一个合并后的 SpeedHubConfig 对象。
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
}
