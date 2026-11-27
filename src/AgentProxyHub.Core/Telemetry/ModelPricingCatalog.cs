using AgentProxyHub.Core.Models;

namespace AgentProxyHub.Core.Telemetry;

/// <summary>
/// 主流大模型百万 Token 单价目录（支持内置前缀匹配与用户自定义定价覆盖）
/// </summary>
public sealed class ModelPricingCatalog
{
    private static readonly List<ModelPricingRule> BuiltInRules =
    [
        new() { Pattern = "claude-opus-4-5", DisplayName = "Claude Opus 4.5", InputPerMillion = 15.0, OutputPerMillion = 75.0, CacheReadPerMillion = 1.50, CacheWritePerMillion = 18.75 },
        new() { Pattern = "claude-sonnet-4-5", DisplayName = "Claude Sonnet 4.5", InputPerMillion = 3.0, OutputPerMillion = 15.0, CacheReadPerMillion = 0.30, CacheWritePerMillion = 3.75 },
        new() { Pattern = "claude-opus-4", DisplayName = "Claude Opus 4.x", InputPerMillion = 15.0, OutputPerMillion = 75.0, CacheReadPerMillion = 1.50, CacheWritePerMillion = 18.75 },
        new() { Pattern = "claude-sonnet-4", DisplayName = "Claude Sonnet 4.x", InputPerMillion = 3.0, OutputPerMillion = 15.0, CacheReadPerMillion = 0.30, CacheWritePerMillion = 3.75 },
        new() { Pattern = "claude-3-7-sonnet", DisplayName = "Claude 3.7 Sonnet", InputPerMillion = 3.0, OutputPerMillion = 15.0, CacheReadPerMillion = 0.30, CacheWritePerMillion = 3.75 },
        new() { Pattern = "claude-3-5-sonnet", DisplayName = "Claude 3.5 Sonnet", InputPerMillion = 3.0, OutputPerMillion = 15.0, CacheReadPerMillion = 0.30, CacheWritePerMillion = 3.75 },
        new() { Pattern = "claude-haiku", DisplayName = "Claude Haiku", InputPerMillion = 0.80, OutputPerMillion = 4.0, CacheReadPerMillion = 0.08, CacheWritePerMillion = 1.00 },
        new() { Pattern = "gpt-5", DisplayName = "OpenAI GPT-5 / Codex", InputPerMillion = 1.25, OutputPerMillion = 10.0, CacheReadPerMillion = 0.125, CacheWritePerMillion = 0.0 },
        new() { Pattern = "gpt-4.1-mini", DisplayName = "GPT-4.1 Mini", InputPerMillion = 0.40, OutputPerMillion = 1.60, CacheReadPerMillion = 0.10, CacheWritePerMillion = 0.0 },
        new() { Pattern = "gpt-4.1", DisplayName = "GPT-4.1", InputPerMillion = 2.00, OutputPerMillion = 8.00, CacheReadPerMillion = 0.50, CacheWritePerMillion = 0.0 },
        new() { Pattern = "gpt-4o-mini", DisplayName = "GPT-4o Mini", InputPerMillion = 0.15, OutputPerMillion = 0.60, CacheReadPerMillion = 0.075, CacheWritePerMillion = 0.0 },
        new() { Pattern = "gpt-4o", DisplayName = "GPT-4o", InputPerMillion = 2.50, OutputPerMillion = 10.00, CacheReadPerMillion = 1.25, CacheWritePerMillion = 0.0 },
        new() { Pattern = "o3-mini", DisplayName = "OpenAI o3-mini", InputPerMillion = 1.10, OutputPerMillion = 4.40, CacheReadPerMillion = 0.55, CacheWritePerMillion = 0.0 },
        new() { Pattern = "o3", DisplayName = "OpenAI o3", InputPerMillion = 2.00, OutputPerMillion = 8.00, CacheReadPerMillion = 0.50, CacheWritePerMillion = 0.0 },
        new() { Pattern = "o4-mini", DisplayName = "OpenAI o4-mini", InputPerMillion = 1.10, OutputPerMillion = 4.40, CacheReadPerMillion = 0.275, CacheWritePerMillion = 0.0 },
        new() { Pattern = "gemini-4-carbon", DisplayName = "Gemini 4 Carbon", InputPerMillion = 1.25, OutputPerMillion = 10.00, CacheReadPerMillion = 0.31, CacheWritePerMillion = 0.0 },
        new() { Pattern = "gemini-4-neon", DisplayName = "Gemini 4 Neon", InputPerMillion = 1.25, OutputPerMillion = 10.00, CacheReadPerMillion = 0.31, CacheWritePerMillion = 0.0 },
        new() { Pattern = "gemini-4", DisplayName = "Gemini 4.x", InputPerMillion = 0.50, OutputPerMillion = 3.00, CacheReadPerMillion = 0.125, CacheWritePerMillion = 0.0 },
        new() { Pattern = "gemini-3-5", DisplayName = "Gemini 3.5", InputPerMillion = 1.25, OutputPerMillion = 10.00, CacheReadPerMillion = 0.31, CacheWritePerMillion = 0.0 },
        new() { Pattern = "gemini-2.5-pro", DisplayName = "Gemini 2.5 Pro", InputPerMillion = 1.25, OutputPerMillion = 10.00, CacheReadPerMillion = 0.31, CacheWritePerMillion = 0.0 },
        new() { Pattern = "gemini-2.5-flash", DisplayName = "Gemini 2.5 Flash", InputPerMillion = 0.15, OutputPerMillion = 0.60, CacheReadPerMillion = 0.0375, CacheWritePerMillion = 0.0 },
        new() { Pattern = "deepseek-reasoner", DisplayName = "DeepSeek R1", InputPerMillion = 0.55, OutputPerMillion = 2.19, CacheReadPerMillion = 0.14, CacheWritePerMillion = 0.0 },
        new() { Pattern = "deepseek-chat", DisplayName = "DeepSeek Chat", InputPerMillion = 0.27, OutputPerMillion = 1.10, CacheReadPerMillion = 0.07, CacheWritePerMillion = 0.0 }
    ];

    private readonly object _lock = new();
    private List<ModelPricingRule> _customRules = new();

    public void SetCustomRules(IEnumerable<ModelPricingRule> customRules)
    {
        lock (_lock)
        {
            _customRules = customRules
                .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
                .Select(r => new ModelPricingRule
                {
                    Pattern = r.Pattern.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? r.Pattern.Trim() : r.DisplayName.Trim(),
                    InputPerMillion = r.InputPerMillion,
                    OutputPerMillion = r.OutputPerMillion,
                    CacheReadPerMillion = r.CacheReadPerMillion,
                    CacheWritePerMillion = r.CacheWritePerMillion,
                    IsCustom = true
                })
                .ToList();
        }
    }

    public List<ModelPricingRule> GetAllRules()
    {
        lock (_lock)
        {
            var merged = new List<ModelPricingRule>(_customRules);
            foreach (var b in BuiltInRules)
            {
                if (!merged.Any(c => c.Pattern.Equals(b.Pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    merged.Add(b);
                }
            }
            return merged;
        }
    }

    public ModelPricingRule? MatchRule(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        string normalized = modelName.Trim().ToLowerInvariant();

        lock (_lock)
        {
            // 优先精确匹配自定义规则，其次前缀/包含匹配自定义，最后匹配内置规则
            var customExact = _customRules.FirstOrDefault(r => normalized == r.Pattern.ToLowerInvariant());
            if (customExact != null) return customExact;

            var customPrefix = _customRules
                .OrderByDescending(r => r.Pattern.Length)
                .FirstOrDefault(r => normalized.Contains(r.Pattern.ToLowerInvariant(), StringComparison.Ordinal));
            if (customPrefix != null) return customPrefix;
        }

        return BuiltInRules
            .OrderByDescending(r => r.Pattern.Length)
            .FirstOrDefault(r => normalized.Contains(r.Pattern.ToLowerInvariant(), StringComparison.Ordinal));
    }

    public (double? CostUsd, bool IsPriced) CalculateCost(UsageLogEntry entry)
    {
        var (cost, _, isPriced) = CalculateCostAndSavings(entry);
        return (cost, isPriced);
    }

    public (double? CostUsd, double CacheSavedUsd, bool IsPriced) CalculateCostAndSavings(UsageLogEntry entry)
    {
        var rule = MatchRule(entry.Model);
        if (rule == null) return (null, 0.0, false);

        double cost =
            (entry.EffectiveInputTokens * rule.InputPerMillion
             + entry.OutputTokens * rule.OutputPerMillion
             + entry.CacheReadTokens * rule.CacheReadPerMillion
             + entry.CacheWriteTokens * rule.CacheWritePerMillion) / 1_000_000.0;

        double saved = entry.CacheReadTokens * Math.Max(0.0, rule.InputPerMillion - rule.CacheReadPerMillion) / 1_000_000.0;
        return (cost, saved, true);
    }
}
