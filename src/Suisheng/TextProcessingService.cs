using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Suisheng;

public sealed record ReplacementRule(string Source, string Target);

public static class TextProcessingService
{
    public static IReadOnlyList<ReplacementRule> ParseReplacements(string text)
    {
        var rules = new List<ReplacementRule>();
        foreach (string line in text.Split('\n'))
        {
            string value = line.Trim();
            if (value.Length == 0) continue;
            string[] parts = value.Split("=>", StringSplitOptions.None);
            if (parts.Length != 2) throw new ArgumentException("每行词语替换须写成：原词 => 替换词。");
            string source = parts[0].Trim();
            string target = parts[1].Trim();
            if (source.Length == 0 || source.Length > 200 || target.Length > 1000 || source.Any(char.IsControl) || target.Any(char.IsControl))
                throw new ArgumentException("替换词语不能为空或包含控制字符；原词不超过 200 字，替换内容不超过 1000 字。");
            if (rules.Any(rule => rule.Source.Equals(source, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("词语替换表存在重复原词，请合并或删除重复项。");
            rules.Add(new(source, target));
            if (rules.Count > 200) throw new ArgumentException("最多支持 200 条词语替换。");
        }
        return rules;
    }

    public static string Process(string raw, UserSettings settings)
    {
        string result = settings.BasicFormatting ? Regex.Replace(raw.Trim(), @"[ \t]{2,}", " ") : raw.Trim();
        if (settings.RemoveFillerWords)
        {
            result = Regex.Replace(result, @"(?i)(?<![\p{L}\p{N}])(?:um+|uh+|hmm+|erm)(?![\p{L}\p{N}])", "");
            result = Regex.Replace(result, @"(^|[\s，,。！？!?])(?:嗯+|呃+|额+|啊+|哦+)(?=$|[\s，,。！？!?])", "$1");
            result = Regex.Replace(result, @"[ ]{2,}", " ").Trim();
        }
        var rules = ParseReplacements(settings.ReplacementRules).OrderByDescending(rule => rule.Source.Length).ToList();
        if (rules.Count > 0 && result.Length > 0)
        {
            var targets = rules.ToDictionary(rule => rule.Source, rule => rule.Target, StringComparer.OrdinalIgnoreCase);
            string pattern = string.Join("|", rules.Select(rule => Regex.Escape(rule.Source)));
            result = Regex.Replace(result, pattern, match => targets[match.Value], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        result = result.Trim();
        if (settings.AutoPunctuation && result.Length > 0 && !"。！？.!?；;：:…".Contains(result[^1]))
            result += settings.Language == "en" ? "." : "。";
        if (!TargetInputService.IsSafeText(result) && result.Length != 0)
            throw new InvalidOperationException("文字处理结果过长或包含控制字符，请修改替换词后重试。");
        return result;
    }
}
