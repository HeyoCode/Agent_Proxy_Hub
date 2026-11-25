using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Apps;

/// <summary>
/// AI 软件独立分流席位与 CLI 隔离环境配置生成器
/// </summary>
public static class AppSeatManager
{
    public static Dictionary<string, string> BuildCliSnippets(AppSeat? primaryClaudeSeat, AppSeat? agySeat)
    {
        int claudePort = primaryClaudeSeat?.LocalPort ?? 17891;
        int agyPort = agySeat?.LocalPort ?? 17893;

        return new Dictionary<string, string>
        {
            ["ps"] = $"""
                     # Windows PowerShell 独立 AI 专线注入（不改系统代理，绕过 Clash）
                     $env:HTTPS_PROXY="http://127.0.0.1:{claudePort}"
                     $env:HTTP_PROXY="http://127.0.0.1:{claudePort}"
                     $env:ALL_PROXY="socks5h://127.0.0.1:{claudePort}"
                     $env:NO_PROXY="localhost,127.0.0.1,192.168.0.0/16,10.0.0.0/8"
                     claude
                     """,
            ["zsh"] = $"""
                      # macOS / Zsh 独立 AI 专线注入（不影响系统与 Clash/Surge）
                      export HTTPS_PROXY="http://127.0.0.1:{claudePort}"
                      export HTTP_PROXY="http://127.0.0.1:{claudePort}"
                      export ALL_PROXY="socks5h://127.0.0.1:{claudePort}"
                      export NO_PROXY="localhost,127.0.0.1,.local"
                      claude
                      """,
            ["claude"] = $$"""
                         // ~/.claude/settings.json（独立锁定 Claude Code 出口）
                         {
                           "env": {
                             "HTTPS_PROXY": "http://127.0.0.1:{{claudePort}}",
                             "HTTP_PROXY": "http://127.0.0.1:{{claudePort}}",
                             "NO_PROXY": "localhost,127.0.0.1"
                           }
                         }
                         """,
            ["agy"] = $"""
                      # Antigravity 独立端口隔离参数（不经过 Clash 7897）
                      Antigravity --proxy-server="socks5://127.0.0.1:{agyPort}"
                      # 或在 IDE Proxy 设置中填入：
                      # http://127.0.0.1:{agyPort}
                      """
        };
    }
}
