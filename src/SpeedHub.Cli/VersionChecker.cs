using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace SpeedHub.Cli
{
    /// <summary>
    /// GitHub Release 版本检查器
    /// 检查是否有新版本可用，并提示用户更新
    /// </summary>
    public static class VersionChecker
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/Vogadero/SpeedHub/releases/latest";
        private const string ReleasesPageUrl = "https://github.com/Vogadero/SpeedHub/releases";
        private const string CurrentVersion = "v3.0-alpha.1";

        /// <summary>
        /// 异步检查最新版本，如果有更新则提示用户
        /// </summary>
        public static async Task CheckForUpdatesAsync()
        {
            try
            {
                using var client = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(5)
                };

                // GitHub API 要求设置 User-Agent
                client.DefaultRequestHeaders.Add("User-Agent", "SpeedHub-Version-Checker");

                var response = await client.GetFromJsonAsync<GitHubRelease>(GitHubApiUrl);
                if (response == null || string.IsNullOrEmpty(response.TagName))
                {
                    return;
                }

                var latestVersion = response.TagName;

                // 比较版本号
                if (IsNewerVersion(latestVersion, CurrentVersion))
                {
                    AnsiConsole.MarkupLine($"\n[yellow bold]🔔 发现新版本![/]\n");
                    AnsiConsole.MarkupLine($"  当前版本: [dim]{CurrentVersion}[/]");
                    AnsiConsole.MarkupLine($"  最新版本: [green bold]{latestVersion}[/]\n");
                    AnsiConsole.MarkupLine($"  更新内容:");
                    
                    // 显示 Release Notes（前 3 行）
                    if (!string.IsNullOrEmpty(response.Body))
                    {
                        var lines = response.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        var showLines = Math.Min(3, lines.Length);
                        for (int i = 0; i < showLines; i++)
                        {
                            var line = lines[i].Trim();
                            if (line.StartsWith("#") || line.StartsWith("-"))
                            {
                                AnsiConsole.MarkupLine($"    [dim]{line}[/]");
                            }
                        }
                    }

                    AnsiConsole.MarkupLine($"\n  前往下载: [blue underline]{ReleasesPageUrl}[/]\n");

                    // 询问是否打开浏览器
                    if (AnsiConsole.Confirm("是否现在打开下载页面？", defaultValue: true))
                    {
                        Program.OpenBrowser(ReleasesPageUrl);
                    }
                }
            }
            catch
            {
                // 忽略版本检查失败（可能是网络问题）
            }
        }

        /// <summary>
        /// 比较版本号，判断 latest 是否比 current 更新
        /// </summary>
        private static bool IsNewerVersion(string latest, string current)
        {
            try
            {
                // 移除 'v' 前缀
                latest = latest.TrimStart('v');
                current = current.TrimStart('v');

                // 解析语义化版本号 (major.minor.patch)
                var latestParts = ParseVersion(latest);
                var currentParts = ParseVersion(current);

                for (int i = 0; i < 3; i++)
                {
                    if (latestParts[i] > currentParts[i]) return true;
                    if (latestParts[i] < currentParts[i]) return false;
                }

                return false; // 版本相同
            }
            catch
            {
                // 解析失败时保守返回 false
                return false;
            }
        }

        /// <summary>
        /// 解析版本号为 [major, minor, patch] 数组
        /// </summary>
        private static int[] ParseVersion(string version)
        {
            // 提取数字部分（忽略 pre-release 标签如 -alpha, -beta）
            var match = Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)");
            if (match.Success)
            {
                return new int[]
                {
                    int.Parse(match.Groups[1].Value),
                    int.Parse(match.Groups[2].Value),
                    int.Parse(match.Groups[3].Value)
                };
            }

            // 无法解析时返回 [0, 0, 0]
            return new int[] { 0, 0, 0 };
        }

        /// <summary>
        /// GitHub Release API 响应模型
        /// </summary>
        private class GitHubRelease
        {
            public string TagName { get; set; } = "";
            public string Name { get; set; } = "";
            public string Body { get; set; } = "";
            public string HtmlUrl { get; set; } = "";
            public DateTime PublishedAt { get; set; }
        }
    }
}
