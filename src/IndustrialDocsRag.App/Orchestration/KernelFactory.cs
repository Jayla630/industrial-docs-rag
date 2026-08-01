using System.Reflection;
using IndustrialDocsRag.App.Configuration;
using Microsoft.SemanticKernel;

namespace IndustrialDocsRag.App.Orchestration;

/// <summary>
/// 构建 Semantic Kernel。
/// <para>
/// 命名空间刻意叫 Orchestration 而不是 Kernel —— 后者会遮蔽 SK 的 <c>Kernel</c> 类型，
/// 在本命名空间内写 <c>Kernel</c> 会被解析成命名空间而不是类。
/// </para>
/// </summary>
public static class KernelFactory
{
    private const string PromptResourceName = "IndustrialDocsRag.App.Prompts.answer.md";

    public static Kernel Create(RagOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Chat.ApiKey))
        {
            throw new InvalidOperationException(
                "缺少生成模型的 API Key。请执行：\n" +
                "  dotnet user-secrets set \"Rag:Chat:ApiKey\" \"<你的key>\" --project src/IndustrialDocsRag.App");
        }

        var builder = Kernel.CreateBuilder();

        // DeepSeek 没有官方 SK connector，但它的接口与 OpenAI 兼容，
        // 用带 endpoint 参数的重载指向 api.deepseek.com 即可。
        builder.AddOpenAIChatCompletion(
            modelId: options.Chat.Model,
            endpoint: new Uri(options.Chat.Endpoint),
            apiKey: options.Chat.ApiKey);

        return builder.Build();
    }

    /// <summary>
    /// 用 SK 的模板引擎渲染系统提示词，把召回的片段填进 <c>{{$context}}</c>。
    /// </summary>
    public static async Task<string> RenderSystemPromptAsync(
        Kernel kernel,
        string context,
        CancellationToken ct = default)
    {
        var config = new PromptTemplateConfig(LoadPromptTemplate())
        {
            // SK 默认会对注入的变量做 HTML 编码来防提示词注入。
            // 但手册内容里本来就有 < > &，尤其 SectionPath 形如「第5章 故障处理 > 5.3 …」，
            // 编码后模型看到的是满屏 &gt;，可读性和检索线索都被破坏。
            // 这里的 context 来自我们自己的索引、不是用户输入，关掉编码是安全的。
            AllowDangerouslySetContent = true,
        };
        config.InputVariables.Add(new InputVariable
        {
            Name = "context",
            AllowDangerouslySetContent = true,
        });

        var factory = new KernelPromptTemplateFactory { AllowDangerouslySetContent = true };
        if (!factory.TryCreate(config, out var template) || template is null)
        {
            throw new InvalidOperationException("SK 无法解析 Prompts/answer.md 模板。");
        }

        return await template.RenderAsync(kernel, new KernelArguments { ["context"] = context }, ct);
    }

    private static string LoadPromptTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(PromptResourceName)
            ?? throw new InvalidOperationException(
                $"找不到嵌入资源 {PromptResourceName}。" +
                "确认 csproj 里 Prompts/answer.md 已声明为 EmbeddedResource。");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
