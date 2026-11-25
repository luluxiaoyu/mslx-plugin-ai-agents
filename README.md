# MSLX AI 助手

支持面板内聊天和外部 MCP 客户端。两个入口都仅由 MSLX 管理员配置。

## 外部 AI 接入

1. 在 WebPanel 打开「AI 助手」，选择「连接外部 AI（MCP）」。
2. 选择当前节点和访问令牌，点「复制这段话」，贴到你的 AI 客户端。可在「高级设置」复制 JSON 配置。
3. 客户端读到工具列表后，刷新连接状态即可看到「已连接」。使用 `localhost` 或其他回环地址时，客户端须在该节点的机器上运行。

默认使用 Streamable HTTP。仅在旧客户端需要时选择 SSE。标准地址为 `http://localhost:25333/api/plugin/mslx-plugin-ai-agents/mcp`，请以面板给出的节点地址为准。

新令牌默认有当前节点的全部权限，请只交给信任的 AI 软件。「高级设置」可创建只读令牌或限制实例范围，也可吊销令牌。

## 确认删除与覆盖操作

AI 提交高危操作后会给出操作编号。打开**同一节点**的 MCP 页，在「待确认操作」里核对目标，再选择「确认执行」或「拒绝」。审批无需模型 API Key。确认后由面板后端执行，AI 可通过 `query_operation_status` 查看结果。

## 面板聊天

在「面板内聊天」的模型设置中填写 API Key、Base URL 和模型名，点「测试模型连接」验证配置。高危操作会在回复内逐项显示，断线后可以查看本轮执行状态。

## 从旧版升级

插件 ID 已由 `mslx-plugins-ai-agents` 统一为 `mslx-plugin-ai-agents`。新目录缺少配置或令牌文件时会尝试逐个迁移，保留旧目录作为备份。外部客户端的旧 MCP 地址需要替换；原有令牌无需重建。

## 开发与打包

先在 `Frontend` 运行 `pnpm install` 和 `pnpm build`，再在项目根目录运行 `dotnet build -c Release`。Release 会嵌入前端和 Skill 文档，并合并插件依赖。
