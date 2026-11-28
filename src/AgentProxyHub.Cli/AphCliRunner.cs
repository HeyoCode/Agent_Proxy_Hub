using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Cli;

/// <summary>
/// 独立无界面 CLI 控制器 (`aph`)：复用本地守护 REST API 完成状态查询、线路绑定、接管与维护
/// 强制 UseProxy = false 绕过任何外部系统代理，避免访问 127.0.0.1 时误撞 Clash/本地端口
/// </summary>
public static class AphCliRunner
{
    private static readonly JsonSerializerOptions PrettyJsonOpts = new()
    {
        WriteIndented = true
    };

    public static HttpClient CreateDirectLoopbackClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        return new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15)
        };
    }

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        HttpClient? httpClientOverride = null,
        CancellationToken ct = default)
    {
        bool jsonMode = false;
        int port = 17880;
        string? customUrl = null;
        string? fallbackId = null;
        string? relayId = null;
        var pos = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--json") jsonMode = true;
            else if (a == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out int p)) port = p;
            else if (a == "--url" && i + 1 < args.Length) customUrl = args[++i];
            else if (a == "--fallback" && i + 1 < args.Length) fallbackId = args[++i];
            else if (a == "--relay" && i + 1 < args.Length) relayId = args[++i];
            else if (a is "-h" or "--help")
            {
                await stdout.WriteLineAsync(GetHelpText());
                return 0;
            }
            else pos.Add(a);
        }

        if (pos.Count == 0)
        {
            await stdout.WriteLineAsync(GetHelpText());
            return 0;
        }

        string baseUrl = (!string.IsNullOrWhiteSpace(customUrl)
            ? customUrl.Trim()
            : $"http://127.0.0.1:{port}").TrimEnd('/');

        bool ownsClient = httpClientOverride == null;
        using var owned = ownsClient ? CreateDirectLoopbackClient() : null;
        HttpClient http = httpClientOverride ?? owned!;

        string cmd = pos[0].ToLowerInvariant();
        try
        {
            switch (cmd)
            {
                case "status":
                {
                    string raw = await http.GetStringAsync($"{baseUrl}/api/overview", ct);
                    var doc = JsonNode.Parse(raw)!.AsObject();
                    if (jsonMode)
                    {
                        var summary = new JsonObject
                        {
                            ["version"] = doc["project"]?["version"]?.DeepClone(),
                            ["os"] = doc["os"]?.DeepClone(),
                            ["memoryMb"] = doc["memoryMb"]?.DeepClone(),
                            ["lockedNic"] = doc["lockedNic"]?["name"]?.DeepClone(),
                            ["lockedIp"] = doc["lockedNic"]?["iPv4Address"]?.DeepClone(),
                            ["nodesCount"] = doc["nodes"]?.AsArray().Count ?? 0,
                            ["seatsCount"] = doc["seats"]?.AsArray().Count ?? 0,
                            ["activeSeats"] = doc["seats"]?.AsArray().Count(s => s?["enabled"]?.GetValue<bool>() == true) ?? 0
                        };
                        await stdout.WriteLineAsync(summary.ToJsonString(PrettyJsonOpts));
                    }
                    else
                    {
                        string ver = doc["project"]?["version"]?.GetValue<string>() ?? ProjectMetadata.Version;
                        string nic = doc["lockedNic"]?["name"]?.GetValue<string>() ?? "自动扫描中";
                        string nicIp = doc["lockedNic"]?["iPv4Address"]?.GetValue<string>() ?? "-";
                        int active = doc["seats"]?.AsArray().Count(s => s?["enabled"]?.GetValue<bool>() == true) ?? 0;
                        int total = doc["seats"]?.AsArray().Count ?? 0;
                        await stdout.WriteLineAsync($"Agent Proxy Hub v{ver} | 物理网卡: {nic} ({nicIp}) | 活跃席位: {active}/{total}");
                    }
                    return 0;
                }

                case "seats":
                {
                    string raw = await http.GetStringAsync($"{baseUrl}/api/overview", ct);
                    var doc = JsonNode.Parse(raw)!.AsObject();
                    var seats = doc["seats"]?.AsArray() ?? new JsonArray();
                    if (jsonMode)
                    {
                        await stdout.WriteLineAsync(seats.ToJsonString(PrettyJsonOpts));
                    }
                    else
                    {
                        foreach (var s in seats)
                        {
                            if (s == null) continue;
                            string id = s["id"]?.GetValue<string>() ?? "";
                            int localPort = s["localPort"]?.GetValue<int>() ?? 0;
                            bool on = s["enabled"]?.GetValue<bool>() == true;
                            string bound = s["boundNodeId"]?.GetValue<string>() ?? "-";
                            string fb = s["fallbackNodeId"]?.GetValue<string>() ?? "";
                            await stdout.WriteLineAsync($"{id,-18} :{localPort}  [{(on ? "ON " : "OFF")}]  bound={bound}{(string.IsNullOrEmpty(fb) ? "" : $"  fallback={fb}")}");
                        }
                    }
                    return 0;
                }

                case "nodes":
                {
                    string raw = await http.GetStringAsync($"{baseUrl}/api/nodes", ct);
                    if (jsonMode)
                    {
                        await stdout.WriteLineAsync(FormatJson(raw));
                    }
                    else
                    {
                        var arr = JsonNode.Parse(raw)?.AsArray() ?? new JsonArray();
                        foreach (var n in arr)
                        {
                            if (n == null) continue;
                            await stdout.WriteLineAsync($"{n["id"]} | {n["name"]} ({n["protocol"]}) | {n["status"]} | {n["exitIp"]}");
                        }
                    }
                    return 0;
                }

                case "verify":
                {
                    string target = pos.Count >= 2 ? pos[1] : "--all";
                    string endpoint = target == "--all"
                        ? $"{baseUrl}/api/nodes/verify-all"
                        : $"{baseUrl}/api/nodes/{Uri.EscapeDataString(target)}/verify";
                    using var resp = await http.PostAsync(endpoint, new StringContent("{}", Encoding.UTF8, "application/json"), ct);
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        await stderr.WriteLineAsync($"验真失败 HTTP {(int)resp.StatusCode}: {body}");
                        return 1;
                    }
                    await stdout.WriteLineAsync(jsonMode ? FormatJson(body) : $"验真完成: {body}");
                    return 0;
                }

                case "bind":
                {
                    if (pos.Count < 3)
                    {
                        await stderr.WriteLineAsync("用法: aph bind <seatId> <nodeId> [--fallback <nodeId>] [--relay <nodeId>] [--json]");
                        return 2;
                    }
                    var payload = new JsonObject
                    {
                        ["boundNodeId"] = pos[2]
                    };
                    if (fallbackId != null) payload["fallbackNodeId"] = fallbackId;
                    if (relayId != null) payload["relayNodeId"] = relayId;

                    using var resp = await http.PutAsync(
                        $"{baseUrl}/api/seats/{Uri.EscapeDataString(pos[1])}",
                        new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                        ct);
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        await stderr.WriteLineAsync($"绑定失败 HTTP {(int)resp.StatusCode}: {body}");
                        return 1;
                    }
                    await stdout.WriteLineAsync(jsonMode ? FormatJson(body) : $"已更新 {pos[1]} -> {pos[2]}");
                    return 0;
                }

                case "takeover":
                {
                    if (pos.Count < 3 || pos[2] is not ("on" or "off"))
                    {
                        await stderr.WriteLineAsync("用法: aph takeover <seatId> <on|off> [--json]");
                        return 2;
                    }
                    var payload = new JsonObject { ["enabled"] = pos[2] == "on" };
                    using var resp = await http.PostAsync(
                        $"{baseUrl}/api/seats/{Uri.EscapeDataString(pos[1])}/takeover",
                        new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                        ct);
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        await stderr.WriteLineAsync($"接管失败 HTTP {(int)resp.StatusCode}: {body}");
                        return 1;
                    }
                    await stdout.WriteLineAsync(jsonMode ? FormatJson(body) : body);
                    return 0;
                }

                case "launch":
                {
                    if (pos.Count < 2)
                    {
                        await stderr.WriteLineAsync("用法: aph launch <seatId> [--json]");
                        return 2;
                    }
                    using var resp = await http.PostAsync(
                        $"{baseUrl}/api/seats/{Uri.EscapeDataString(pos[1])}/launch",
                        new StringContent("{}", Encoding.UTF8, "application/json"),
                        ct);
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        await stderr.WriteLineAsync($"启动失败 HTTP {(int)resp.StatusCode}: {body}");
                        return 1;
                    }
                    await stdout.WriteLineAsync(jsonMode ? FormatJson(body) : body);
                    return 0;
                }

                case "cleanup":
                {
                    using var resp = await http.PostAsync(
                        $"{baseUrl}/api/maintenance/cleanup",
                        new StringContent("{}", Encoding.UTF8, "application/json"),
                        ct);
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        await stderr.WriteLineAsync($"清理失败 HTTP {(int)resp.StatusCode}: {body}");
                        return 1;
                    }
                    await stdout.WriteLineAsync(jsonMode ? FormatJson(body) : body);
                    return 0;
                }

                default:
                    await stderr.WriteLineAsync($"未知命令: {cmd}");
                    await stdout.WriteLineAsync(GetHelpText());
                    return 2;
            }
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"连接本地网关 ({baseUrl}) 失败：{ex.Message}");
            return 1;
        }
    }

    private static string FormatJson(string raw)
    {
        try
        {
            return JsonNode.Parse(raw)?.ToJsonString(PrettyJsonOpts) ?? raw;
        }
        catch
        {
            return raw;
        }
    }

    public static string GetHelpText() =>
        """
        Agent Proxy Hub CLI (aph) v0.8.0 — 本地网关自动化与脚本控制工具
        用法:
          aph status [--json] [--port 17880]
          aph seats [--json]
          aph nodes [--json]
          aph verify [<nodeId>|--all] [--json]
          aph bind <seatId> <nodeId> [--fallback <nodeId>] [--relay <nodeId>] [--json]
          aph takeover <seatId> <on|off> [--json]
          aph launch <seatId> [--json]
          aph cleanup [--json]
        """;
}
