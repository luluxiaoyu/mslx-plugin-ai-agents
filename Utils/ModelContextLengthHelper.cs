using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MSLX.Plugin.AiAgents.Utils;

public static partial class ModelContextLengthHelper
{
    public const string DefaultContextLengthText = "256k";
    public const int DefaultContextTokens = 256 * 1024;
    public const int MinContextTokens = 1024;
    public const int MaxContextTokens = 16 * 1024 * 1024;

    [GeneratedRegex(@"^\s*(?<num>\d+(?:\.\d+)?)\s*(?<unit>[kKmM]?)\s*$")]
    private static partial Regex ContextLengthRegex();

    public static bool TryParse(string? input, out int tokens, out string normalized, out string errorMessage)
    {
        tokens = DefaultContextTokens;
        normalized = DefaultContextLengthText;

        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = "模型上下文长度不能为空，支持数字或 k/m 单位（如 128k、256k、1M）。";
            return false;
        }

        var match = ContextLengthRegex().Match(input);
        if (!match.Success)
        {
            errorMessage = "模型上下文长度格式无效，仅支持正数及 k/K、m/M 单位（如 128k、256K、1m、2M）。";
            return false;
        }

        if (!double.TryParse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value <= 0)
        {
            errorMessage = "模型上下文长度必须为大于 0 的数字。";
            return false;
        }

        var unit = match.Groups["unit"].Value;
        double multiplier = unit switch
        {
            "k" or "K" => 1024d,
            "m" or "M" => 1024d * 1024d,
            _ => 1d
        };

        var scaled = Math.Round(value * multiplier);
        if (!double.IsFinite(scaled) || scaled < MinContextTokens)
        {
            errorMessage = $"模型上下文长度不能小于 1k ({MinContextTokens} tokens)。";
            return false;
        }

        if (scaled > MaxContextTokens)
        {
            errorMessage = $"模型上下文长度不能超过 16m ({MaxContextTokens} tokens)。";
            return false;
        }

        tokens = (int)scaled;
        normalized = match.Groups["num"].Value + unit;
        errorMessage = string.Empty;
        return true;
    }

    public static (int Tokens, string Label) Resolve(string? configured)
    {
        return TryParse(configured, out var tokens, out var normalized, out _)
            ? (tokens, normalized)
            : (DefaultContextTokens, DefaultContextLengthText);
    }

    public static string FormatTokens(long tokens)
    {
        if (tokens < 1024) return tokens.ToString(CultureInfo.InvariantCulture);
        if (tokens < 1024 * 1024) return $"{tokens / 1024d:0.#}k";
        return $"{tokens / (1024d * 1024d):0.##}m";
    }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ModelContextLengthAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null)
        {
            return ValidationResult.Success;
        }

        if (value is not string text)
        {
            return new ValidationResult("模型上下文长度必须是字符串格式，如 256k 或 1M。",
                validationContext.MemberName is { } m ? new[] { m } : null);
        }

        if (!ModelContextLengthHelper.TryParse(text, out _, out _, out var errorMessage))
        {
            return new ValidationResult(errorMessage,
                validationContext.MemberName is { } m ? new[] { m } : null);
        }

        return ValidationResult.Success;
    }
}
