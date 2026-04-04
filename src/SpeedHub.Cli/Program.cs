using System.IO;
using Spectre.Console;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpeedHub.Core;
using SpeedHub.Web.Hubs;

namespace SpeedHub.Cli
{
    /// <summary>
    /// SpeedHub 命令行入口
    /// </summary>
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            // 漂亮的 Banner
            AnsiConsole.Write(
                new FigletText("SpeedHub")
                    .Color(Color.Aqua));

            AnsiConsole.MarkupLine($"[bold][aqua]v3.0[/][/]");
            AnsiConsole.MarkupLine("[dim]新一代开发者网络加速器[/]\n");

            var command = args.Length > 0 ? args[0] : "run";

            return command switch
            {
                "run" => await RunAsync(),
                "start" => await InstallServiceAsync(),
                "stop" => await UninstallServiceAsync(),
                "--help" or "-h" => ShowHelp(),
                _ => RunAndShowHelp()
            };
        }

        /// <summary>
        /// 显示帮助并返回错误码（用于 switch expression 的默认分支）
        /// </summary>
        private static int RunAndShowHelp()
        {
            ShowHelp();
            return 1;
        }

        /// <summary>
        /// 运行代理服务
        /// </summary>
        private static async Task<int> RunAsync()
        {
            try
            {
                // 创建 Host（在 status spinner 内部完成构建，这样启动过程有视觉反馈）
                IHost? host = null;

                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Plain.Foreground(Color.Aqua))
                    .Start("正在启动 SpeedHub...", ctx =>
                    {
                        ctx.Status = "初始化 DNS 解析器...";
                        Thread.Sleep(300);

                        ctx.Status = "加载域名配置...";
                        Thread.Sleep(200);

                        ctx.Status = "启动代理服务器...";
                        
                        try
                        {
                            host = CreateHostBuilder(Array.Empty<string>()).Build();
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException("Host 构建失败: " + ex.Message, ex);
                        }
                    });

                if (host == null)
                {
                    AnsiConsole.MarkupLine("\n[red bold]X[/] [red]Host build failed, cannot start[/]");
                    return 1;
                }

                AnsiConsole.MarkupLine("\n[green bold]=>[/] [green] SpeedHub startup successful![/]");

                // 显示状态面板
                PrintStatusPanel();

                // Web Dashboard 地址（可点击跳转）
                AnsiConsole.MarkupLine("\n[bold]Web Dashboard:[/] [blue underline]http://localhost:38458[/]");
                AnsiConsole.MarkupLine("[bold]HTTP Proxy:[/] [blue underline]http://localhost:38457[/]");
                AnsiConsole.MarkupLine("[dim]按 Ctrl+C 停止服务...[/]\n");

                // 自动打开浏览器跳转到 Dashboard
                OpenBrowser("http://localhost:38458");
                AnsiConsole.MarkupLine("[dim]=> 已自动打开浏览器访问 Dashboard[/]\n");

                // 异步检查最新版本（不阻塞主流程）
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000); // 等待 2 秒再检查，避免影响启动速度
                    await VersionChecker.CheckForUpdatesAsync();
                });

                await host.RunAsync();
                return 0;
            }
            catch (Exception ex)
            {
                // Use plain text (not Markup) to avoid secondary crash if ex.Message contains [ or ]
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[red bold]X[/]");
                AnsiConsole.WriteLine($"  Startup failed: {ex.Message}");
                AnsiConsole.WriteLine($"  {ex}");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]Please check:[/]");
                AnsiConsole.MarkupLine("[yellow]- appsettings.json exists and is valid JSON[/]");
                AnsiConsole.MarkupLine("[yellow]- ports 38457/38458 are not in use[/]");
                AnsiConsole.MarkupLine("[yellow]- logs/ directory for detailed error info[/]");
                AnsiConsole.MarkupLine("[yellow]Press any key to exit...[/]");
                Console.ReadKey(intercept: true);
                return 1;
            }
        }

        /// <summary>
        /// 安装为系统服务
        /// </summary>
        private static async Task<int> InstallServiceAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                AnsiConsole.MarkupLine("[yellow]正在安装 Windows 服务...[/]");
                // TODO: 实现Windows服务安装
            }
            else if (OperatingSystem.IsLinux())
            {
                AnsiConsole.MarkupLine("[yellow]正在安装 systemd 服务...[/]");
                // TODO: 实现systemd服务安装
            }

            AnsiConsole.MarkupLine("[green bold]=>[/] [green]Service installed[/]");
            return 0;
        }

        /// <summary>
        /// 卸载系统服务
        /// </summary>
        private static async Task<int> UninstallServiceAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                AnsiConsole.MarkupLine("[yellow]正在卸载 Windows 服务...[/]");
            }
            else if (OperatingSystem.IsLinux())
            {
                AnsiConsole.MarkupLine("[yellow]正在卸载 systemd 服务...[/]");
            }

            AnsiConsole.MarkupLine("[green bold]=>[/] [green]Service uninstalled[/]");
            return 0;
        }

        /// <summary>
        /// 显示帮助信息
        /// </summary>
        private static int ShowHelp()
        {
            AnsiConsole.Write(new Markup(@"
[bold]用法:[/]
  speedhub [命令]

[bold]命令:[/]
  [aqua]run[/]       启动代理服务（默认）
  [aqua]start[/]     安装并启动为系统服务
  [aqua]stop[/]      停止并卸载系统服务
  [aqua]-h, --help[/] 显示帮助信息

[bold]示例:[/]
  speedhub run              # 直接运行
  speedhub start            # 安装为后台服务
  
[bold]Web Dashboard:[/]
  http://localhost:38458     # 管理界面

[bold]代理端口:[/]
  HTTP Proxy :38457         # HTTP/HTTPS 代理
  HTTPS      :443           # HTTPS 反向代理  
  SSH        :22            # Git SSH 代理
  Git        :9418          # Git 协议代理

[bold]更多信息:[/]
  GitHub: https://github.com/Vogadero/SpeedHub
  文档: https://speedhub.dev/docs
"));
            return 0;
        }

        /// <summary>
        /// 打印状态面板
        /// </summary>
        private static void PrintStatusPanel()
        {
            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            table.AddColumn("[bold]组件[/]");
            table.AddColumn("[bold]端口[/]");
            table.AddColumn("[bold]状态[/]");

            table.AddRow(
                new Markup("[aqua]HTTP/HTTPS 代理[/]"),
                new Text(":38457"),
                new Markup("[green]● 运行中[/]"));

            table.AddRow(
                new Markup("[aqua]HTTPS 反向代理[/]"),
                new Text(":443"),
                new Markup("[green]● 运行中[/]"));

            table.AddRow(
                new Markup("[aqua]SSH 代理[/]"),
                new Text(":22"),
                new Markup("[green]● 运行中[/]"));

            table.AddRow(
                new Markup("[aqua]Git 协议代理[/]"),
                new Text(":9418"),
                new Markup("[green]● 运行中[/]"));

            table.AddRow(
                new Markup("[aqua]Web Dashboard[/]"),
                new Text(":38458"),
                new Markup("[green]● 可用[/]"));

            AnsiConsole.Write(table);
        }

        /// <summary>
        /// 在默认浏览器中打开指定 URL
        /// </summary>
        public static void OpenBrowser(string url)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch
            {
                // 忽略打开浏览器失败（某些环境可能不支持）
            }
        }

        /// <summary>
        /// 创建 Host Builder（集成 Web Dashboard + 代理核心）
        /// 
        /// 注意：Kestrel 只监听内部端口 5000（用于 HTTP 代理请求转发和 Dashboard）。
        /// 代理端口 38457 由 ConnectTunnelService（TcpListener）监听。
        /// </summary>
        private static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<SpeedHubStartup>();
                })
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.SetBasePath(Directory.GetCurrentDirectory());
                    config.AddJsonFile("appsettings.json", optional: true);
                    config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json",
                        optional: true);

                    var appsettingsDir = Path.Combine(Directory.GetCurrentDirectory(), "appsettings");
                    if (Directory.Exists(appsettingsDir))
                    {
                        foreach (var file in Directory.GetFiles(appsettingsDir, "appsettings.*.json"))
                        {
                            config.AddJsonFile(file, optional: false);
                        }
                    }

                    config.AddEnvironmentVariables();
                    config.AddCommandLine(args);
                });
        }
    }

    /// <summary>
    /// ASP.NET Core Startup 类 - 集成 SpeedHub.Web 的所有功能到 CLI
    /// </summary>
    public class SpeedHubStartup
    {
        private IConfiguration Configuration { get; }

        public SpeedHubStartup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSpeedHubCore(Configuration);

            // 注册 SignalR 统计推送服务（定时向 Dashboard 推送实时数据，每2秒）
            // ⚠️ 这是前端自动刷新的关键 - 没有这行 Timer 不会启动
            // 放在 Cli 而非 Core 中，因为 StatsPushService 属于 Web 层
            services.AddHostedService<SpeedHub.Web.Hubs.StatsPushService>();

            services.AddSignalR(options =>
            {
                options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            }).AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });

            services.AddControllers().AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });

            services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                });
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    var logger = context.RequestServices.GetRequiredService<ILogger<SpeedHubStartup>>();
                    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
                    logger.LogError(exception, "请求发生未处理异常: {Path}", context.Request.Path);
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        System.Text.Json.JsonSerializer.Serialize(new { error = "内部错误", message = exception?.Message }));
                });
            });

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseMiddleware<SpeedHub.Core.Middleware.ProxyMiddleware>();

            app.UseCors();
            app.UseStaticFiles();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHub<StatsHub>("/hubs/stats");
                endpoints.MapGet("/api/health", () => new { status = "ok", timestamp = DateTime.UtcNow });
                endpoints.MapFallbackToFile("index.html");
            });
        }
    }
}
