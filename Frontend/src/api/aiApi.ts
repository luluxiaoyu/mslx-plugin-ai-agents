import request from 'mslx-request';

export interface AiSettingsModel {
  aiEnabled: boolean;
  aiApiKey: string;
  aiBaseUrl: string;
  aiModelName: string;
  aiContextLength?: string;
  aiContextTokens?: number;
  basePromptTokens?: number;
}

export interface AiContextStatus {
  usedTokens: number;
  maxTokens: number;
  maxLabel: string;
  usagePercent: number;
  isEstimated: boolean;
  compressed: boolean;
  compressedBeforeTokens?: number | null;
  compressedAfterTokens?: number | null;
  summary?: string | null;
  archivedSourceCount: number;
  compressionThresholdTokens: number;
}

export interface McpTokenModel {
  id: string;
  name: string;
  token: string;
  maskedToken?: string;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt?: string | null;
  connectedAt?: string | null;
  readOnly?: boolean;
  serverIds?: number[] | null;
  isRevoked?: boolean;
}

export interface CreateMcpTokenRequest {
  name: string;
  expireDays: number;
  readOnly?: boolean;
  serverIds?: number[] | null;
}

export interface ToolApproval {
  id: string;
  tool: string;
  serverId: number;
  serverName: string;
  target: string;
  action: string;
  source: 'mcp' | 'chat';
  requester: string;
  approver?: string;
  status: string;
  createdAt: string;
  expiresAt: string;
}

export interface ChatMessage {
  role: 'user' | 'assistant' | 'system';
  content: string;
}

export interface ChatTurnStatus {
  status: string;
  tools: { tool: string; status: string }[];
}

const PLUGIN_URL = '/api/plugin/mslx-plugin-ai-agents';
const SETTINGS_URL = `${PLUGIN_URL}/settings`;
const MCP_TOKENS_URL = `${SETTINGS_URL}/mcp/tokens`;
const CHAT_URL = `${PLUGIN_URL}/ai/chat`;
const HUB_URL = '/api/hubs/plugin/mslx-plugin-ai-agents/chat';
const TURN_URL = `${PLUGIN_URL}/ai/turns`;

export const statusLabels: Record<string, string> = {
  pending: '待确认', executing: '执行中', running: '执行中', succeeded: '已完成',
  failed: '执行失败', rejected: '已拒绝', expired: '已失效', cancelled: '已停止',
  unknown: '待核对', submitted: '已提交后台任务',
};

export function useAiApi() {
  const sdk = (window as any).MSLX_NodeContext;
  const node = sdk?.use?.();
  const http = node?.request ?? request;
  const nodeId = node?.nodeId || 'local';
  const stores = (window as any).MSLX_Stores;
  const account = stores?.useUserStore?.()?.userInfo;
  const nodeName = nodeId === 'local' ? '主节点'
    : stores?.useNodeStore?.()?.slaveNodes?.find((item: { nodeId: string }) => item.nodeId === nodeId)?.nodeName;
  let connection: any;
  let active: { id: string; controller: AbortController } | undefined;

  function getMcpEndpointUrls() {
    const target = node?.getTarget() ?? {
      baseUrl: localStorage.getItem('mslx-base-url') || window.location.origin,
    };
    const base = `${target.baseUrl.replace(/\/+$/, '')}${PLUGIN_URL}/mcp`;
    return {
      streamableHttpUrl: base,
      sseUrl: `${base}/sse`,
      skillMdUrl: `${base}/skill.md`,
      nodeName: target.name || nodeName || new URL(target.baseUrl).host,
    };
  }

  async function getConnection() {
    if (!node?.createHub) return null;
    connection ??= node.createHub(HUB_URL);
    if (connection.state === 'Disconnected') await connection.start();
    return connection.state === 'Connected' ? connection : null;
  }

  const getTurnStatus = (id: string): Promise<ChatTurnStatus> => http.get({
    url: `${TURN_URL}/${encodeURIComponent(id)}`,
  });

  async function abortAiChatStream() {
    const turn = active;
    if (!turn) return;
    turn.controller.abort();
    await http.post({ url: `${TURN_URL}/${turn.id}/stop` });
  }

  async function sendAiChatStream(
    id: string,
    messages: ChatMessage[],
    onMessage: (content: string) => void,
    onTool: (tool: string, data: any) => void,
    onError: (error: string) => void,
    onDone: () => void,
    onStatus: (status: ChatTurnStatus) => void,
    onContextStatus?: (status: AiContextStatus) => void,
  ) {
    const controller = new AbortController();
    active = { id, controller };
    let hub: any;
    const chunkHandler = (chunk: string, turnId: string) => {
      if (turnId === id && !controller.signal.aborted) onMessage(chunk);
    };
    const toolHandler = (tool: string, data: any, turnId: string) => {
      if (turnId === id && !controller.signal.aborted) onTool(tool, data);
    };
    const errorHandler = (error: string, turnId: string) => {
      if (turnId === id && !controller.signal.aborted) onError(error);
    };
    const contextHandler = (contextStatus: AiContextStatus, turnId: string) => {
      if (turnId === id && !controller.signal.aborted) onContextStatus?.(contextStatus);
    };
    try {
      try { hub = await getConnection(); } catch { /* HTTP uses the same node context. */ }
      if (controller.signal.aborted) return;
      if (!hub) {
        await streamHttp();
        return;
      }

      hub.on('ChatChunk', chunkHandler);
      hub.on('ToolExecuted', toolHandler);
      hub.on('ChatError', errorHandler);
      hub.on('ContextStatus', contextHandler);
      await hub.invoke('StartChatTurn', id, messages);
    } catch {
      if (!controller.signal.aborted) {
        const status = await getTurnStatus(id).catch(() => ({ status: 'unknown', tools: [] }));
        onStatus(status);
        onError(`连接已中断。本轮状态：${statusLabels[status.status] ?? status.status}。可刷新执行状态。`);
      }
    } finally {
      if (hub) {
        hub.off('ChatChunk', chunkHandler);
        hub.off('ToolExecuted', toolHandler);
        hub.off('ChatError', errorHandler);
        hub.off('ContextStatus', contextHandler);
      }
      if (active?.id === id) active = undefined;
      onDone();
    }

    async function streamHttp() {
      const stream: ReadableStream<Uint8Array> = await http.post({
        url: CHAT_URL,
        data: { turnId: id, messages },
        adapter: 'fetch',
        responseType: 'stream',
        signal: controller.signal,
      }, { isTransformResponse: false });
      const reader = stream.getReader();
      const decoder = new TextDecoder();
      let pending = '';
      try {
        while (true) {
          const { done, value } = await reader.read();
          if (done) throw new Error('聊天响应提前结束');
          pending += decoder.decode(value, { stream: true });
          const events = pending.split(/\r?\n\r?\n/);
          pending = events.pop() ?? '';
          for (const event of events) {
            const type = event.match(/^event: *([^\r\n]+)/m)?.[1];
            const data = event.match(/^data: *([^\r\n]*)/m)?.[1] ?? '';
            if (type === 'done') return;
            if (type === 'message') onMessage(data.replace(/\\n/g, '\n'));
            if (type === 'error') onError(data);
            if (type === 'tool_executed') {
              const result = JSON.parse(data);
              onTool(result.tool, result.data);
            }
            if (type === 'context_status') {
              const result: AiContextStatus = JSON.parse(data);
              onContextStatus?.(result);
            }
          }
        }
      } finally {
        await reader.cancel().catch(() => {});
        reader.releaseLock();
      }
    }
  }

  return {
    getMcpEndpointUrls,
    historyKey: account?.id || account?.name
      ? `mslx_ai_chat_history:${JSON.stringify([localStorage.getItem('mslx-base-url') || window.location.origin, nodeId, account.id || account.name])}`
      : undefined,
    getAiSettings: (): Promise<AiSettingsModel> => http.get({ url: SETTINGS_URL }),
    updateAiSettings: (data: AiSettingsModel) => http.post({ url: SETTINGS_URL, data }),
    testModel: (data: AiSettingsModel): Promise<{ message: string }> =>
      http.post({ url: `${SETTINGS_URL}/test-model`, data }),
    getInstances: (): Promise<{ id: number; name: string }[]> =>
      http.get({ url: `${SETTINGS_URL}/mcp/instances` }),
    getMcpTokens: (): Promise<McpTokenModel[]> => http.get({ url: MCP_TOKENS_URL }),
    createMcpToken: (data: CreateMcpTokenRequest): Promise<McpTokenModel> => http.post({ url: MCP_TOKENS_URL, data }),
    revokeMcpToken: (id: string, permanent = false) => http.delete({
      url: `${MCP_TOKENS_URL}/${encodeURIComponent(id)}`,
      params: permanent ? { permanent: true } : undefined,
    }),
    listApprovals: (): Promise<ToolApproval[]> => http.get({ url: `${PLUGIN_URL}/ai/operations` }),
    confirmAiToolAction: (confirmationId: string, approved: boolean):
      Promise<{ success: boolean; message: string; data?: ToolApproval }> =>
      http.post({ url: `${PLUGIN_URL}/ai/confirm-tool`, data: { confirmationId, approved } }),
    sendAiChatStream,
    abortAiChatStream,
    getTurnStatus,
  };
}
