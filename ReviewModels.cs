using System.Collections.Concurrent;
using System.Text.Json;

internal static class ReviewModelCatalog
{
    private static readonly IReadOnlyDictionary<ReviewAgent, IReadOnlyList<string>> FallbackModels
        = new Dictionary<ReviewAgent, IReadOnlyList<string>>
        {
            [ReviewAgent.Codex] = [CodexReviewSettings.DefaultCodexModel],
            [ReviewAgent.Claude] = ["opus", "sonnet", "haiku", "fable"],
            [ReviewAgent.Kimi] = ["kimi-code/k3"],
            [ReviewAgent.DeepSeek] = ["deepseek-v4-pro", "deepseek-v4-flash"],
            [ReviewAgent.OpenCode] =
            [
                "opencode/muse-spark-1.2-contributor-free",
                "opencode/big-pickle",
                "opencode/hy3-free",
                "opencode/mimo-v2.5-free",
                "opencode/nemotron-3-ultra-free",
                "opencode/nemotron-3.5-lightning-free",
                "opencode/x-preview-f-free",
            ],
        };
    private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<string>>> DiscoveryTasks
        = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<string>> GetModelsAsync(ReviewAgentSettings agent)
    {
        var key = $"{agent.AgentName}|{agent.ResolvedCommand}";
        var discovered = await DiscoveryTasks.GetOrAdd(key, _ => DiscoverAsync(agent));
        return MergeModels(agent.Agent, agent.Model, discovered);
    }

    internal static IReadOnlyList<string> MergeModels(
        ReviewAgent agent,
        string? currentModel,
        IEnumerable<string>? discovered = null)
    {
        var models = new List<string>();
        Add(currentModel);
        foreach (var model in FallbackModels.GetValueOrDefault(agent) ?? [])
        {
            Add(model);
        }

        foreach (var model in discovered ?? [])
        {
            Add(model);
        }

        return models;

        void Add(string? model)
        {
            if (!string.IsNullOrWhiteSpace(model)
                && !models.Contains(model.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                models.Add(model.Trim());
            }
        }
    }

    internal static IReadOnlyList<string> ParseCodexModels(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return models.EnumerateArray()
                .Select(model => model.TryGetProperty("slug", out var slug) ? slug.GetString() : null)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<string> ParseLineModels(string output)
    {
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains('/', StringComparison.Ordinal)
                && !line.Any(char.IsWhiteSpace))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> DiscoverAsync(ReviewAgentSettings agent)
    {
        IReadOnlyList<string> arguments = agent.Agent switch
        {
            ReviewAgent.Codex => ["debug", "models", "--bundled"],
            ReviewAgent.OpenCode => ["models", "opencode"],
            _ => [],
        };
        if (arguments.Count == 0)
        {
            return [];
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                agent.ResolvedCommand,
                arguments,
                AppContext.BaseDirectory,
                input: null,
                timeout: TimeSpan.FromSeconds(30),
                eligibilityCheckInterval: null,
                stillEligible: null,
                CancellationToken.None);
            if (result.ExitCode != 0)
            {
                return [];
            }

            return agent.Agent == ReviewAgent.Codex
                ? ParseCodexModels(result.StandardOutput)
                : ParseLineModels(result.StandardOutput);
        }
        catch
        {
            return [];
        }
    }
}

internal static class ReviewEffortCatalog
{
    private static readonly IReadOnlyDictionary<ReviewAgent, IReadOnlyList<string>> Efforts
        = new Dictionary<ReviewAgent, IReadOnlyList<string>>
        {
            [ReviewAgent.Codex] = ["low", "medium", "high", "xhigh", "max"],
            [ReviewAgent.Claude] = ["low", "medium", "high", "max"],
            [ReviewAgent.Kimi] = [],
            [ReviewAgent.DeepSeek] = ["high", "max"],
            [ReviewAgent.OpenCode] = ["low", "medium", "high", "max"],
        };

    public static bool SupportsEffort(ReviewAgent agent) => Efforts.GetValueOrDefault(agent)?.Count > 0;

    public static IReadOnlyList<string> For(ReviewAgent agent) => Efforts.GetValueOrDefault(agent) ?? [];
}
