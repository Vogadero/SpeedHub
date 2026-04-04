using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;
using SpeedHub.Core.Configuration;

namespace SpeedHub.Core.Tls;

/// <summary>
/// TLS处理器 - 管理证书和TLS连接配置
/// </summary>
public class TlsHandler : ITlsHandler
{
    private readonly IOptions<SpeedHubConfig> _config;
    private readonly ILogger<TlsHandler> _logger;
    
    /// <summary>
    /// 自签名CA证书（每台主机唯一）
    /// </summary>
    public X509Certificate2? CaCertificate { get; private set; }
    
    public TlsHandler(
        IOptions<SpeedHubConfig> config,
        ILogger<TlsHandler> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// 初始化TLS处理器 - 生成或加载CA证书
    /// </summary>
    public void Initialize(string certDirectory)
    {
        var certPath = Path.Combine(certDirectory, "speedhub.cer");
        var keyPath = Path.Combine(certDirectory, "speedhub.key");
        
        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            // 加载现有证书
            try
            {
                var certPem = File.ReadAllText(certPath);
                var keyPem = File.ReadAllText(keyPath);
                
                // TODO: 从PEM创建X509Certificate2
                _logger.LogInformation("已加载现有CA证书: {Path}", certPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载CA证书失败，将生成新证书");
            }
        }
        
        // 如果没有证书，生成新的
        if (CaCertificate == null)
        {
            GenerateSelfSignedCaCert(certPath, keyPath);
        }
    }

    /// <summary>
    /// 获取域名的TLS连接配置
    /// </summary>
    public TlsConnectionConfig GetTlsConfig(string domain)
    {
        var domainConfig = GetDomainConfig(domain) ?? new DomainConfig();
        
        return new TlsConnectionConfig
        {
            SendSni = domainConfig.TlsSni ?? true,
            SniPattern = domainConfig.TlsSniPattern ?? domain,
            IgnoreNameMismatch = domainConfig.TlsIgnoreNameMismatch,
        };
    }
    
    /// <summary>
    /// 为指定域名动态生成服务器证书
    /// </summary>
    public X509Certificate2 GenerateServerCert(string domain)
    {
        if (CaCertificate == null)
            throw new InvalidOperationException("CA证书未初始化");
        
        // 使用CA签发域名证书
        _logger.LogDebug("为域名生成证书: {Domain}", domain);
        
        // TODO: 实现基于BouncyCastle或System.Security.Cryptography的证书签发
        throw new NotImplementedException("证书生成功能待实现");
    }

    private DomainConfig? GetDomainConfig(string domain)
    {
        var configs = _config.Value.DomainConfigs;
        
        if (configs.TryGetValue(domain, out var exactConfig))
            return exactConfig.Enabled ? exactConfig : null;
        
        return configs
            .Where(c => c.Key.Contains('*') && IsWildcardMatch(c.Key, domain))
            .OrderBy(c => c.Value.Priority)
            .Select(c => c.Value)
            .FirstOrDefault();
    }
    
    private bool IsWildcardMatch(string pattern, string input)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }
    
    private void GenerateSelfSignedCaCert(string certPath, string keyPath)
    {
        // TODO: 使用OpenSSL或BouncyCastle生成自签名CA证书
        _logger.LogInformation("正在生成新的自签名CA证书...");
        
        // 占位实现 - 生产环境应使用完整的证书生成逻辑
        _logger.LogWarning("⚠️ 证书生成功能待完整实现，请使用预生成的测试证书");
    }
}

/// <summary>
/// TLS处理器接口
/// </summary>
public interface ITlsHandler
{
    /// <summary>
    /// CA证书
    /// </summary>
    X509Certificate2? CaCertificate { get; }
    
    /// <summary>
    /// 初始化
    /// </summary>
    void Initialize(string certDirectory);
    
    /// <summary>
    /// 获取TLS配置
    /// </summary>
    TlsConnectionConfig GetTlsConfig(string domain);
    
    /// <summary>
    /// 生成服务器证书
    /// </summary>
    X509Certificate2 GenerateServerCert(string domain);
}

/// <summary>
/// TLS连接配置
/// </summary>
public class TlsConnectionConfig
{
    /// <summary>
    /// 是否发送SNI
    /// </summary>
    public bool SendSni { get; set; } = true;
    
    /// <summary>
    /// SNI值
    /// </summary>
    public string SniPattern { get; set; } = "";
    
    /// <summary>
    /// 是否忽略证书名称不匹配
    /// </summary>
    public bool IgnoreNameMismatch { get; set; } = false;
}
