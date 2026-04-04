using System.Net;

namespace SpeedHub.Core.Configuration
{
    /// <summary>
    /// 域名配置项
    /// </summary>
    public class DomainConfig
    {
    /// <summary>
    /// 域名匹配模式（支持通配符 *）
    /// </summary>
    public string Pattern { get; set; } = "*";
    
    /// <summary>
    /// TLS握手时是否发送SNI
    /// </summary>
    public bool? TlsSni { get; set; }
    
    /// <summary>
    /// SNI表达式模板，支持 @domain @ipaddress @random 变量
    /// </summary>
    public string? TlsSniPattern { get; set; }
    
    /// <summary>
    /// 是否忽略服务器证书域名不匹配
    /// </summary>
    public bool TlsIgnoreNameMismatch { get; set; }
    
    /// <summary>
    /// 请求超时时长
    /// </summary>
    public TimeSpan? Timeout { get; set; }
    
    /// <summary>
    /// 请求的目标IP地址
    /// </summary>
    public IPAddress? IPAddress { get; set; }
    
    /// <summary>
    /// 请求目的地重定向（CDN替换等）
    /// </summary>
    public Uri? Destination { get; set; }
    
    /// <summary>
    /// 自定义响应（阻断请求）
    /// </summary>
    public ResponseConfig? Response { get; set; }
    
    /// <summary>
    /// 配置优先级（数字越小越优先）
    /// </summary>
    public int Priority { get; set; } = 100;
    
    /// <summary>
    /// 是否启用此配置
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 自定义响应配置
/// </summary>
public class ResponseConfig
{
    /// <summary>
    /// HTTP状态码
    /// </summary>
    public int StatusCode { get; set; } = 404;
    
    /// <summary>
    /// Content-Type
    /// </summary>
    public string ContentType { get; set; } = "text/plain;charset=utf-8";
    
    /// <summary>
    /// 响应内容
    /// </summary>
    public string? ContentValue { get; set; }
    }
}
