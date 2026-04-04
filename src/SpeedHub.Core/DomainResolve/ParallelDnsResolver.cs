using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DnsClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedHub.Core.Configuration;

namespace SpeedHub.Core.DomainResolve;

/// <summary>
/// 并行DNS解析器 - 优化版：并行查询 + 缓存 + 智能IP选择
/// </summary>
public class ParallelDnsResolver : IDnsResolver
{
    private readonly IOptions<SpeedHubConfig> _config;
    private readonly ILogger<ParallelDnsResolver> _logger;
    private readonly IMemoryCache _cache;
    private readonly LookupClient _dnsClient;
    
    // IP延迟历史记录（用于智能选择）
    private readonly ConcurrentDictionary<string, IpLatencyHistory> _ipLatencyHistory = new();
    
    /// <summary>
    /// DNS 解析统计信息
    /// </summary>
    public DnsStats Stats { get; } = new();

    public ParallelDnsResolver(
        IOptions<SpeedHubConfig> config,
        ILogger<ParallelDnsResolver> logger,
        IMemoryCache cache)
    {
        _config = config;
        _logger = logger;
        _cache = cache;
        
        // 配置DNS客户端
        var dnsServers = config.Value.FallbackDns
            .Select(s => new IPEndPoint(
                IPAddress.Parse(s.Split(':')[0]),
                int.Parse(s.Split(':')[1])))
            .ToArray();
        
        _dnsClient = new LookupClient(new LookupClientOptions(dnsServers)
        {
            UseCache = false,  // 我们自己管理缓存
            Timeout = TimeSpan.FromMilliseconds(config.Value.DnsQueryTimeoutMs),
            Retries = 0,       // 不重试，快速失败
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string domain, int port = 443)
    {
        Stats.TotalRequests++;
        
        // 1. 先检查本地缓存
        var cacheKey = $"dns:{domain}:{port}";
        if (_cache.TryGetValue(cacheKey, out CachedIpResult? cached) && cached != null)
        {
            Stats.CacheHits++;
            return cached.IpAddresses;
        }
        
        // 2. 并行查询所有DNS服务器
        var sw = Stopwatch.StartNew();
        var results = await ResolveWithFallbackAsync(domain);
        sw.Stop();
        
        if (results.Count == 0)
        {
            Stats.FailedRequests++;
            throw new DnsResolutionException($"无法解析域名: {domain}");
        }
        
        // 3. 如果启用IP测速，选择最优IP
        IReadOnlyList<IPAddress> finalIps;
        if (_config.Value.EnableIpSpeedTest && results.Count > 1)
        {
            finalIps = SelectBestIp(domain, results.ToList(), port);
            Stats.SpeedTestCount++;
        }
        else
        {
            finalIps = results;
        }
        
        // 4. 写入缓存
        var cacheEntry = new CachedIpResult(finalIps, DateTime.UtcNow);
        _cache.Set(cacheKey, cacheEntry, 
            TimeSpan.FromMinutes(_config.Value.DnsCacheTtlMinutes));
        
        // 更新统计
        var elapsedMs = sw.ElapsedMilliseconds;
        Stats.AvgResolutionTimeMs = 
            (Stats.AvgResolutionTimeMs * (Stats.SuccessfulRequests - 1) + elapsedMs)
            / Math.Max(1, Stats.SuccessfulRequests);
        Stats.SuccessfulRequests++;
        
        _logger.LogInformation("域名解析完成: {Domain}:{Port} → [{Ips}] 耗时: {ElapsedMs}ms",
            domain, port, string.Join(", ", finalIps.Select(ip => ip.ToString())), elapsedMs);
        
        return finalIps;
    }
    
    /// <summary>
    /// 并行查询所有DNS服务器，返回第一个成功的结果
    /// </summary>
    private async Task<List<IPAddress>> ResolveWithFallbackAsync(string domain)
    {
        using var cts = new CancellationTokenSource(_config.Value.DnsQueryTimeoutMs * 2);
        
        // 并行发起所有DNS查询
        var queryTasks = _config.Value.FallbackDns.Select(async dnsEndpoint =>
        {
            try
            {
                var parts = dnsEndpoint.Split(':');
                var serverIp = IPAddress.Parse(parts[0]);
                var serverPort = int.Parse(parts[1]);
                
                var result = await _dnsClient.QueryAsync(domain, QueryType.A,
                    new DnsQueryOptions
                    {
                        RequestDnsSecRecords = false,
                    }, cancellationToken: cts.Token);
                
                if (result.HasError || result.Answers.Count == 0)
                {
                    _logger.LogWarning("DNS查询失败: {Dns}@{Server} → {Error}",
                        domain, dnsEndpoint, result.ErrorMessage ?? "无结果");
                    return null;
                }
                
                var ips = result.Answers.ARecords()
                    .Select(r => r.Address)
                    .ToList();
                    
                if (ips.Count > 0)
                {
                    _logger.LogDebug("DNS查询成功: {Domain}@{Server} → {Ips}",
                        domain, dnsEndpoint, string.Join(", ", ips));
                    return ips;
                }
                
                return null;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                _logger.LogWarning("DNS查询超时: {Domain}@{Server}",
                    domain, dnsEndpoint);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DNS查询异常: {Domain}@{Server}",
                    domain, dnsEndpoint);
                return null;
            }
        }).ToArray();
        
        // 等待任意一个成功
        while (!cts.IsCancellationRequested && queryTasks.Any())
        {
            var completedTask = await Task.WhenAny(queryTasks);
            
            if (completedTask.IsCompletedSuccessfully)
            {
                var result = await completedTask;
                if (result != null && result.Count > 0)
                {
                    cts.Cancel(); // 取消其他任务
                    return result;
                }
            }
            
            // 移除已完成的任务
            queryTasks = queryTasks.Where(t => t != completedTask).ToArray();
        }
        
        return new List<IPAddress>();
    }
    
    /// <summary>
    /// 基于历史延迟数据选择最优IP
    /// </summary>
    private List<IPAddress> SelectBestIp(string domain, List<IPAddress> ips, int port)
    {
        // 并发测试所有IP的TCP连接延迟
        var testTasks = ips.Select(async ip =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(ip, port);
                sw.Stop();
                
                var latency = sw.ElapsedMilliseconds;
                
                // 记录到历史
                UpdateLatencyHistory(ip, latency, success: true);
                
                return (Ip: ip, Latency: latency, Success: true);
            }
            catch
            {
                UpdateLatencyHistory(ip, -1, success: false);
                return (Ip: ip, Latency: -1, Success: false);
            }
        }).ToArray();
        
        var results = Task.WhenAll(testTasks).GetAwaiter().GetResult();
        
        // 选择成功率最高且延迟最低的IP
        var bestResults = results
            .Where(r => r.Success)
            .OrderBy(r =>
            {
                var history = _ipLatencyHistory.GetValueOrDefault(r.Ip.ToString());
                return history?.AverageLatency ?? r.Latency;
            })
            .ThenBy(r => r.Latency)
            .Select(r => r.Ip)
            .ToList();
        
        // 保持原始顺序，但把最优IP放到第一位
        if (bestResults.Count > 0 && !bestResults[0].Equals(ips[0]))
        {
            var orderedIps = ips.ToList();
            orderedIps.Remove(bestResults[0]);
            orderedIps.Insert(0, bestResults[0]);
            return orderedIps;
        }
        
        return ips;
    }
    
    /// <summary>
    /// 更新IP延迟历史记录
    /// </summary>
    private void UpdateLatencyHistory(IPAddress ip, long latencyMs, bool success)
    {
        var key = ip.ToString();
        var history = _ipLatencyHistory.GetOrAdd(key, _ => new IpLatencyHistory());
        history.Record(latencyMs, success);
    }
}

/// <summary>
/// DNS 解析器接口
/// </summary>
public interface IDnsResolver
{
    /// <summary>
    /// 异步解析域名
    /// </summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string domain, int port = 443);
    
    /// <summary>
    /// 统计信息
    /// </summary>
    DnsStats Stats { get; }
}

/// <summary>
/// DNS 解析统计
/// </summary>
public class DnsStats
{
    public long TotalRequests { get; set; }
    public long SuccessfulRequests { get; set; }
    public long FailedRequests { get; set; }
    public long CacheHits { get; set; }
    public long SpeedTestCount { get; set; }
    public double AvgResolutionTimeMs { get; set; }
    
    public double CacheHitRate => TotalRequests > 0 ? (double)CacheHits / TotalRequests : 0;
    public double SuccessRate => TotalRequests > 0 ? (double)SuccessfulRequests / TotalRequests : 0;
}

/// <summary>
/// 缓存的IP结果
/// </summary>
public record CachedIpResult(IReadOnlyList<IPAddress> IpAddresses, DateTime CachedAt);

/// <summary>
/// IP延迟历史记录
/// </summary>
public class IpLatencyHistory
{
    private const int MaxHistorySize = 20;
    private readonly Queue<long> _latencies = new();
    private int _successCount;
    private int _failCount;
    private object _lock = new();
    
    public void Record(long latencyMs, bool success)
    {
        lock (_lock)
        {
            if (success && latencyMs >= 0)
            {
                if (_latencies.Count >= MaxHistorySize)
                    _latencies.Dequeue();
                _latencies.Enqueue(latencyMs);
                _successCount++;
            }
            else
            {
                _failCount++;
            }
        }
    }
    
    public double? AverageLatency
    {
        get
        {
            lock (_lock)
            {
                return _latencies.Count > 0 ? _latencies.Average() : null;
            }
        }
    }
    
    public double SuccessRate
    {
        get
        {
            lock (_lock)
            {
                var total = _successCount + _failCount;
                return total > 0 ? (double)_successCount / total : 0;
            }
        }
    }
}

/// <summary>
/// DNS解析异常
/// </summary>
public class DnsResolutionException : Exception
{
    public string Domain { get; }
    
    public DnsResolutionException(string message, string? domain = null) : base(message)
    {
        Domain = domain ?? string.Empty;
    }
}
