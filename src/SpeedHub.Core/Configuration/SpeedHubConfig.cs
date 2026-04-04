namespace SpeedHub.Core.Configuration
{
    /// <summary>
    /// SpeedHub 应用配置
    /// </summary>
    public class SpeedHubConfig
    {
    /// <summary>
    /// HTTP 代理端口
    /// </summary>
    public int HttpProxyPort { get; set; } = 38457;
    
    /// <summary>
    /// HTTPS 反向代理端口
    /// </summary>
    public int HttpsProxyPort { get; set; } = 443;
    
    /// <summary>
    /// HTTP 反向代理端口（用于DNS劫持后的流量）
    /// </summary>
    public int HttpPort { get; set; } = 80;
    
    /// <summary>
    /// SSH 代理端口
    /// </summary>
    public int SshProxyPort { get; set; } = 22;
    
    /// <summary>
    /// Git 协议代理端口
    /// </summary>
    public int GitProtocolPort { get; set; } = 9418;
    
    /// <summary>
    /// 备用 DNS 服务器列表（需支持TCP）
    /// </summary>
    public List<string> FallbackDns { get; set; } = new()
    {
        "8.8.8.8:53",
        "119.29.29.29:53",
        "114.114.114.114:53"
    };
    
    /// <summary>
    /// DNS 缓存 TTL（分钟）
    /// </summary>
    public int DnsCacheTtlMinutes { get; set; } = 5;
    
    /// <summary>
    /// DNS 查询超时（毫秒）
    /// </summary>
    public int DnsQueryTimeoutMs { get; set; } = 3000;
    
    /// <summary>
    /// 最大并发连接数
    /// </summary>
    public int MaxConnectionsPerServer { get; set; } = 20;
    
    /// <summary>
    /// 连接池生命周期（分钟）
    /// </summary>
    public int ConnectionPoolLifetimeMinutes { get; set; } = 5;
    
    /// <summary>
    /// 连接池空闲超时（分钟）
    /// </summary>
    public int ConnectionPoolIdleTimeoutMinutes { get; set; } = 2;
    
    /// <summary>
    /// 是否启用 IP 测速选择
    /// </summary>
    public bool EnableIpSpeedTest { get; set; } = true;
    
    /// <summary>
    /// 域名配置字典
    /// </summary>
    public Dictionary<string, DomainConfig> DomainConfigs { get; set; } = new();
    
    /// <summary>
    /// 日志级别
    /// </summary>
    public string LogLevel { get; set; } = "Information";
    }
}
