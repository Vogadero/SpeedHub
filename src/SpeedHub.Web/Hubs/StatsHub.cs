using Microsoft.AspNetCore.SignalR;
using SpeedHub.Core;
using Timer = System.Timers.Timer;

namespace SpeedHub.Web.Hubs
{
    /// <summary>
    /// 实时统计数据 SignalR Hub
    /// </summary>
    public class StatsHub : Hub
    {
        private readonly ILogger<StatsHub> _logger;
        private readonly StatsService _statsService;

        public StatsHub(ILogger<StatsHub> logger, StatsService statsService)
        {
            _logger = logger;
            _statsService = statsService;
        }

        /// <summary>
        /// 连接建立时发送当前统计
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation("Dashboard客户端连接: {ConnectionId}", Context.ConnectionId);
            
            // 发送初始数据
            var stats = _statsService.GetDashboardStats();
            await Clients.Caller.SendAsync("ReceiveStats", stats);
            
            await base.OnConnectedAsync();
        }

        /// <summary>
        /// 连接断开时
        /// </summary>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation("Dashboard客户端断开: {ConnectionId}", Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
    }

    /// <summary>
    /// 统计推送服务（定时向所有连接的客户端推送数据）
    /// 
    /// ⚠️ 此服务必须在 Startup.ConfigureServices 中注册：
    ///   services.AddHostedService&lt;StatsPushService&gt;();
    /// 否则 Timer 永远不会启动，前端不会收到实时更新。
    /// </summary>
    public class StatsPushService : IHostedService, IDisposable
    {
        private readonly IHubContext<StatsHub> _hubContext;
        private readonly StatsService _statsService;
        private readonly ILogger<StatsPushService> _logger;
        private Timer? _timer;

        public StatsPushService(
            IHubContext<StatsHub> hubContext,
            StatsService statsService,
            ILogger<StatsPushService> logger)
        {
            _hubContext = hubContext;
            _statsService = statsService;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("StatsPushService 启动 - 每2秒推送统计数据");

            // 每2秒推送一次统计数据
            _timer = new Timer(2000);
            _timer.Elapsed += async (_, _) =>
            {
                try
                {
                    var stats = _statsService.GetDashboardStats();
                    await _hubContext.Clients.All.SendAsync("ReceiveStats", stats);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "推送统计数据失败");
                }
            };
            _timer.AutoReset = true;
            _timer.Start();
            
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("StatsPushService 停止");
            _timer?.Stop();
            _timer?.Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
