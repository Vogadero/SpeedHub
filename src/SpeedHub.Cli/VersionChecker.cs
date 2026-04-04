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
        
        /// <summary>
        /// 获取当前程序版本（从程序集版本读取）
        /// </summary>
        private static string CurrentVersion => 
            $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "3.0.0"}";

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

                // 提取主版本号 (major.minor.patch)
                var latestParts = ParseVersion(latest);
                var currentParts = ParseVersion(current);

                // 比较 major.minor.patch
                for (int i = 0; i < 3; i++)
                {
                    if (latestParts[i] > currentParts[i]) return true;
                    if (latestParts[i] < currentParts[i]) return false;
                }

                // 主版本号相同，比较 pre-release 标签 (如 alpha.30 vs alpha.31)
                var latestPreRelease = ExtractPreRelease(latest);
                var currentPreRelease = ExtractPreRelease(current);

                if (!string.IsNullOrEmpty(latestPreRelease) && !string.IsNullOrEmpty(currentPreRelease))
                {
                    // 提取 pre-release 中的数字部分进行比较
                    var latestNum = ExtractPreReleaseNumber(latestPreRelease);
                    var currentNum = ExtractPreReleaseNumber(currentPreRelease);

                    if (latestNum > currentNum) return true;
                }
                else if (!string.IsNullOrEmpty(latestPreRelease) && string.IsNullOrEmpty(currentPreRelease))
                {
                    // latest 是 pre-release，current 是正式版，不认为更新
                    return false;
                }
                else if (string.IsNullOrEmpty(latestPreRelease) && !string.IsNullOrEmpty(currentPreRelease))
                {
                    // latest 是正式版，current 是 pre-release，认为更新
                    return true;
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
        /// 提取 pre-release 标签 (如 "alpha.30" 从 "3.0.0-alpha.30")
        /// </summary>
        private static string ExtractPreRelease(string version)
        {
            var dashIndex = version.IndexOf('-');
            if (dashIndex > 0 && dashIndex < version.Length - 1)
            {
                return version.Substring(dashIndex + 1);
            }
            return "";
        }

        /// <summary>
        /// 从 pre-release 标签提取数字 (如从 "alpha.30" 提取 30)
        /// </summary>
        private static int ExtractPreReleaseNumber(string preRelease)
        {
            var lastDot = preRelease.LastIndexOf('.');
            if (lastDot > 0 && lastDot < preRelease.Length - 1)
            {
                var numStr = preRelease.Substring(lastDot + 1);
                if (int.TryParse(numStr, out int num))
                {
                    return num;
                }
            }
            return 0;
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
