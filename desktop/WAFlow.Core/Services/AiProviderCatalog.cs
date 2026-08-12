namespace WAFlow.Core.Services;

public enum AiProviderProtocol
{
    OpenAiCompatible,
    AnthropicMessages
}

public sealed record AiProviderDefinition(
    string Id,
    string DisplayName,
    string DefaultBaseUrl,
    string Description,
    IReadOnlyList<string> ExampleModels,
    AiProviderProtocol Protocol = AiProviderProtocol.OpenAiCompatible);

public static class AiProviderCatalog
{
    public const string DefaultProviderId = "custom";

    public static readonly IReadOnlyList<AiProviderDefinition> Supported =
    [
        new(DefaultProviderId, "自定义 OpenAI 兼容接口", "", "可填写任意 HTTPS OpenAI Chat Completions 兼容 Base URL", []),
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "DeepSeek 官方 OpenAI 兼容接口", ["deepseek-v4-flash", "deepseek-v4-pro"]),
        new("openai", "OpenAI", "https://api.openai.com/v1", "OpenAI 官方 API", ["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"]),
        new("anthropic", "Anthropic Claude", "https://api.anthropic.com/v1", "Claude 原生 Messages API", ["claude-sonnet-4-6", "claude-opus-4-6"], AiProviderProtocol.AnthropicMessages),
        new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "Gemini OpenAI 兼容接口", ["gemini-2.5-pro", "gemini-2.5-flash"]),
        new("xai", "xAI Grok", "https://api.x.ai/v1", "xAI 官方 OpenAI 兼容接口", ["grok-4.5"]),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "聚合多家模型的 OpenAI 兼容接口", ["openai/gpt-5", "anthropic/claude-sonnet-4"]),
        new("groq", "Groq", "https://api.groq.com/openai/v1", "Groq 高速推理接口", ["llama-3.3-70b-versatile"]),
        new("together", "Together AI", "https://api.together.xyz/v1", "Together AI 开源模型平台", ["meta-llama/Llama-3.3-70B-Instruct-Turbo"]),
        new("mistral", "Mistral AI", "https://api.mistral.ai/v1", "Mistral 官方 API", ["mistral-large-latest"]),
        new("qwen", "阿里云百炼 / Qwen", "https://dashscope.aliyuncs.com/compatible-mode/v1", "DashScope OpenAI 兼容接口", ["qwen-max", "qwen-plus"]),
        new("moonshot", "Moonshot / Kimi", "https://api.moonshot.cn/v1", "Moonshot OpenAI 兼容接口", ["kimi-k2-0711-preview"]),
        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "智谱 OpenAI 兼容接口", ["glm-4.5"]),
        new("siliconflow", "SiliconFlow", "https://api.siliconflow.cn/v1", "硅基流动 OpenAI 兼容接口", ["deepseek-ai/DeepSeek-V3"])
    ];

    public static AiProviderDefinition Resolve(string? id) =>
        Supported.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? Supported.First(item => item.Id == DefaultProviderId);
}
