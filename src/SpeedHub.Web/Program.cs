using System.IO;
using Microsoft.Extensions.Options;
using Serilog;
using SpeedHub.Core;
using SpeedHub.Core.Tls;
using SpeedHub.Web.Hubs;

var builder = WebApplication.CreateBuilder(args);

// ==================== 配置 Serilog 日志 ====================
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/speedhub-.txt", 
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

builder.Host.UseSerilog();

// ==================== 注册服务 ====================

// 1. SpeedHub 核心服务
builder.Services.AddSpeedHubCore(builder.Configuration);

// 2. SignalR 实时通信
builder.Services.AddSignalR();

// 3. 控制器（RESTful API）
builder.Services.AddControllers();

// 4. 统计推送后台服务（每2秒向 Dashboard 客户端推送数据）
builder.Services.AddHostedService<StatsPushService>();

// 5. CORS（允许Dashboard跨域访问）
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// ==================== 构建应用 ====================
var app = builder.Build();

// 初始化TLS处理器
var tlsHandler = app.Services.GetRequiredService<ITlsHandler>();
tlsHandler.Initialize(Path.Combine(AppContext.BaseDirectory, "cacert"));

// ==================== 中间件管道 ====================

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// CORS
app.UseCors();

// 静态文件
app.UseStaticFiles();

// 路由
app.MapControllers();
app.MapHub<StatsHub>("/hubs/stats");

// API 端点映射
app.MapGet("/api/health", () => new { status = "ok", timestamp = DateTime.UtcNow })
    .WithName("HealthCheck")
    .WithTags("System");

app.MapGet("/api/stats", (StatsService stats) => stats.GetDashboardStats())
    .WithName("GetStats")
    .WithTags("Monitoring");

app.MapGet("/api/config", (IOptions<SpeedHub.Core.Configuration.SpeedHubConfig> config) => 
{
    // 返回脱敏的配置信息
    var c = config.Value;
    return new
    {
        HttpProxyPort = c.HttpProxyPort,
        FallbackDns = c.FallbackDns.Count,
        DomainConfigsCount = c.DomainConfigs.Count,
        DnsCacheTtlMinutes = c.DnsCacheTtlMinutes,
    };
})
    .WithName("GetConfig")
    .WithTags("Config");

// Dashboard 页面（默认路由）
app.MapFallbackToFile("index.html");

// 启动日志
Log.Information("SpeedHub v3.0 启动完成");
Log.Information("   Web UI: http://localhost:{Port}", 38458);
Log.Information("   HTTP Proxy: http://localhost:{Port}", builder.Configuration.GetValue("SpeedHub:HttpProxyPort", 80));
Log.Information("   HTTPS Proxy: https://localhost:{Port}", builder.Configuration.GetValue("SpeedHub:HttpsProxyPort", 443));

app.Run();
