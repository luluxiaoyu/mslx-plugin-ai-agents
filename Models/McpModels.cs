using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSLX.Plugin.AiAgents.Models;

/// <summary>
/// 标准 MCP JSON-RPC 2.0 请求对象
/// </summary>
internal sealed record McpRequest(JsonElement? Id, string Method, JsonElement? Params)
{
    internal static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { });

    internal static bool TryParse(JsonElement body, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out McpRequest? request,
        out JsonElement? id)
    {
        request = null;
        id = null;
        if (body.ValueKind != JsonValueKind.Object)
            return false;

        if (body.TryGetProperty("id", out var idProperty))
        {
            if (idProperty.ValueKind != JsonValueKind.String && !IsInteger(idProperty))
                return false;

            id = idProperty;
        }

        var method = GetString(body, "method");
        if (GetString(body, "jsonrpc") != "2.0" || string.IsNullOrWhiteSpace(method))
            return false;

        request = new McpRequest(id, method, body.TryGetProperty("params", out var parameters) ? parameters : null);
        return true;
    }

    internal static string? GetString(JsonElement? obj, string key) =>
        obj is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    internal static bool IsInteger(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number
        && (value.TryGetInt64(out _) || (value.TryGetDecimal(out var number) && decimal.Truncate(number) == number)
            || !value.GetRawText().Contains('.') && !value.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 标准 MCP JSON-RPC 2.0 响应对象
/// </summary>
public class McpResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpError? Error { get; set; }

    public static McpResponse Success(object? id, object? result) => new()
    {
        Id = id,
        Result = result
    };

    public static McpResponse Fail(object? id, McpError error) => new()
    {
        Id = id,
        Error = error
    };
}

/// <summary>
/// 标准 JSON-RPC 2.0 错误对象
/// </summary>
public class McpError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }

    public static McpError ParseError(string msg = "Parse error") =>
        new() { Code = -32700, Message = msg };

    public static McpError InvalidRequest(string msg = "Invalid Request") =>
        new() { Code = -32600, Message = msg };

    public static McpError MethodNotFound(string msg = "Method not found") =>
        new() { Code = -32601, Message = msg };

    public static McpError InvalidParams(string msg = "Invalid params") =>
        new() { Code = -32602, Message = msg };

    public static McpError InternalError(string msg = "Internal error") =>
        new() { Code = -32603, Message = msg };
}

/// <summary>
/// MCP 工具定义
/// </summary>
public class McpToolDefinition
{
    internal static readonly IReadOnlyList<string> TargetIdFields = ["server_id", "tunnel_id"];

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; set; } = JsonSerializer.SerializeToElement(new { type = "object" });

    internal string? ValidateArguments(JsonElement arguments) => ValidateValue(InputSchema, arguments, "arguments");

    // Validate the JSON Schema keywords used by the published tool definitions.
    private static string? ValidateValue(JsonElement schema, JsonElement value, string path)
    {
        var type = McpRequest.GetString(schema, "type");
        var matchesType = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => McpRequest.IsInteger(value),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => true
        };
        if (!matchesType)
            return $"'{path}' must be of type {type}.";

        if (schema.TryGetProperty("enum", out var choices)
            && !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(choice, value)))
            return $"'{path}' must be one of {choices.GetRawText()}.";

        if (schema.TryGetProperty("minimum", out var minimum) && schema.TryGetProperty("maximum", out var maximum)
            && (!value.TryGetDecimal(out var number) || number < minimum.GetDecimal() || number > maximum.GetDecimal()))
            return $"'{path}' must be between {minimum.GetRawText()} and {maximum.GetRawText()}.";

        if (type == "object")
        {
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var key in required.EnumerateArray())
                {
                    if (!value.TryGetProperty(key.GetString()!, out _))
                        return $"'{path}.{key.GetString()}' is required.";
                }
            }

            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (value.TryGetProperty(property.Name, out var argument)
                        && ValidateValue(property.Value, argument, $"{path}.{property.Name}") is { } error)
                        return error;
                }
            }
        }

        if (type == "array" && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (ValidateValue(items, item, $"{path}[{index++}]") is { } error)
                    return error;
            }
        }

        return null;
    }
}

/// <summary>
/// MCP 工具内容 (如文本、图片等)
/// </summary>
public class McpToolContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
}

/// <summary>
/// MCP 工具执行结果
/// </summary>
public class McpToolResult
{
    [JsonPropertyName("content")]
    public List<McpToolContent> Content { get; set; } = new();

    [JsonPropertyName("isError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsError { get; set; }

    [JsonPropertyName("structuredContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? StructuredContent { get; set; }

    public static McpToolResult Success(string text) => new()
    {
        Content = new List<McpToolContent> { new() { Type = "text", Text = text } },
        IsError = false
    };

    public static McpToolResult Error(string text) => new()
    {
        Content = new List<McpToolContent> { new() { Type = "text", Text = text } },
        IsError = true
    };
}

/// <summary>
/// MCP 初始化握手结果
/// </summary>
public class McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public McpServerCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("serverInfo")]
    public McpImplementation ServerInfo { get; set; } = new();

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }
}

public class McpServerCapabilities
{
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpToolsCapability? Tools { get; set; } = new();
}

public class McpToolsCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; } = false;
}

public class McpImplementation
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "MSLX.Plugin.AiAgents";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";
}

/// <summary>
/// MCP Token 存储实体
/// </summary>
public class McpTokenRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public bool ReadOnly { get; set; }
    public int[]? ServerIds { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAt { get; set; }
    internal DateTime ToolCallWindowStartedAt { get; set; }
    internal int ToolCallsInWindow { get; set; }
}

/// <summary>
/// MCP Token 外部展示 DTO (已脱敏)
/// </summary>
public class McpTokenDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string MaskedToken { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public bool ReadOnly { get; set; }
    public int[]? ServerIds { get; set; }
    public bool IsRevoked { get; set; }
    public string Status { get; set; } = "active"; // active, expired, revoked
}

/// <summary>
/// 创建 MCP Token 请求
/// </summary>
public class CreateMcpTokenRequest
{
    public string Name { get; set; } = string.Empty;
    public int? ExpiresInDays { get; set; }
    public int? ExpireDays { get; set; }
    public bool ReadOnly { get; set; }
    public int[]? ServerIds { get; set; }

    public int? GetEffectiveDays() => ExpiresInDays ?? ExpireDays;
}
