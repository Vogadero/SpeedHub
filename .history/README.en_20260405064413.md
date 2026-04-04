<p align="center">
  En | <a href="./README.md">中文</a>
</p>

<h2 align="center">SpeedHub</h2>
<p align="center">
  <strong>Next-Gen Developer Network Accelerator 🚀</strong><br>
  <em>Accelerate Your Code — Blazing-fast access to GitHub, Google Fonts & more</em>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-purple?logo=dotnet" alt=".NET 8"/>
  <img src="https://img.shields.io/badge/license-MIT-green" alt="License"/>
  <img src="https://img.shields.io/badge/version-v3.0-orange" alt="Version"/>
</p>

---

![Dashboard Preview](docs/SpeedHub.Cli.png)

## What is SpeedHub?

**SpeedHub is a local network proxy accelerator** designed to solve the problem of slow and unstable access to overseas services (GitHub, Google Fonts, npm, etc.) that developers face every day.

### How It Works

```
Your Computer (Browser / Git / IDE)
        │
        ▼
   ┌──────────┐
   │ SpeedHub │ ◄── Runs locally on port :38457
   │  Engine  │     Intercepts & accelerates traffic
   └────┬─────┘
        │ Parallel DNS · CDN Replacement · TLS Optimization
        ▼
   Target Servers (github.com, googleapis.com ...)
```

**In short: point your browser or Git proxy to SpeedHub (port `:38457`), and all proxied requests get intelligently optimized and accelerated.**

### What Problems Does It Solve?

| Pain Point | SpeedHub's Solution |
|------------|---------------------|
| GitHub clone/download at a few KB/s | **Parallel DNS resolution** + **smart IP speed testing** — auto-routes to fastest path |
| Google Fonts / AJAX timeouts | **CDN replacement** — auto-replaces Google resources with available mirrors |
| `git push` frequently disconnects | **Connection pooling** + **custom TLS/SNI control** for stable handshakes |
| DNS pollution causing resolution failures | **Multi-server concurrent DNS queries** — bypasses single-point failures |
| No visibility into what's happening | **Web Dashboard real-time panel** — all stats at a glance |

### Typical Use Cases

- :globe_with_meridians: **Browser acceleration** — Set browser proxy to `127.0.0.1:38457` for faster GitHub / StackOverflow access
- :wrench: **Git acceleration** — `git config --global http.proxy http://127.0.0.1:38457`, noticeably faster clone/push
- :package: **Dev tools acceleration** — VS Code, IDEA etc. access plugin marketplaces and download dependencies through proxy
- :whale: **Docker deployment** — One-click containerized deployment on servers, shared team acceleration

> :bulb: **No global VPN needed!** SpeedHub only activates for configured domain rules — other traffic is unaffected.

---

## Features

| Feature | Description |
|---------|-------------|
| **Multi-Protocol Proxy** | HTTP/HTTPS/SSH/Git all-in-one support |
| **Parallel DNS Resolution** | Concurrent queries to multiple DNS servers, 50%+ speed improvement |
| **Smart Caching** | Automatic DNS result caching with configurable TTL |
| **IP Speed Testing** | Auto-selects the lowest-latency IP address |
| **Custom TLS** | Fine-grained SNI and certificate verification control |
| **CDN Replacement** | Auto-replaces Google CDN with domestic mirrors |
| **Web Dashboard** | Real-time monitoring panel with visual config management (:38458) |
| **Docker Support** | One-click containerized deployment |
| **Cross-Platform** | Windows / Linux / macOS (including Apple Silicon) |

## Quick Start

### Download

Get the latest release from [GitHub Releases](https://github.com/Vogadero/SpeedHub/releases):

| Platform | File |
|:---------|:-----|
| Windows x64 | `SpeedHub-win-x64.zip` |
| Linux x64 | `SpeedHub-linux-x64.tar.gz` |
| macOS Intel | `SpeedHub-osx-x64.zip` |
| macOS Apple Silicon | `SpeedHub-osx-arm64.zip` |

### Run

```bash
# Windows — extract and double-click
SpeedHub.Cli.exe

# Linux / macOS
tar -xzf SpeedHub-linux-x64.tar.gz
chmod +x SpeedHub.Cli
./SpeedHub.Cli
```

After startup:
- :globe_with_meridians: Open **http://localhost:38458** for the Web Dashboard (real-time monitoring)
- :zap: Set your proxy to **`127.0.0.1:38457`** to activate acceleration

### Docker Deployment

```bash
docker run -d \
  --name speedhub \
  --network host \
  -v $(pwd)/appsettings.json:/app/appsettings.json \
  ghcr.io/vogadero/speedhub:latest
```

## Web Dashboard

Visit **http://localhost:38458** after launch for the real-time monitoring dashboard:

- **DNS Resolution Stats** — Total requests, success/fail, cache hit rate, average latency
- **Proxy Traffic Stats** — Real upstream/downstream traffic statistics (not simulated data), reflecting actual network usage
- **Domain Rules Management** — List of active TLS SNI, CDN replacement rules
- **Live Traffic Monitor** — Per-domain bandwidth usage, auto-refreshes every 2s
- **System Status** — Uptime, memory usage, connection status (SignalR real-time push)
- **Theme Toggle** — Click the theme button in the top-right corner to switch between light and dark modes

![Dashboard Preview](docs/dashboard-preview.png)

## Configuration

### Basic Config (`appsettings.json`)

```json
{
  "SpeedHub": {
    "HttpProxyPort": 38457,       // HTTP proxy port
    "FallbackDns": [              // Fallback DNS servers
      "8.8.8.8:53",
      "119.29.29.29:53"
    ],
    "DnsCacheTtlMinutes": 5,     // DNS cache TTL (minutes)
    "EnableIpSpeedTest": true    // Enable IP speed testing
  }
}
```

### Domain Rules (`appsettings/` directory)

Each JSON file contains domain-specific rules:

```json
// appsettings/github.json
{
  "DomainConfigs": {
    "github.com": {
      "TlsSni": false            // Don't send SNI
    },
    "*.githubusercontent.com": {
      "TlsIgnoreNameMismatch": true // Ignore cert name mismatch
    }
  }
}
```

#### Available Config Options

| Field | Type | Description |
|-------|------|-------------|
| `TlsSni` | bool? | Whether to send SNI during TLS handshake |
| `TlsSniPattern` | string? | SNI template, supports @domain @ip @random variables |
| `TlsIgnoreNameMismatch` | bool | Ignore certificate name mismatch |
| `Destination` | Uri? | Request redirect target (CDN replacement) |
| `Timeout` | TimeSpan? | Request timeout duration |
| `IPAddress` | IPAddress? | Force specific request IP |
| `Response` | object? | Custom response (blocking) |

## Architecture

```
┌────────────────────────────────────────────────────┐
│                   SpeedHub v3.0                    │
│                                                    │
│  ┌─────────────┐   ┌──────────────┐                │
│  │  Web UI     │   │   CLI        │                │
│  │  (SignalR)  │──▶│ (Spectre)    │                │
│  └─────────────┘   └──────────────┘                │
│         │                                          │
│  ┌──────▼───────┐                                  │
│  │ Core Engine  │                                  │
│  ├──────────────┤                                  │
│  │ • DnsResolver│ ← Parallel DNS + Cache + Test    │
│  │ • TlsHandler │ ← Custom TLS/SNI Control         │
│  │ • ProxyEngine│ ← YARP Reverse Proxy             │
│  └──────────────┘                                  │
│                                                    │
│  Tech Stack: .NET 8 + ASP.NET Core + YARP + SignalR│
└────────────────────────────────────────────────────┘
```

## Development Guide

### Prerequisites

- .NET 8 SDK
- Git

### Local Development

```bash
# Clone the repo
git clone https://github.com/Vogadero/SpeedHub.git
cd SpeedHub

# Restore dependencies
dotnet restore

# Run Web Dashboard
dotnet run --project src/SpeedHub.Web

# Or run CLI version
dotnet run --project src/SpeedHub.Cli
```

### Project Structure

```
SpeedHub/
├── src/
│   ├── SpeedHub.Core/          # Core engine
│   │   ├── DomainResolve/      # DNS resolution
│   │   ├── Proxy/              # Proxy handling
│   │   ├── Tls/                # TLS handling
│   │   └── Configuration/      # Config models
│   ├── SpeedHub.Web/           # Web Dashboard (static frontend + SignalR)
│   │   ├── Hubs/               # SignalR Hub
│   │   └── wwwroot/            # Frontend pages
│   └── SpeedHub.Cli/           # CLI tool (Spectre.Console)
├── appsettings/                # Domain rule config files
├── .github/workflows/          # CI/CD (multi-platform build + auto-release)
├── docs/                       # Documentation
└── tests/                      # Unit tests
```

## Comparison with FastGithub

| Aspect | FastGithub (v2.1.4) | SpeedHub (v3.0) |
|--------|---------------------|-----------------|
| .NET Version | 6/7 | **8 LTS** |
| DNS Resolution | **Serial**, tries one by one | **Parallel**, queries all simultaneously |
| Caching | None | **In-memory cache** with configurable TTL |
| IP Selection | Fixed order | **Smart speed test**, picks best IP |
| Connection Pool | None | **SocketsHttpHandler** pool reuse |
| UI | WinForms Desktop | **Web Dashboard** (SignalR real-time) |
| API | None | **RESTful API** |
| Monitoring | Log files | **Real-time statistics panel** |
| CI/CD | Manual release | **GitHub Actions** multi-platform auto-build |
| Cross-Platform | Windows only | **Windows / Linux / macOS** |
| Docker | None | **Official support** |

## Changelog

See [Releases](https://github.com/Vogadero/SpeedHub/releases) page for details.

## Contributing

Issues and Pull Requests are welcome!

1. Fork the project
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit changes (`git commit -m 'Add amazing feature'`)
4. Push to branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

MIT License — see [LICENSE](LICENSE) file for details.

## Acknowledgments

- [FastGithub](https://github.com/dotnetcore/FastGithub) — Original project inspiration
- [YARP](https://microsoft.github.io/reverse-proxy/) — Microsoft's open-source reverse proxy framework
- [Spectre.Console](https://spectreconsole.net/) — Terminal UI library

---

<div align="center">

**SpeedHub — Accelerate Your Code**

Made by [Vogadero](https://github.com/Vogadero) &nbsp;|&nbsp;
<a href="./README.md">🇨🇳 中文</a>

</div>
