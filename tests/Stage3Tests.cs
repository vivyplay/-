using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using Suisheng;

namespace SuishengTests;

internal static class Stage3Tests
{
    public static async Task Run(string root)
    {
        string directory = Path.Combine(root, "ai-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new UserSettings();
        var store = new SettingsStore(directory);
        store.Save(settings);
        Program.Record("AI 默认关闭且普通设置不包含接口配置依赖", !store.Load().AiEnabled && settings.AiEndpoint == "" && settings.AiModel == "");
        settings.AiEndpoint = "https://example.invalid/v1/chat/completions"; settings.AiModel = "contract-model";
        settings.AiTimeoutSeconds = 5; settings.AiTemplateId = "proofread"; settings.AiInstructions = "仅纠错"; settings.AiStrength = "light";
        store.Save(settings);
        string json = File.ReadAllText(Path.Combine(directory, "settings.json"));
        Program.Record("AI 非敏感配置保存及范围校验", store.Load().AiStrength == "light" && !json.Contains("contract-secret", StringComparison.Ordinal));
        try { SettingsStore.Validate(new UserSettings { AiEndpoint = "http://example.com/v1/chat/completions" }); Program.Record("公网明文 HTTP 接口拒绝", false); }
        catch (ArgumentException) { Program.Record("公网明文 HTTP 接口拒绝", true); }
        try { SettingsStore.Validate(new UserSettings { AiEndpoint = "https://user:secret@example.com/v1/chat/completions" }); Program.Record("接口 URL 用户信息拒绝", false); }
        catch (ArgumentException) { Program.Record("接口 URL 用户信息拒绝", true); }

        var credentials = new AiCredentialStore(directory);
        credentials.Save("contract-secret");
        string encrypted = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(directory, "ai-credential.bin")));
        Program.Record("DPAPI 当前用户密钥往返及密文落盘", credentials.Load() == "contract-secret" && !encrypted.Contains("contract-secret", StringComparison.Ordinal) &&
            !File.ReadAllText(Path.Combine(directory, "settings.json")).Contains("contract-secret", StringComparison.Ordinal));
        credentials.Clear();
        Program.Record("AI 密钥可明确删除", !credentials.HasKey && credentials.Load() == null);

        var templates = new AiTemplateStore(directory);
        Program.Record("五种真实内置 AI 模板", templates.GetAll().Count == 5 && templates.GetAll().Select(item => item.Id).SequenceEqual(new[] { "general", "concise", "detailed-task", "programming", "proofread" }));
        var custom = templates.Find("general"); custom.Id = "custom-contract"; custom.Name = "契约模板"; custom.BuiltIn = false; custom.UserPrompt = "要求：{{instructions}}\n{{text}}";
        templates.Save(custom);
        Program.Record("自定义 AI 模板新建与持久化", new AiTemplateStore(directory).Find("custom-contract").Name == "契约模板");
        templates.RestoreBuiltIns(); templates.Delete("custom-contract");
        Program.Record("恢复内置模板及删除自定义模板", templates.GetAll().Count == 5 && new AiTemplateStore(directory).GetAll().Count == 5);
        string brokenPath = Path.Combine(directory, "broken-templates.json"); File.WriteAllText(brokenPath, "broken");
        string brokenRoot = Path.Combine(directory, "broken"); Directory.CreateDirectory(brokenRoot); File.Copy(brokenPath, Path.Combine(brokenRoot, "ai-templates.json"));
        var broken = new AiTemplateStore(brokenRoot);
        bool protectedBroken = broken.LoadWarning != null;
        try { broken.Delete("general"); protectedBroken = false; }
        catch (InvalidDataException) { }
        Program.Record("损坏 AI 模板不被静默覆盖", protectedBroken && File.ReadAllText(Path.Combine(brokenRoot, "ai-templates.json")) == "broken");

        settings.AiEndpoint = "http://127.0.0.1:1/v1/chat/completions?api-version=contract";
        var template = templates.Find("general");
        using var client = new OpenAiCompatibleClient();
        await WithServer(async context =>
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync();
            using var document = JsonDocument.Parse(body);
            bool valid = context.Request.Url!.AbsolutePath == "/v1/chat/completions" && context.Request.Url.Query == "?api-version=contract" &&
                context.Request.Headers["Authorization"] == "Bearer contract-secret" &&
                document.RootElement.GetProperty("model").GetString() == "contract-model" &&
                document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("待处理原文", StringComparison.Ordinal);
            await Respond(context, 200, "{\"choices\":[{\"message\":{\"content\":\"  优化后的正文。  \"}}]}");
            Program.Record("OpenAI 兼容真实 HTTP 请求格式与原文来源", valid);
        }, async endpoint =>
        {
            settings.AiEndpoint = endpoint + "/v1/chat/completions?api-version=contract";
            string result = await client.OptimizeAsync(settings, "contract-secret", template, "待处理原文", CancellationToken.None);
            Program.Record("OpenAI 兼容响应正文解析", result == "优化后的正文。");
        });

        await ErrorCase(client, settings, template, 401, "{}", AiErrorKind.InvalidKey, "AI 无效密钥错误分类");
        int attempts = 0;
        await WithServer(async context => { attempts++; await Respond(context, 429, "{\"error\":{\"code\":\"insufficient_quota\"}}"); }, async endpoint =>
        {
            settings.AiEndpoint = endpoint;
            var kind = await GetError(client, settings, template);
            Program.Record("AI 额度错误分类且不自动重试", kind == AiErrorKind.Quota && attempts == 1, $"请求次数={attempts}");
        });
        await ErrorCase(client, settings, template, 404, "{}", AiErrorKind.ModelUnavailable, "AI 模型不可用错误分类");
        await ErrorCase(client, settings, template, 429, "{}", AiErrorKind.RateLimited, "AI 限流错误分类");
        await ErrorCase(client, settings, template, 200, "{\"choices\":[{\"message\":{\"content\":\"  \"}}]}", AiErrorKind.EmptyResponse, "AI 空响应明确拒绝");
        await ErrorCase(client, settings, template, 200, "not-json", AiErrorKind.InvalidResponse, "AI 异常格式明确拒绝");

        await WithServer(async context => { await Task.Delay(1000); await Respond(context, 200, "{\"choices\":[{\"message\":{\"content\":\"迟到结果\"}}]}"); }, async endpoint =>
        {
            settings.AiEndpoint = endpoint;
            using var cancellation = new CancellationTokenSource(100);
            try { await client.OptimizeAsync(settings, "contract-secret", template, "原文", cancellation.Token); Program.Record("AI 请求用户取消", false); }
            catch (OperationCanceledException) { Program.Record("AI 请求用户取消", true); }
        });
        await WithServer(async context => { await Task.Delay(6500); try { await Respond(context, 200, "{}"); } catch (HttpListenerException) { } }, async endpoint =>
        {
            settings.AiEndpoint = endpoint;
            var kind = await GetError(client, settings, template);
            Program.Record("AI 超时分类且不自动重试", kind == AiErrorKind.Timeout);
        });
        await TestSession(Path.Combine(directory, "session"));
    }

    private static async Task TestSession(string root)
    {
        string? previous = Environment.GetEnvironmentVariable("SUISHENG_DATA_DIRECTORY");
        Environment.SetEnvironmentVariable("SUISHENG_DATA_DIRECTORY", root);
        using var controller = new SessionController(System.Windows.Application.Current.Dispatcher);
        try
        {
            await WithServer(context => Respond(context, 200, "{\"choices\":[{\"message\":{\"content\":\"AI 优化结果\"}}]}"), async endpoint =>
            {
                controller.SaveAiConfiguration(endpoint, "session-contract", 20, "session-secret", false);
                await controller.SetAiEnabledAsync(true);
                await controller.OptimizeAsync("手动原文", "保留原意", "general");
                Program.Record("AI 完成后默认停留待确认且不写入", controller.Phase == SessionPhase.AwaitingConfirmation && controller.Result == "AI 优化结果" && controller.OriginalResult == "手动原文");
                controller.UpdateAiResult("人工编辑后的结果");
                Program.Record("AI 预览结果可编辑且原文不变", controller.Result == "人工编辑后的结果" && controller.OriginalResult == "手动原文");
                await controller.CancelAsync();
                Program.Record("取消待确认保留结果并回到待机", controller.Phase == SessionPhase.Idle && controller.Result == "人工编辑后的结果");
            });
            int requests = 0;
            await WithServer(async context => { requests++; await Task.Delay(800); try { await Respond(context, 200, "{\"choices\":[{\"message\":{\"content\":\"迟到结果\"}}]}"); } catch (HttpListenerException) { } }, async endpoint =>
            {
                controller.SaveAiConfiguration(endpoint, "session-contract", 20, null, false);
                var optimizing = controller.OptimizeAsync("取消期间原文", "保留原意", "general");
                await Task.Delay(100);
                bool optimizingState = controller.Phase == SessionPhase.Optimizing;
                await controller.SetAiEnabledAsync(false);
                await optimizing;
                await Task.Delay(900);
                Program.Record("优化中关闭 AI 取消并忽略迟到结果", optimizingState && requests == 1 && controller.Phase == SessionPhase.Idle && !controller.Settings.AiEnabled && controller.Result == "取消期间原文");
                bool blocked = false;
                try { await controller.OptimizeAsync("不应发送", "", "general"); }
                catch (InvalidOperationException) { blocked = true; }
                Program.Record("AI 关闭时手动优化不发送请求", blocked && requests == 1);
            });
            await WithServer(context => Respond(context, 401, "{}"), async endpoint =>
            {
                controller.SaveAiConfiguration(endpoint, "session-contract", 20, null, false);
                await controller.SetAiEnabledAsync(true);
                await controller.OptimizeAsync("失败时保留原文", "保留原意", "general");
                Program.Record("AI 失败保留原文且普通会话可恢复", controller.Phase == SessionPhase.Idle && controller.Result == "失败时保留原文" && controller.OriginalResult == "失败时保留原文");
                await controller.SetAiEnabledAsync(false);
                Program.Record("关闭 AI 不要求有效 API 配置", !controller.Settings.AiEnabled && controller.Phase == SessionPhase.Idle);
            });
        }
        finally { Environment.SetEnvironmentVariable("SUISHENG_DATA_DIRECTORY", previous); }
    }

    private static async Task ErrorCase(OpenAiCompatibleClient client, UserSettings settings, AiTemplate template, int status, string body, AiErrorKind expected, string name)
    {
        await WithServer(context => Respond(context, status, body), async endpoint =>
        {
            settings.AiEndpoint = endpoint;
            Program.Record(name, await GetError(client, settings, template) == expected);
        });
    }

    private static async Task<AiErrorKind?> GetError(OpenAiCompatibleClient client, UserSettings settings, AiTemplate template)
    {
        try { await client.OptimizeAsync(settings, "contract-secret", template, "原文", CancellationToken.None); return null; }
        catch (AiRequestException e) { return e.Kind; }
    }

    private static async Task WithServer(Func<HttpListenerContext, Task> handler, Func<string, Task> test)
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        using var listener = new HttpListener();
        string prefix = $"http://127.0.0.1:{port}/"; listener.Prefixes.Add(prefix); listener.Start();
        var serving = Task.Run(async () =>
        {
            try { await handler(await listener.GetContextAsync()); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
        });
        try { await test(prefix.TrimEnd('/')); }
        finally { listener.Stop(); await serving; }
    }

    private static async Task Respond(HttpListenerContext context, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body); context.Response.StatusCode = status; context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
    }
}
