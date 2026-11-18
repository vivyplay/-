using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Suisheng;

public sealed record HistoryEntry(Guid Id, DateTime CreatedAt, string Original, string Text, bool Submitted)
{
    public string Status => Submitted ? "已写入" : "待手动";
}

public sealed class HistoryStore
{
    private readonly string path;
    private List<HistoryEntry> entries = [];
    public string? LoadWarning { get; private set; }
    public HistoryStore(string? root = null)
    {
        path = Path.Combine(root ?? SettingsStore.DataDirectory, "history.json");
        if (!File.Exists(path)) return;
        try
        {
            entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path)) ?? [];
            if (entries.Any(item => item is null || item.Original is null || item.Text is null || item.Id == Guid.Empty || item.Original.Length > 16000 || item.Text.Length > 16000 || item.Text.Any(char.IsControl)) || entries.Select(item => item.Id).Distinct().Count() != entries.Count)
                throw new InvalidDataException("历史记录格式无效。");
            entries = entries.OrderByDescending(item => item.CreatedAt).Take(100).ToList();
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidDataException or ArgumentException)
        {
            entries = [];
            LoadWarning = "历史记录无法读取，原文件未删除；再次新增记录前请备份该文件。";
        }
    }

    public IReadOnlyList<HistoryEntry> GetAll(UserSettings settings)
    {
        if (LoadWarning != null) return [];
        entries = entries.Where(item => item.CreatedAt >= DateTime.Now.AddDays(-settings.HistoryRetentionDays)).ToList();
        return entries;
    }

    public void Record(UserSettings settings, string original, string text, bool submitted)
    {
        if (!settings.HistoryEnabled || LoadWarning != null || text.Length == 0) return;
        GetAll(settings);
        entries.Insert(0, new(Guid.NewGuid(), DateTime.Now, settings.KeepOriginalResult ? original : "", text, submitted));
        entries = entries.Take(100).ToList();
        Save();
    }

    public void Delete(Guid id)
    {
        entries.RemoveAll(item => item.Id == id);
        Save();
    }

    public void Clear()
    {
        LoadWarning = null;
        entries.Clear();
        if (File.Exists(path)) File.Delete(path);
    }

    private void Save()
    {
        if (LoadWarning != null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
