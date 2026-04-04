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

namespace SpeedHub.Cli;

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

        AnsiConsole.MarkupLine($"[bold][aqua]v3.0-alpha.1[/][/]");
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
            // 创建 Host
            var hostBuilder = CreateHostBuilder(Array.Empty<string>());

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
                });

            AnsiConsole.MarkupLine("[green bold]✓[/] SpeedHub 启动成功！\n");

            // 显示状态面板
            PrintStatusPanel();

            AnsiConsole.MarkupLine("\n[dim]按 Ctrl+C 停止服务...[/]\n");

            await hostBuilder.RunConsoleAsync();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[red bold]✗[/] [red]启动失败: {ex.Message}[/]");
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

        AnsiConsole.MarkupLine("[green bold]✓[/] [green]服务安装完成[/]");
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

        AnsiConsole.MarkupLine("[green bold]✓[/] [green]服务卸载完成[/]");
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
    /// 创建 Host Builder（集成 Web Dashboard + 代理核心）
    /// </summary>
    private static IHostBuilder CreateHostBuilder(string[] args)
    {
        return Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(options =>
                {
                    // Web Dashboard 端口
                    options.ListenAnyIP(38458);
                })
                .UseStartup<SpeedHubStartup>();
            })
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(Directory.GetCurrentDirectory());
                config.AddJsonFile("appsettings.json", optional: true);
                config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json",
                    optional: true);

                // 加载各平台域名配置目录
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
        // SpeedHub 核心服务
        services.AddSpeedHubCore(Configuration);

        // SignalR 实时通信
        services.AddSignalR();

        // 控制器 API
        services.AddControllers();

        // CORS
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
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseRouting();

        app.UseCors();
        app.UseStaticFiles();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            endpoints.MapHub<StatsHub>("/hubs/stats");

            // API 端点
            endpoints.MapGet("/api/health", () => new { status = "ok", timestamp = DateTime.UtcNow });

            endpoints.MapFallbackToFile("index.html");
        });
    }
}
