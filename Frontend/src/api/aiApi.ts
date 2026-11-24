import request from 'mslx-request';

export interface AiSettingsModel {
  aiEnabled: boolean;
  aiApiKey: string;
  aiBaseUrl: string;
  aiModelName: string;
}

const SETTINGS_URL = '/api/plugin/mslx-plugins-ai-agents/settings';
const MCP_TOKENS_URL = '/api/plugin/mslx-plugins-ai-agents/settings/mcp/tokens';
const CHAT_URL = '/api/plugin/mslx-plugins-ai-agents/ai/chat';
const CONFIRM_TOOL_URL = '/api/plugin/mslx-plugins-ai-agents/ai/confirm-tool';
const HUB_URL = '/api/hubs/plugin/mslx-plugins-ai-agents/chat';

export interface McpTokenModel {
  id: string;
  name: string;
  token: string;
  maskedToken?: string;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt?: string | null;
  isRevoked?: boolean;
}

export interface CreateMcpTokenRequest {
  name: string;
  expireDays?: number | null;
  expiresInDays?: number | null;
}

const TOKEN_NAME = 'mslx-web-token';
const BASE_URL_NAME = 'mslx-base-url';

function getHttp() {
  try {
    const nodeContext = (window as any).MSLX_NodeContext?.use?.();
    if (nodeContext?.request) return nodeContext.request;
  } catch {
    // outside node context
  }
  return request || (window as any).mslxRequest;
}

function getToken(): string {
  return localStorage.getItem(TOKEN_NAME) || localStorage.getItem('token') || '';
}

function getBaseUrl(): string {
  return localStorage.getItem(BASE_URL_NAME) || '';
}

export async function getAiSettings(): Promise<AiSettingsModel> {
  const http = getHttp();
  if (http?.get) {
    return await http.get({ url: SETTINGS_URL });
  }
  const token = getToken();
  const res = await fetch(`${getBaseUrl()}${SETTINGS_URL}`, {
    headers: {
      'Content-Type': 'application/json',
      'x-user-token': token,
      Authorization: token ? `Bearer ${token}` : '',
    },
  });
  const json = await res.json();
  return json.data ?? json;
}

export async function updateAiSettings(data: AiSettingsModel): Promise<any> {
  const http = getHttp();
  if (http?.post) {
    return await http.post({ url: SETTINGS_URL, data });
  }
  const token = getToken();
  const res = await fetch(`${getBaseUrl()}${SETTINGS_URL}`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'x-user-token': token,
      Authorization: token ? `Bearer ${token}` : '',
    },
    body: JSON.stringify(data),
  });
  const json = await res.json();
  return json.data ?? json;
}

async function fetchMcpSettings(url: string, method: 'GET' | 'POST' | 'DELETE', data?: CreateMcpTokenRequest) {
  const token = getToken();
  const res = await fetch(`${getBaseUrl()}${url}`, {
    method,
    headers: {
      'Content-Type': 'application/json',
      'x-user-token': token,
      Authorization: token ? `Bearer ${token}` : '',
    },
    body: data ? JSON.stringify(data) : undefined,
  });
  const json = await res.json().catch(() => null);
  if (!res.ok || !json) throw new Error(json?.message || `令牌请求失败（HTTP ${res.status}）`);
  return json;
}

function unwrapMcpSettings(result: any) {
  if (typeof result?.code === 'number' && result.code !== 200) {
    throw new Error(result.message || '令牌请求失败');
  }
  return result?.data ?? result;
}

export async function getMcpTokens(): Promise<McpTokenModel[]> {
  const http = getHttp();
  const res = http?.get
    ? await http.get({ url: MCP_TOKENS_URL })
    : await fetchMcpSettings(MCP_TOKENS_URL, 'GET');
  return unwrapMcpSettings(res);
}

export async function createMcpToken(data: CreateMcpTokenRequest): Promise<McpTokenModel> {
  const http = getHttp();
  const payload = {
    name: data.name,
    expireDays: data.expireDays ?? data.expiresInDays,
    expiresInDays: data.expiresInDays ?? data.expireDays,
  };
  const res = http?.post
    ? await http.post({ url: MCP_TOKENS_URL, data: payload })
    : await fetchMcpSettings(MCP_TOKENS_URL, 'POST', payload);
  return unwrapMcpSettings(res);
}

export async function revokeMcpToken(id: string): Promise<void> {
  const http = getHttp();
  const url = `${MCP_TOKENS_URL}/${encodeURIComponent(id)}`;
  const res = http?.delete
    ? await http.delete({ url })
    : await fetchMcpSettings(url, 'DELETE');
  unwrapMcpSettings(res);
}

export function getMcpEndpointUrls() {
  const rawBase = getBaseUrl() || (typeof window !== 'undefined' ? window.location.origin : 'http://localhost:25333');
  const base = `${rawBase.replace(/\/+$/, '')}/api/plugin/mslx-plugins-ai-agents/mcp`;
  return {
    streamableHttpUrl: base,
    sseUrl: `${base}/sse`,
    skillMdUrl: `${base}/skill.md`,
  };
}

export interface ChatMessage {
  role: 'user' | 'assistant' | 'system';
  content: string;
}

let aiChatSignalRConn: any = null;
let activeHttpAbortController: AbortController | null = null;

export function abortAiChatStream() {
  if (activeHttpAbortController) {
    activeHttpAbortController.abort();
    activeHttpAbortController = null;
  }
  if (aiChatSignalRConn) {
    try {
      aiChatSignalRConn.stop();
    } catch (e) {
      console.warn('停止 SignalR 连接:', e);
    }
  }
}

async function getOrCreateSignalRConnection(): Promise<any> {
  if (aiChatSignalRConn) {
    if (aiChatSignalRConn.state === 'Disconnected') {
      try {
        await aiChatSignalRConn.start();
        return aiChatSignalRConn;
      } catch (e) {
        console.warn('重启已有 SignalR 连接失败，重新创建:', e);
        aiChatSignalRConn = null;
      }
    } else {
      return aiChatSignalRConn;
    }
  }

  // 1. 优先使用 (window as any).MSLX_NodeContext.use()?.createHub
  try {
    const nodeContext = (window as any).MSLX_NodeContext?.use?.();
    if (nodeContext?.createHub) {
      aiChatSignalRConn = nodeContext.createHub(HUB_URL);
      if (aiChatSignalRConn.state === 'Disconnected') {
        await aiChatSignalRConn.start();
      }
      return aiChatSignalRConn;
    }
  } catch (e) {
    console.warn('使用 MSLX_NodeContext.createHub 失败:', e);
  }

  // 2. 回退到自建 HubConnectionBuilder
  const token = getToken();
  const signalRLib = (window as any).signalR;
  if (signalRLib?.HubConnectionBuilder) {
    const fullHubUrl = `${getBaseUrl()}${HUB_URL}`;
    aiChatSignalRConn = new signalRLib.HubConnectionBuilder()
      .withUrl(fullHubUrl, {
        accessTokenFactory: () => token,
      })
      .withAutomaticReconnect()
      .build();

    if (aiChatSignalRConn.state === 'Disconnected') {
      await aiChatSignalRConn.start();
    }
    return aiChatSignalRConn;
  }

  return null;
}

export async function sendAiChatStream(
  messages: ChatMessage[],
  onMessage: (content: string) => void,
  onTool: (toolName: string, data: any) => void,
  onError: (error: string) => void,
  onDone: () => void
) {
  try {
    const conn = await getOrCreateSignalRConnection();
    if (!conn) {
      throw new Error('SignalR 客户端不可用，转入 HTTP SSE');
    }

    conn.off('ChatChunk');
    conn.off('ToolExecuted');
    conn.off('ChatError');
    conn.off('ChatComplete');

    conn.on('ChatChunk', (chunk: string) => {
      onMessage(chunk);
    });

    conn.on('ToolExecuted', (toolName: string, data: any) => {
      onTool(toolName, data);
    });

    conn.on('ChatError', (err: string) => {
      onError(err);
      onDone();
    });

    conn.on('ChatComplete', () => {
      onDone();
    });

    await conn.invoke('SendMessage', messages);
  } catch (wsErr: any) {
    console.warn('SignalR AI 对话尝试失败，回退至 HTTP SSE:', wsErr);
    await sendAiChatStreamHttp(messages, onMessage, onTool, onError, onDone);
  }
}

async function sendAiChatStreamHttp(
  messages: ChatMessage[],
  onMessage: (content: string) => void,
  onTool: (toolName: string, data: any) => void,
  onError: (error: string) => void,
  onDone: () => void
) {
  const token = getToken();
  const baseUrl = getBaseUrl();
  const url = `${baseUrl}${CHAT_URL}`;

  activeHttpAbortController = new AbortController();

  try {
    const response = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'x-user-token': token,
        Authorization: token ? `Bearer ${token}` : '',
      },
      signal: activeHttpAbortController.signal,
      body: JSON.stringify({ messages }),
    });

    if (!response.ok) {
      const errText = await response.text();
      onError(`请求失败 (${response.status}): ${errText}`);
      onDone();
      return;
    }

    const reader = response.body?.getReader();
    const decoder = new TextDecoder('utf-8');
    if (!reader) {
      onError('无法读取响应流');
      onDone();
      return;
    }

    let buffer = '';
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const events = buffer.split('\n\n');
      buffer = events.pop() || '';

      for (const evt of events) {
        if (!evt.trim()) continue;
        const lines = evt.split('\n');
        let eventType = 'message';
        let eventData = '';

        for (const line of lines) {
          if (line.startsWith('event:')) {
            eventType = line.substring(6).trim();
          } else if (line.startsWith('data:')) {
            eventData = line.substring(5).trim();
          }
        }

        const unescapedData = eventData.replace(/\\n/g, '\n');

        if (eventType === 'message') {
          onMessage(unescapedData);
        } else if (eventType === 'tool_executed') {
          try {
            const parsed = JSON.parse(unescapedData);
            onTool(parsed.tool, parsed.data);
          } catch (e) {
            onTool('unknown', unescapedData);
          }
        } else if (eventType === 'error') {
          onError(unescapedData);
        } else if (eventType === 'done') {
          onDone();
          return;
        }
      }
    }

    onDone();
  } catch (err: any) {
    if (err.name !== 'AbortError') {
      onError(err.message || '网络连接失败');
    }
    onDone();
  } finally {
    activeHttpAbortController = null;
  }
}

export interface ConfirmToolResult {
  success: boolean;
  message: string;
  tool?: string;
  data?: any;
}

export async function confirmAiToolAction(
  confirmationId: string,
  approved: boolean
): Promise<ConfirmToolResult> {
  if (aiChatSignalRConn && aiChatSignalRConn.state === 'Connected') {
    try {
      const result = await aiChatSignalRConn.invoke('ConfirmToolAction', confirmationId, approved);
      if (result && result.success) {
        return result;
      }
      console.warn('SignalR 确认未成功，回退 HTTP:', result);
    } catch (e) {
      console.warn('SignalR 敏感操作确认失败，回退 HTTP:', e);
    }
  }

  const token = getToken();
  const baseUrl = getBaseUrl();
  const url = `${baseUrl}${CONFIRM_TOOL_URL}`;

  const response = await fetch(url, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'x-user-token': token,
      Authorization: token ? `Bearer ${token}` : '',
    },
    body: JSON.stringify({ confirmationId, approved }),
  });

  const json = await response.json().catch(() => null);
  if (!response.ok || !json || (json.code !== 200 && json.code !== 0 && !json.success)) {
    throw new Error(json?.message || `请求失败 (${response.status})`);
  }
  return json.data ?? json;
}
