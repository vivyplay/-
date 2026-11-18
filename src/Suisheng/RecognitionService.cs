using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace Suisheng;

public sealed record ModelInfo(string Id, string Name, string FileName, long Bytes, string Sha256)
{
    public static IReadOnlyList<ModelInfo> All { get; } =
    [
        new("tiny-q5_1", "Tiny 多语言量化 · 31 MB · 快速试用", "ggml-tiny-q5_1.bin", 32152673, "818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7"),
        new("base", "Base 多语言 · 141 MB · 更好的识别", "ggml-base.bin", 147951465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe")
    ];
    public static ModelInfo Find(string id) => id == "base" ? All[1] : All[0];
}

public sealed class RecognitionService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static string ModelPath(UserSettings settings) => Path.Combine(settings.ModelDirectory, ModelInfo.Find(settings.ModelId).FileName);

    public static async Task<bool> VerifyAsync(string path, ModelInfo model, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != model.Bytes) return false;
        await using var stream = File.OpenRead(path);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        return hash.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task DownloadAsync(UserSettings settings, IProgress<double> progress, CancellationToken token)
    {
        var model = ModelInfo.Find(settings.ModelId);
        string destination = ModelPath(settings);
        Directory.CreateDirectory(settings.ModelDirectory);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        string host = settings.DownloadSource == "mirror" ? "https://hf-mirror.com" : "https://huggingface.co";
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            using var response = await client.GetAsync($"{host}/ggerganov/whisper.cpp/resolve/main/{model.FileName}", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    received += count;
                    if (received > model.Bytes) throw new InvalidDataException("模型文件大小异常，已停止下载。");
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    progress.Report(100.0 * received / model.Bytes);
                }
            }
            if (!await VerifyAsync(temporary, model, deadline.Token)) throw new InvalidDataException("模型 SHA-256 校验失败，请重新下载。");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<string> TranscribeAsync(float[] samples, UserSettings settings, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            return await Task.Run(async () =>
            {
                string path = ModelPath(settings);
                if (!await VerifyAsync(path, ModelInfo.Find(settings.ModelId), cancellation))
                    throw new InvalidDataException("模型缺失或校验失败，请先下载正确模型。");
                cancellation.ThrowIfCancellationRequested();
                using var factory = WhisperFactory.FromPath(path);
                var builder = factory.CreateBuilder().WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8)).WithNoContext();
                if (settings.Language == "auto") builder.WithLanguageDetection();
                else builder.WithLanguage(settings.Language);
                if (settings.Language == "zh") builder.WithPrompt("以下是简体中文的语音转写。");
                // 取消枚举后仍须等待原生推理结束，才能释放模型。
                await using var processor = builder.Build();
                var segments = new List<string>();
                await foreach (var segment in processor.ProcessAsync(samples, cancellation))
                {
                    cancellation.ThrowIfCancellationRequested();
                    segments.Add(segment.Text);
                }
                cancellation.ThrowIfCancellationRequested();
                return string.Join("", segments).Trim();
            }, cancellation);
        }
        finally { gate.Release(); }
    }
}
