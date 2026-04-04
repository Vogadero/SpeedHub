# SpeedHub

<p align="center">
  <strong>新一代开发者网络加速器</strong><br>
  <em>Accelerate Your Code</em>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-purple?logo=dotnet" alt=".NET 8"/>
  <img src="https://img.shields.io/badge/license-MIT-green" alt="License"/>
  <img src="https://img.shields.io/badge/version-v3.0--alpha-orange" alt="Version"/>
</p>

---

## 功能特性

| 功能 | 描述 |
|------|------|
| **多协议代理** | HTTP/HTTPS/SSH/Git 协议一站式支持 |
| **并行DNS解析** | 多DNS服务器并发查询，速度提升 50%+ |
| **智能缓存** | DNS结果自动缓存，减少重复查询 |
| **IP测速选择** | 自动选择延迟最低的IP地址 |
| **TLS自定义** | 精细化控制SNI和证书验证 |
| **CDN替换** | Google CDN 自动替换为国内镜像 |
| **Web Dashboard** | 实时监控面板，可视化配置管理 |
| **Docker支持** | 一键容器化部署 |
| **跨平台** | Windows / Linux / macOS (含 Apple Silicon) |

## 快速开始

### 下载

前往 [GitHub Releases](https://github.com/Vogadero/SpeedHub/releases) 下载对应平台包：

| 平台 | 文件 |
|:------|:------|
| Windows x64 | `SpeedHub-win-x64.zip` |
| Linux x64 | `SpeedHub-linux-x64.tar.gz` |
| macOS Intel | `SpeedHub-osx-x64.zip` |
| macOS Apple Silicon | `SpeedHub-osx-arm64.zip` |

### 运行

```bash
# Windows — 解压后双击运行
SpeedHub.Cli.exe

# Linux / macOS
tar -xzf SpeedHub-linux-x64.tar.gz
chmod +x SpeedHub.Cli
./SpeedHub.Cli
```

启动后打开 **http://localhost:38458** 查看 Web Dashboard。

### Docker 部署

```bash
docker run -d \
  --name speedhub \
  --network host \
  -v $(pwd)/appsettings.json:/app/appsettings.json \
  ghcr.io/vogadero/speedhub:latest
```

## Web Dashboard

启动后访问 **http://localhost:38458** 即可看到实时监控面板，包含：

- **DNS 解析统计** — 总请求数、成功/失败、缓存命中率、平均耗时
- **域名规则管理** — 当前生效的 TLS SNI、CDN 替换等规则列表
- **实时流量监控** — 各域名的流量带宽，2 秒自动刷新
- **系统状态** — 运行时间、内存占用、连接状态（SignalR 实时推送）

![Dashboard Preview](docs/dashboard-preview.png)

## 配置说明

### 基础配置 (`appsettings.json`)

```json
{
  "SpeedHub": {
    "HttpProxyPort": 38457,       // HTTP代理端口
    "FallbackDns": [              // 备用DNS服务器
      "8.8.8.8:53",
      "119.29.29.29:53"
    ],
    "DnsCacheTtlMinutes": 5,     // DNS缓存时间(分钟)
    "EnableIpSpeedTest": true    // 启用IP测速
  }
}
```

### 域名规则 (`appsettings/` 目录)

每个 JSON 文件对应一组域名规则：

```json
// appsettings/github.json
{
  "DomainConfigs": {
    "github.com": {
      "TlsSni": false            // 不发送SNI
    },
    "*.githubusercontent.com": {
      "TlsIgnoreNameMismatch": true // 忽略证书不匹配
    }
  }
}
```

#### 可用配置项

| 字段 | 类型 | 说明 |
|------|------|------|
| `TlsSni` | bool? | TLS握手是否发送SNI |
| `TlsSniPattern` | string? | SNI模板，支持 @domain @ip @random 变量 |
| `TlsIgnoreNameMismatch` | bool | 忽略证书域名不匹配 |
| `Destination` | Uri? | 请求重定向目标(CDN替换) |
| `Timeout` | TimeSpan? | 请求超时时间 |
| `IPAddress` | IPAddress? | 强制指定请求IP |
| `Response` | object? | 自定义响应(阻断) |

## 架构设计

```
┌─────────────────────────────────────────────────────┐
│                   SpeedHub v3.0                     │
│                                                     │
│  ┌─────────────┐   ┌──────────────┐                │
│  │  Web UI     │   │   CLI        │                │
│  │  (SignalR)  │──▶│ (Spectre)    │                │
│  └─────────────┘   └──────────────┘                │
│         │                                         │
│  ┌──────▼──────┐                                  │
│  │ Core Engine │                                  │
│  ├─────────────┤                                  │
│  │ • DnsResolver│ ← 并行DNS + 缓存 + 测速         │
│  │ • TlsHandler │ ← 自定义TLS/SNI控制             │
│  │ • ProxyEngine│ ← YARP反向代理                  │
│  └─────────────┘                                  │
│                                                     │
│  技术栈: .NET 8 + ASP.NET Core + YARP + SignalR   │
└─────────────────────────────────────────────────────┘
```

## 开发指南

### 环境要求

- .NET 8 SDK
- Git

### 本地开发

```bash
# 克隆项目
git clone https://github.com/Vogadero/SpeedHub.git
cd SpeedHub

# 还原依赖
dotnet restore

# 运行 Web Dashboard
dotnet run --project src/SpeedHub.Web

# 或运行 CLI 版本
dotnet run --project src/SpeedHub.Cli
```

### 项目结构

```
SpeedHub/
├── src/
│   ├── SpeedHub.Core/          # 核心引擎
│   │   ├── DomainResolve/      # DNS解析
│   │   ├── Proxy/              # 代理处理
│   │   ├── Tls/                # TLS处理
│   │   └── Configuration/      # 配置模型
│   ├── SpeedHub.Web/           # Web Dashboard (静态前端 + SignalR)
│   │   ├── Hubs/               # SignalR Hub
│   │   └── wwwroot/            # 前端页面
│   └── SpeedHub.Cli/           # CLI 工具 (Spectre.Console)
├── appsettings/                # 域名规则配置文件
├── .github/workflows/          # CI/CD (多平台构建 + Release 自动发布)
├── docs/                       # 文档
└── tests/                      # 单元测试
```

## 与 FastGithub 的改进对比

| 维度 | FastGithub (v2.1.4) | SpeedHub (v3.0) |
|------|---------------------|-----------------|
| .NET 版本 | 6/7 | **8 LTS** |
| DNS 解析 | **串行**，逐个尝试 | **并行**，同时查询所有服务器 |
| 缓存 | 无 | **内存缓存**，可配置TTL |
| IP 选择 | 固定顺序 | **智能测速**，选最优IP |
| 连接池 | 无 | **SocketsHttpHandler** 池复用 |
| UI | WinForms 桌面 | **Web Dashboard** (SignalR实时更新) |
| API | 无 | **RESTful API** |
| 监控 | 日志文件 | **实时统计面板** |
| CI/CD | 手动发布 | **GitHub Actions** 多平台自动构建 |
| 跨平台 | 仅 Windows | **Windows / Linux / macOS** |
| Docker | 无 | **官方支持** |

## 更新日志

详见 [Releases](https://github.com/Vogadero/SpeedHub/releases) 页面。

## 贡献

欢迎提交 Issue 和 Pull Request！

1. Fork 项目
2. 创建特性分支 (`git checkout -b feature/amazing-feature`)
3. 提交更改 (`git commit -m 'Add amazing feature'`)
4. 推送到分支 (`git push origin feature/amazing-feature`)
5. 打开 Pull Request

## License

MIT License - 查看 [LICENSE](LICENSE) 文件了解详情。

## 致谢

- [FastGithub](https://github.com/dotnetcore/FastGithub) - 原始项目的灵感来源
- [YARP](https://microsoft.github.io/reverse-proxy/) - 微软开源的反向代理框架
- [Spectre.Console](https://spectreconsole.net/) - 终端UI库

---

<div align="center">

**SpeedHub — Accelerate Your Code**

Made by [Vogadero](https://github.com/Vogadero)

</div>
