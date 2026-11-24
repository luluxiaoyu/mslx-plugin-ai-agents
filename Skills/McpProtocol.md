# MSLX MCP 接入说明

## Streamable HTTP

将客户端的 MCP 服务地址设为 `http://localhost:25333/api/plugin/mslx-plugins-ai-agents/mcp`，传输选择 Streamable HTTP，并配置请求头 `Authorization: Bearer <MCP_TOKEN>`。把示例主机和端口替换为实际连接的 MSLX 地址。

```json
{
  "mcpServers": {
    "mslx": {
      "url": "http://localhost:25333/api/plugin/mslx-plugins-ai-agents/mcp",
      "headers": {
        "Authorization": "Bearer <MCP_TOKEN>"
      }
    }
  }
}
```

客户端使用 `initialize` 协商协议版本。此端点支持 `2025-11-25` 和 `2025-06-18`，使用无状态的 JSON 响应。每次 POST 发送一条 JSON-RPC 消息，并附加 `Content-Type: application/json` 和 `Accept: application/json, text/event-stream`。初始化后的请求应附加协商出的 `MCP-Protocol-Version`。初始化通知获得空的 HTTP 202 响应。GET 和 DELETE 返回 HTTP 405。

通过 `tools/list` 获取工具及其 `inputSchema`。执行工具时使用列表中给出的名称和参数，例如调用只读的备份列表工具。

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "list_server_backups",
    "arguments": {}
  }
}
```

## 旧版 HTTP + SSE

协议版本为 `2024-11-05`。将仅支持 SSE 的客户端指向 `/api/plugin/mslx-plugins-ai-agents/mcp/sse`，并优先使用相同的 Bearer 请求头。仅支持 URL 参数的旧客户端可在 SSE 地址附加 `?token=<MCP_TOKEN>`。

SSE 建立后，首条 `endpoint` 事件提供实际的 POST 地址 `/api/plugin/mslx-plugins-ai-agents/mcp/message?sessionId=…`。客户端向该地址提交 JSON-RPC 消息，POST 返回空的 HTTP 202，结果通过同一 SSE 连接的 `message` 事件送达。会话随连接结束而失效。已有脚本可以继续携带令牌直接向 `/mcp/message` POST 并读取 JSON 响应。

## 令牌与来源

在 MSLX 的 AI 助手面板创建和吊销 MCP 令牌。令牌过期、吊销或删除后也无法通过既有 SSE 会话继续调用。每个令牌每分钟可执行 60 次工具调用。

桌面客户端通常不发送 `Origin`。浏览器可以从与 MCP 地址同源的本地回环地址访问。对其他受信任的浏览器来源，在宿主配置的 `MslxMcp:AllowedOrigins` 数组加入完整 Origin，例如通过环境变量配置 `MslxMcp__AllowedOrigins__0=https://panel.example.com`。

`GET /api/plugin/mslx-plugins-ai-agents/mcp/skill.md` 提供运维指南的 Markdown。客户端当前工具列表和参数以 `tools/list` 为准。
