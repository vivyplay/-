using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Suisheng;

public enum AiErrorKind { InvalidKey, ModelUnavailable, Network, Timeout, RateLimited, Quota, EmptyResponse, InvalidResponse, Service }

public sealed class AiRequestException : Exception
{
    public AiErrorKind Kind { get; }
    public AiRequestException(AiErrorKind kind, string message) : base(message) => Kind = kind;
}

public sealed class OpenAiCompatibleClient : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsClient;

    public OpenAiCompatibleClient(HttpClient? http = null)
    {
        this.http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        ownsClient = http == null;
    }

    public Task<string> OptimizeAsync(UserSettings settings, string apiKey, AiTemplate template, string source, CancellationToken cancellation)
    {
        string strength = settings.AiStrength switch
        {
            "light" => "尽量少改动，只处理明显错误和不通顺之处。",
            "thorough" => "可以充分重组句式与结构，但不得遗漏或虚构事实。",
            _ => "适度调整表达和结构，保留原意与关键细节。"
        };
        string expansion = settings.AiAllowExpansion ? "可以补充必要的衔接，但不得虚构事实。" : "不要扩写，不增加原文没有的信息。";
        string system = template.SystemPrompt + "\n" + strength + expansion + " 输出纯文本正文，不使用 Markdown 代码围栏。";
        string user = template.UserPrompt.Replace("{{instructions}}", settings.AiInstructions, StringComparison.Ordinal)
            .Replace("{{text}}", source, StringComparison.Ordinal);
        return SendAsync(settings, apiKey, system, user, 4096, cancellation);
    }

    public Task<string> TestConnectionAsync(UserSettings settings, string apiKey, CancellationToken cancellation) =>
        SendAsync(settings, apiKey, "你是连接测试助手，只回复指定文字。", "只回复：连接成功", 16, cancellation);

    private async Task<string> SendAsync(UserSettings settings, string apiKey, string system, string user, int maximumTokens, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new AiRequestException(AiErrorKind.InvalidKey, "尚未配置 API Key。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.AiTimeoutSeconds));
        try
        {
            var payload = new
            {
                model = settings.AiModel,
                messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
                temperature = 0.3,
                max_tokens = maximumTokens
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.AiEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (body.Length > 2_000_000) throw new AiRequestException(AiErrorKind.InvalidResponse, "AI 服务返回内容过大，已停止处理。");
            if (!response.IsSuccessStatusCode) throw Classify(response.StatusCode, body);
            return Parse(body);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new AiRequestException(AiErrorKind.Timeout, $"AI 请求超过 {settings.AiTimeoutSeconds} 秒，已取消；不会自动重试。");
        }
        catch (HttpRequestException)
        {
            throw new AiRequestException(AiErrorKind.Network, "无法连接 AI 服务，请检查网络、接口地址和 TLS 配置；不会自动重试。");
        }
        catch (AiRequestException) { throw; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException)
        {
            throw new AiRequestException(AiErrorKind.InvalidResponse, "AI 服务返回格式不符合 OpenAI 兼容 Chat Completions 协议。");
        }
    }

    private static string Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new AiRequestException(AiErrorKind.InvalidResponse, "AI 响应缺少 choices 内容。");
        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new AiRequestException(AiErrorKind.InvalidResponse, "AI 响应缺少 message.content 文本。");
        string result = content.GetString()?.Trim() ?? "";
        if (result.Length == 0) throw new AiRequestException(AiErrorKind.EmptyResponse, "AI 返回了空内容，原文已保留，可重试或修改模板。");
        if (result.Length > 100_000) throw new AiRequestException(AiErrorKind.InvalidResponse, "AI 返回文本超过 100000 字符，已停止处理。");
        return result;
    }

    private static AiRequestException Classify(HttpStatusCode status, string body)
    {
        bool quota = false;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                (error.TryGetProperty("code", out var code) || error.TryGetProperty("type", out code)) && code.ValueKind == JsonValueKind.String)
                quota = code.GetString()?.Contains("quota", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (JsonException) { }
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AiErrorKind.InvalidKey, "AI 服务拒绝了 API Key 或访问权限，请检查凭据和服务权限。"),
            HttpStatusCode.NotFound => new(AiErrorKind.ModelUnavailable, "接口地址或模型不可用，请按服务文档核对完整 Chat Completions 地址和模型名称。"),
            HttpStatusCode.PaymentRequired => new(AiErrorKind.Quota, "AI 服务额度不足或需要付费，请检查账户；不会自动重试。"),
            (HttpStatusCode)429 when quota => new(AiErrorKind.Quota, "AI 服务报告额度不足，请检查账户；不会自动重试。"),
            (HttpStatusCode)429 => new(AiErrorKind.RateLimited, "AI 服务请求受限，请稍后手动重试；不会自动重试。"),
            _ => new(AiErrorKind.Service, $"AI 服务返回 HTTP {(int)status}，请求未完成；不会自动重试。")
        };
    }

    public void Dispose()
    {
        if (ownsClient) http.Dispose();
    }
}
