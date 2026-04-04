using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SpeedHub.Core.Configuration;

namespace SpeedHub.Web.Controllers
{
    /// <summary>
    /// 域名配置 API - 返回所有已加载的域名加速规则
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class DomainsController : ControllerBase
    {
    private readonly SpeedHubConfig _config;

    public DomainsController(IOptions<SpeedHubConfig> config)
    {
        _config = config.Value;
    }

    /// <summary>
    /// 获取所有域名规则列表
    /// </summary>
    [HttpGet]
    public IActionResult GetDomains()
    {
        var domains = _config.DomainConfigs
            .Select(kv => new DomainRuleDto
            {
                Pattern = kv.Key,
                TlsSni = kv.Value.TlsSni,
                Destination = kv.Value.Destination?.ToString(),
                TlsIgnoreNameMismatch = kv.Value.TlsIgnoreNameMismatch,
                Enabled = kv.Value.Enabled,
            })
            .OrderBy(d => d.Pattern)
            .ToList();

        return Ok(domains);
    }
}

/// <summary>
/// 域名规则 DTO（前端展示用）
/// </summary>
public class DomainRuleDto
{
    public string Pattern { get; set; } = string.Empty;
    public bool? TlsSni { get; set; }
    public string? Destination { get; set; }
    public bool TlsIgnoreNameMismatch { get; set; }
    public bool Enabled { get; set; }
    }
}
