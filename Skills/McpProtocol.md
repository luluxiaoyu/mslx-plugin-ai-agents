# MSLX MCP 接入说明

## Streamable HTTP

将客户端的 MCP 服务地址设为 `http://localhost:25333/api/plugin/mslx-plugin-ai-agents/mcp`，传输选择 Streamable HTTP，并配置请求头 `Authorization: Bearer <MCP_TOKEN>`。把示例主机和端口替换为实际连接的 MSLX 地址。

```json
{
  "mcpServers": {
    "mslx": {
      "url": "http://localhost:25333/api/plugin/mslx-plugin-ai-agents/mcp",
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

协议版本为 `2024-11-05`。将仅支持 SSE 的客户端指向 `/api/plugin/mslx-plugin-ai-agents/mcp/sse`，并优先使用相同的 Bearer 请求头。仅支持 URL 参数的旧客户端可在 SSE 地址附加 `?token=<MCP_TOKEN>`。

SSE 建立后，首条 `endpoint` 事件提供实际的 POST 地址 `/api/plugin/mslx-plugin-ai-agents/mcp/message?sessionId=…`。客户端向该地址提交 JSON-RPC 消息，POST 返回空的 HTTP 202，结果通过同一 SSE 连接的 `message` 事件送达。会话随连接结束而失效。已有脚本可以继续携带令牌直接向 `/mcp/message` POST 并读取 JSON 响应。

## 文件上传（核心 / 整合包 / 实例文件 / 图片 / 插件 / 证书）

除使用 `stage_upload_file`（支持 `content_text`、`content_base64`、`local_path`）或直接向 `upload_server_file` 等工具传入内容外，大文件可通过 HTTP 上传端点暂存并获取 `uploadId`（即 `fileKey`）：

- **上传文件**：`POST /api/plugin/mslx-plugin-ai-agents/mcp/uploads`
  - 请求头：`Authorization: Bearer <MCP_TOKEN>`
  - 支持 `multipart/form-data`（表单字段 `file`，可选 `filename`）或原始二进制请求体（通过 `?filename=...` 或 `X-File-Name` 指定文件名）。
  - 返回 `{ "uploadId": "...", "fileKey": "...", "fileName": "...", "sizeBytes": 12345, "sha256": "..." }`。
- **检查整合包内容**：`GET /api/plugin/mslx-plugin-ai-agents/mcp/uploads/{uploadId}/inspect`（或调用 MCP 工具 `inspect_uploaded_archive`），返回是否为 `.mrpack` 及包内候选核心 `.jar` 列表。
- **删除未使用的暂存文件**：`DELETE /api/plugin/mslx-plugin-ai-agents/mcp/uploads/{uploadId}`。
- **后续使用方式**：
  - 创建服务器上传核心/整合包：`create_mc_server(core_file_key=...)` 或 `create_mc_server(package_file_key=..., core_filename=...)`
  - 替换实例核心：`update_instance_settings(server_id=..., core_file_key=...)`
  - 上传实例文件 / Mod / 插件 / `server-icon.png`：`upload_server_file(server_id=..., file_name=..., dir_path=..., upload_id=... / content_base64=... / content_text=...)`
  - 上传面板静态背景图：`upload_panel_image(file_name=..., upload_id=..., apply_to="light"|"dark"|"both"|"none")`
  - 上传安装本地 MSLX 插件：`upload_mslx_plugin(upload_id=..., overwrite=..., enabled=...)`
  - 上传面板 SSL 证书与私钥文本：`update_panel_ssl(enable_ssl=true, certificate_text=..., private_key_text=...)`

只读令牌禁止调用任何上传端点与写入工具。

## 实例终端命令行（run_server_terminal / request_terminal_permission）

- `run_server_terminal(server_id=..., command=..., shell="auto", working_dir=..., timeout_seconds=30)`：自动切换到目标服务器实例目录后执行命令行操作（Windows 默认使用 PowerShell，macOS 默认使用 `zsh`，Linux 默认使用 `bash`）。
- **权限规则**：MCP 面板上的「默认开启终端权限」默认关闭。
  - 开启时：具备写权限的 MCP 令牌可直接调用 `run_server_terminal` 执行命令，无需审批。
  - 关闭时（默认）：AI 必须先调用 `request_terminal_permission(server_id=..., duration_hours=..., duration_minutes=..., reason="...")` 申请指定实例的临时终端权限并附带使用时间，提交后由管理员在 MCP 面板的「待确认操作」审批；外部 Agent 可通过 `query_operation_status` 查询审批状态。审批通过后，在有效期内可直接对该实例调用 `run_server_terminal`。

## 后台任务查询（list_background_tasks / query_background_task / cancel_background_task）

- `list_background_tasks(server_id=..., task_type=..., state=..., keyword=..., limit=20)`（只读）：返回当前用户的后台任务列表（按创建时间倒序），包含 `id`、`instanceId`、`type`、`title`、`targetName`、`state`、`progress`、`message` 等字段；当备份、还原、压缩或建服接口未直接返回 `taskId` 时，可据此匹配任务进度。
- `query_background_task(task_id=...)`（只读）：根据任务 ID 查询单个后台任务实时进度与状态。
- `cancel_background_task(task_id=...)`（写入权限）：取消排队或运行中的后台任务。

## 令牌与来源

在 MSLX 的 AI 助手面板创建和吊销 MCP 令牌。令牌过期、吊销或删除后也无法通过既有 SSE 会话继续调用。每个令牌每分钟可执行 60 次工具调用。

桌面客户端通常不发送 `Origin`。浏览器可以从与 MCP 地址同源的本地回环地址访问。对其他受信任的浏览器来源，在宿主配置的 `MslxMcp:AllowedOrigins` 数组加入完整 Origin，例如通过环境变量配置 `MslxMcp__AllowedOrigins__0=https://panel.example.com`。

`GET /api/plugin/mslx-plugin-ai-agents/mcp/skill.md` 提供运维指南的 Markdown。客户端当前工具列表和参数以 `tools/list` 为准。
