using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MSLX.Plugin.AiAgents.Models;
using MSLX.Plugin.AiAgents.Services;
using MSLX.Plugin.AiAgents.Utils;

namespace MSLX.Plugin.AiAgents.Controllers;

[ApiController]
[AllowAnonymous]
[Route(RoutePrefix)]
public class McpController(
    AiService aiService,
    McpTokenManager tokenManager,
    IConfiguration configuration,
    ILogger<McpController> logger) : ControllerBase
{
    private const string RoutePrefix = "api/plugin/mslx-plugin-ai-agents/mcp";
    private const string LegacyProtocolVersion = "2024-11-05";
    private const string PreferredProtocolVersion = "2025-11-25";
    private const string ProtocolVersionHeader = "MCP-Protocol-Version";

    private static readonly ConcurrentDictionary<string, LegacySession> Sessions = new();

    [HttpGet("skill.md")]
    public IActionResult GetSkillMarkdown() =>
        Content(AiSkillManager.GetSkillMarkdown(aiService.GetMcpToolDefinitions()), "text/markdown; charset=utf-8", Encoding.UTF8);

    [HttpGet("sse")]
    public async Task GetSse(CancellationToken cancellationToken)
    {
        if (!IsAllowedOrigin())
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!tokenManager.ValidateToken(GetCredential(allowQueryToken: true), out var token))
        {
            Response.Headers.WWWAuthenticate = "Bearer";
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        var sessionId = Guid.NewGuid().ToString("N");
        var session = new LegacySession(token!, Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true }));
        Sessions[sessionId] = session;

        try
        {
            var endpoint = $"{Request.PathBase}/{RoutePrefix}/message?sessionId={sessionId}";
            await WriteSseAsync($"event: endpoint\ndata: {endpoint}\n\n", cancellationToken);

            while (!cancellationToken.IsCancellationRequested && tokenManager.IsActive(session.Token))
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await session.Messages.Reader.WaitToReadAsync(heartbeat.Token))
                        break;

                    while (session.Messages.Reader.TryRead(out var message))
                        await WriteSseAsync($"event: message\ndata: {message}\n\n", cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (tokenManager.IsActive(session.Token))
                        await WriteSseAsync(": keepalive\n\n", cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected.
        }
        finally
        {
            Sessions.TryRemove(sessionId, out _);
            session.Messages.Writer.TryComplete();
        }
    }

    internal static void CloseLegacySessions()
    {
        foreach (var session in Sessions.Values)
            session.Messages.Writer.TryComplete();
    }

    [HttpPost]
    public Task<IActionResult> PostStreamableHttp(CancellationToken cancellationToken) =>
        HandleMessageAsync(legacyRoute: false, cancellationToken);

    [HttpGet]
    [HttpDelete]
    public IActionResult StreamableHttpDoesNotKeepSessions()
    {
        if (!IsAllowedOrigin())
            return StatusCode(StatusCodes.Status403Forbidden);

        Response.Headers.Allow = "POST";
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    [HttpPost("message")]
    public Task<IActionResult> PostLegacyMessage(CancellationToken cancellationToken) =>
        HandleMessageAsync(legacyRoute: true, cancellationToken);

    private async Task<IActionResult> HandleMessageAsync(bool legacyRoute, CancellationToken cancellationToken)
    {
        if (!IsAllowedOrigin())
            return StatusCode(StatusCodes.Status403Forbidden);

        LegacySession? session = null;
        var credential = GetCredential(allowQueryToken: legacyRoute);
        if (legacyRoute && Request.Query.TryGetValue("sessionId", out var sessionId))
        {
            if (sessionId.Count != 1 || !Sessions.TryGetValue(sessionId.ToString(), out session))
                return NotFound();
        }

        McpTokenRecord? token;
        if (session != null && credential == null)
        {
            token = session.Token;
            if (!tokenManager.IsActive(token))
                return UnauthorizedMcp();
        }
        else
        {
            if (!tokenManager.ValidateToken(credential, out token))
                return UnauthorizedMcp();

            if (session != null && !ReferenceEquals(session.Token, token))
                return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!Request.HasJsonContentType())
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);

        if (Request.Headers.TryGetValue(ProtocolVersionHeader, out var version)
            && (version.Count != 1 || !SupportsVersion(version.ToString(), legacyRoute, session != null)))
        {
            return WriteJson(McpResponse.Fail(null,
                McpError.InvalidRequest($"Unsupported {ProtocolVersionHeader}")), StatusCodes.Status400BadRequest);
        }

        if (!tokenManager.MarkUsed(token!, toolListRead: false)) return UnauthorizedMcp();

        JsonDocument document;
        try
        {
            // MCP uses System.Text.Json independently of the host's Newtonsoft.Json MVC formatters.
            document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return WriteJson(McpResponse.Fail(null, McpError.ParseError()), StatusCodes.Status400BadRequest);
        }

        using (document)
        {
            if (!McpRequest.TryParse(document.RootElement, out var request, out var id))
                return WriteJson(McpResponse.Fail(id, McpError.InvalidRequest()), StatusCodes.Status400BadRequest);

            // Notifications do not have an ID and must never dispatch request-only tools.
            if (request.Id == null)
                return Accepted();

            McpResponse response;
            if (request.Params is { ValueKind: not JsonValueKind.Object })
            {
                response = McpResponse.Fail(request.Id, McpError.InvalidParams("'params' must be an object."));
            }
            else
            {
                try
                {
                    response = await DispatchAsync(request, token!, legacyRoute, session != null);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP request failed for {Method}", request.Method);
                    response = McpResponse.Fail(request.Id, McpError.InternalError());
                }
            }

            if (session == null)
                return WriteJson(response);

            return session.Messages.Writer.TryWrite(JsonSerializer.Serialize(response))
                ? Accepted()
                : NotFound();
        }
    }

    private async Task<McpResponse> DispatchAsync(McpRequest request, McpTokenRecord token, bool legacyRoute, bool legacySse)
    {
        switch (request.Method)
        {
            case "initialize":
                var requestedVersion = McpRequest.GetString(request.Params, "protocolVersion");
                if (string.IsNullOrWhiteSpace(requestedVersion)
                    && (!legacyRoute || request.Params is { } initParams && initParams.TryGetProperty("protocolVersion", out _)))
                    return McpResponse.Fail(request.Id, McpError.InvalidParams("'protocolVersion' must be a non-empty string."));

                return McpResponse.Success(request.Id, new McpInitializeResult
                {
                    ProtocolVersion = requestedVersion != null && SupportsVersion(requestedVersion, legacyRoute, legacySse)
                        ? requestedVersion
                        : legacySse || requestedVersion == null ? LegacyProtocolVersion : PreferredProtocolVersion,
                    Capabilities = new McpServerCapabilities
                    {
                        Tools = new McpToolsCapability { ListChanged = false }
                    },
                    ServerInfo = new McpImplementation(),
                    Instructions = "MSLX Minecraft Server Management Agent MCP Server."
                });

            case "ping":
                return McpResponse.Success(request.Id, new { });

            case "tools/list":
                if (request.Params is { } parameters && parameters.TryGetProperty("cursor", out _))
                    return McpResponse.Fail(request.Id, McpError.InvalidParams("Invalid tools/list cursor."));

                var visibleTools = aiService.GetMcpToolDefinitions().Where(tool => McpToolPermissions.AllowsTool(token, tool)).ToArray();
                tokenManager.MarkUsed(token, toolListRead: true);
                return McpResponse.Success(request.Id, new { tools = visibleTools });

            case "tools/call":
                return await CallToolAsync(request, token);

            default:
                return McpResponse.Fail(request.Id, McpError.MethodNotFound());
        }
    }

    private async Task<McpResponse> CallToolAsync(McpRequest request, McpTokenRecord token)
    {
        var name = McpRequest.GetString(request.Params, "name");
        if (string.IsNullOrWhiteSpace(name))
            return McpResponse.Fail(request.Id, McpError.InvalidParams("'name' must be a non-empty string."));

        var tool = aiService.GetMcpToolDefinitions().Find(tool => tool.Name == name);
        if (tool == null)
            return McpResponse.Fail(request.Id, McpError.InvalidParams($"Unknown tool: {name}"));

        var arguments = McpRequest.EmptyObject;
        if (request.Params!.Value.TryGetProperty("arguments", out var suppliedArguments))
        {
            if (suppliedArguments.ValueKind != JsonValueKind.Object)
                return McpResponse.Fail(request.Id, McpError.InvalidParams("'arguments' must be an object."));

            arguments = suppliedArguments;
        }

        if (tool.ValidateArguments(arguments) is { } error)
            return McpResponse.Success(request.Id, McpToolResult.Error(error));

        if (!McpToolPermissions.AllowsTool(token, tool) || !McpToolPermissions.AllowsArguments(token, name, arguments))
            return McpResponse.Success(request.Id, McpToolResult.Error("当前令牌无权调用此工具或访问目标实例。"));

        if (!tokenManager.TryUseTool(token))
            return McpResponse.Fail(request.Id, new McpError { Code = -32000, Message = "MCP tool call limit exceeded or token is no longer active." });

        if (name == "query_operation_status")
        {
            var operation = aiService.GetMcpApproval(McpRequest.GetString(arguments, "operation_id") ?? "", token.Id);
            if (operation == null)
                return McpResponse.Success(request.Id, McpToolResult.Error("找不到此令牌提交的操作。"));
            var result = McpToolResult.Success(JsonSerializer.Serialize(operation));
            result.StructuredContent = operation;
            return McpResponse.Success(request.Id, result);
        }

        var output = await aiService.ExecuteMcpToolAsync(name, NormalizeTargetIds(tool, arguments), token.Id);
        return McpResponse.Success(request.Id, output.StartsWith("[ERROR]", StringComparison.Ordinal)
            ? McpToolResult.Error(output)
            : McpToolResult.Success(output));
    }

    private static string NormalizeTargetIds(McpToolDefinition tool, JsonElement arguments)
    {
        var json = arguments.GetRawText();
        if (!tool.InputSchema.TryGetProperty("properties", out var properties))
            return json;

        JsonObject? normalized = null;
        foreach (var idName in McpToolDefinition.TargetIdFields)
        {
            if (properties.TryGetProperty(idName, out _) && arguments.TryGetProperty(idName, out var id)
                && !id.TryGetInt32(out _))
            {
                // Existing tool handlers parse IDs as Int32 text. Preserve equivalent JSON numbers such as 1.0.
                normalized ??= JsonNode.Parse(json)!.AsObject();
                normalized[idName] = (int)id.GetDecimal();
            }
        }
        return normalized?.ToJsonString() ?? json;
    }

    private bool IsAllowedOrigin()
    {
        if (!Request.Headers.TryGetValue("Origin", out var origins))
            return true;

        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
            || origin.Scheme is not ("http" or "https") || origins[0] != origin.GetLeftPart(UriPartial.Authority))
            return false;

        var localOrigin = $"{Request.Scheme}://{Request.Host}";
        return (origin.IsLoopback && string.Equals(origins[0], localOrigin, StringComparison.OrdinalIgnoreCase))
            || configuration.GetSection("MslxMcp:AllowedOrigins").GetChildren()
                .Any(entry => string.Equals(entry.Value, origins[0], StringComparison.OrdinalIgnoreCase));
    }

    private string? GetCredential(bool allowQueryToken)
    {
        if (Request.Headers.TryGetValue("Authorization", out var values))
        {
            var value = values.Count == 1 ? values.ToString() : string.Empty;
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return value["Bearer ".Length..].Trim();

            return string.Empty;
        }

        return allowQueryToken ? Request.Query["token"].FirstOrDefault() : null;
    }

    private IActionResult UnauthorizedMcp()
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return Unauthorized();
    }

    private static bool SupportsVersion(string version, bool legacyRoute, bool legacySse) =>
        version == LegacyProtocolVersion ? legacyRoute
            : !legacySse && version is PreferredProtocolVersion or "2025-06-18";

    private static ContentResult WriteJson(McpResponse response, int statusCode = StatusCodes.Status200OK) =>
        new() { Content = JsonSerializer.Serialize(response), ContentType = "application/json; charset=utf-8", StatusCode = statusCode };

    private async Task WriteSseAsync(string payload, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(payload, cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private sealed record LegacySession(McpTokenRecord Token, Channel<string> Messages);
}
