<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { MessagePlugin } from 'tdesign-vue-next';
import { AddIcon, BrowseIcon, CopyIcon, RefreshIcon } from 'tdesign-icons-vue-next';
import { useAiApi, statusLabels, type McpTokenModel, type ToolApproval } from '../api/aiApi';

const { createMcpToken, getMcpEndpointUrls, getMcpTokens, revokeMcpToken, getInstances,
  listApprovals, confirmAiToolAction } = useAiApi();
const endpoints = getMcpEndpointUrls();
const permissionWarning = '拿到这把钥匙的人可以管理当前节点上的服务器，请只交给你信任的 AI 软件。';
const transport = ref<'http' | 'sse'>('http');
const endpoint = computed(() => transport.value === 'http' ? endpoints.streamableHttpUrl : endpoints.sseUrl);
const transportLabel = computed(() => transport.value === 'http' ? 'Streamable HTTP' : 'SSE');
const transportOptions = [
  { label: 'Streamable HTTP', value: 'http' },
  { label: 'SSE（旧版客户端）', value: 'sse' },
];
const isLocalEndpoint = computed(() => {
  try {
    const hostname = new URL(endpoint.value).hostname.replace(/\.$/, '');
    return hostname === 'localhost' || hostname.endsWith('.localhost') || hostname === '[::1]'
      || /^127(?:\.\d{1,3}){3}$/.test(hostname) || /^\[::ffff:7f[\da-f]{2}:[\da-f]{1,4}\]$/.test(hostname);
  } catch {
    return false;
  }
});
const clients = [
  { name: '腾讯 WorkBuddy', url: 'https://www.codebuddy.cn/work/' },
  { name: '千问 Go', url: 'https://www.qianwen.com/chat/workbench' },
  { name: '豆包电脑版', url: 'https://www.doubao.com/download/desktop' },
];
const presetText = '请帮我连上 MSLX。连好后，告诉我有哪些 Minecraft 服务器。先只查看，别改任何设置。';

const tokens = ref<McpTokenModel[]>([]);
const tokensLoading = ref(true);
const loadError = ref('');
const selectedTokenId = ref('');
const dateTime = new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium', timeStyle: 'short' });

const tokenStatus = (token: McpTokenModel) => {
  if (token.isRevoked) return { label: '已吊销', theme: 'danger' } as const;
  if (token.expiresAt && Date.parse(token.expiresAt) <= Date.now()) {
    return { label: '已过期', theme: 'warning' } as const;
  }
  return { label: '有效', theme: 'success' } as const;
};

const usableTokens = computed(() => tokens.value.filter((token) => token.token && tokenStatus(token).theme === 'success'));
const selectedToken = computed(() => usableTokens.value.find((token) => token.id === selectedTokenId.value));
const maskedToken = (token: McpTokenModel) => token.maskedToken || (token.token.length > 12
  ? `${token.token.slice(0, 8)}…${token.token.slice(-4)}`
  : '••••••••');
const tokenOptions = computed(() => usableTokens.value.map((token) => ({
  label: `${token.name} · ${maskedToken(token).slice(-4)}`,
  value: token.id,
})));
const connectedLabel = computed(() => selectedToken.value?.connectedAt ? '已连接，AI 已成功读取工具列表。' : '等待 AI 首次读取工具列表。');
const tokenPlaceholder = computed(() => tokensLoading.value ? '加载中…' : '暂无可用令牌');

function formatExpiration(expiresAt: string | null) {
  if (!expiresAt) return '永久有效';
  const timestamp = Date.parse(expiresAt);
  return Number.isFinite(timestamp) ? `${dateTime.format(timestamp)} 到期` : '到期时间未知';
}

function selectUsableToken() {
  if (!selectedToken.value) selectedTokenId.value = usableTokens.value[0]?.id ?? '';
}

async function loadTokens() {
  tokensLoading.value = true;
  loadError.value = '';
  try {
    const result = await getMcpTokens();
    if (!Array.isArray(result)) throw new Error('令牌列表响应无效');
    tokens.value = result;
    selectUsableToken();
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : '获取令牌失败';
  } finally {
    tokensLoading.value = false;
  }
}

function buildPromptDetails(token: string) {
  return `MCP 连接方式：${transportLabel.value}
服务地址：${endpoint.value}
请求头：Authorization: Bearer ${token}
${selectedToken.value?.serverIds ? `仅查询已授权的实例 ID：${selectedToken.value.serverIds.join('、')}` : ''}
运维指南：${endpoints.skillMdUrl}

如果你不能直接连接这个服务，请告诉我该在哪里添加，以及下一步怎么做。`;
}

function buildConfig(token: string) {
  return JSON.stringify({
    mcpServers: {
      'mslx-server-ops': {
        url: endpoint.value,
        headers: { Authorization: `Bearer ${token}` },
      },
    },
  }, null, 2);
}

const previewToken = computed(() => selectedToken.value ? maskedToken(selectedToken.value) : 'YOUR_MCP_TOKEN');
const configPreview = computed(() => buildConfig(previewToken.value));
const promptDetailsPreview = computed(() => buildPromptDetails(previewToken.value));

async function copyText(text: string, label: string) {
  if (!text) return false;
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
    } else {
      const previousFocus = document.activeElement;
      const field = document.createElement('textarea');
      field.value = text;
      field.readOnly = true;
      field.style.cssText = 'position: fixed; inset: 0 auto auto 0; opacity: 0;';
      document.body.append(field);
      try {
        field.select();
        if (!document.execCommand('copy')) throw new Error('Clipboard unavailable');
      } finally {
        field.remove();
        if (previousFocus instanceof HTMLElement) previousFocus.focus({ preventScroll: true });
      }
    }
    MessagePlugin.success(`已复制${label}`);
    return true;
  } catch {
    MessagePlugin.error('复制失败，请检查浏览器的剪贴板权限后重试');
    return false;
  }
}

function getCurrentToken() {
  const token = selectedToken.value;
  if (token && tokenStatus(token).theme !== 'success') {
    selectedTokenId.value = '';
    tokens.value = [...tokens.value];
    selectUsableToken();
    return selectedToken.value;
  }
  return token;
}

function copyConfig() {
  const token = getCurrentToken();
  if (token !== null) return copyText(buildConfig(token?.token || 'YOUR_MCP_TOKEN'), ' JSON 配置');
}

const createOpen = ref(false);
const creating = ref(false);
const quickError = ref('');
const promptCopied = ref(false);
const createdToken = ref<McpTokenModel | null>(null);
const nameInput = ref<HTMLInputElement | null>(null);
const createHeading = ref<HTMLElement | null>(null);
const revokeHeading = ref<HTMLElement | null>(null);
const tokensHeading = ref<HTMLElement | null>(null);
const name = ref('');
const expireDays = ref(30);
const expirationOptions = [
  { label: '7 天', value: 7 },
  { label: '30 天', value: 30 },
  { label: '90 天', value: 90 },
  { label: '180 天', value: 180 },
  { label: '永久有效', value: 0 },
];
const readOnly = ref(false);
const restrictInstances = ref(false);
const serverIds = ref<number[]>([]);
const instanceOptions = ref<{ label: string; value: number }[]>([]);
const loadingInstances = ref(false);
watch(restrictInstances, async (restricted) => {
  if (!restricted) return;
  loadingInstances.value = true;
  try {
    instanceOptions.value = (await getInstances()).map(({ id, name }) => ({ value: id, label: `#${id} ${name}` }));
  } catch {
    createError.value = '无法读取实例列表，请取消并重新开启实例限制。';
  } finally {
    loadingInstances.value = false;
  }
});

const nameError = ref('');
const createError = ref('');

watch([selectedTokenId, transport], () => {
  promptCopied.value = false;
});

async function addToken(name: string, expireDays: number, readOnly = false, serverIds: number[] | null = null) {
  const token = await createMcpToken({ name, expireDays, readOnly, serverIds });
  if (!token?.id || !token.token) throw new Error('没有收到连接钥匙，请刷新后重试');
  tokens.value = [token, ...tokens.value.filter((item) => item.id !== token.id)];
  selectedTokenId.value = token.id;
  return token;
}

async function handleQuickConnect() {
  if (tokensLoading.value || creating.value) return;
  quickError.value = '';
  promptCopied.value = false;
  const token = getCurrentToken();
  if (token === null) return;

  if (token) {
    promptCopied.value = await copyText(`${presetText}\n\n${buildPromptDetails(token.token)}`, '这段话');
    return;
  }

  if (loadError.value) {
    await loadTokens();
    return;
  }

  creating.value = true;
  try {
    const created = await addToken('AI 助手接入', 30);
    promptCopied.value = await copyText(`${presetText}\n\n${buildPromptDetails(created.token)}`, '这段话');
    if (!promptCopied.value) quickError.value = '钥匙已准备好。请再点「复制这段话」。';
  } catch {
    quickError.value = '钥匙没生成成功，请再试一次。';
  } finally {
    creating.value = false;
  }
}

let dialogTrigger: HTMLElement | null = null;

function restoreDialogFocus() {
  const target = dialogTrigger?.isConnected ? dialogTrigger : tokensHeading.value;
  target?.focus({ preventScroll: true });
  dialogTrigger = null;
}

function keepDialogFocus(event: KeyboardEvent) {
  if (!(event.currentTarget instanceof HTMLElement)) return;
  const controls = Array.from(event.currentTarget.querySelectorAll<HTMLElement>(
    'button:not([disabled]), input:not([disabled]), a[href]',
  )).filter((element) => element.tabIndex >= 0 && element.getClientRects().length > 0);
  const focused = controls.indexOf(document.activeElement as HTMLElement);
  const boundary = event.shiftKey ? 0 : controls.length - 1;
  if (focused === -1 || focused === boundary) {
    event.preventDefault();
    (event.shiftKey ? controls.at(-1) : controls[0])?.focus();
  }
}

function openCreate(event: MouseEvent) {
  dialogTrigger = event.currentTarget as HTMLElement;
  name.value = '';
  expireDays.value = 30;
  readOnly.value = false;
  restrictInstances.value = false;
  serverIds.value = [];
  nameError.value = '';
  createError.value = '';
  createdToken.value = null;
  createOpen.value = true;
}

function submitNameWithEnter(event: KeyboardEvent) {
  if (event.isComposing) return;
  event.preventDefault();
  void createToken();
}

async function createToken() {
  if (creating.value || createdToken.value) return;
  const trimmedName = name.value.trim();
  if (!trimmedName) {
    nameError.value = '请输入令牌名称';
    await nextTick();
    nameInput.value?.focus();
    return;
  }

  creating.value = true;
  createError.value = '';
  try {
    if (restrictInstances.value && !serverIds.value.length) {
      createError.value = "请至少选择一个实例。";
      return;
    }
    createdToken.value = await addToken(trimmedName, expireDays.value, readOnly.value, restrictInstances.value ? serverIds.value : null);
    await nextTick();
    createHeading.value?.focus();
  } catch (error) {
    createError.value = error instanceof Error ? error.message : '创建失败，请重试';
  } finally {
    creating.value = false;
  }
}

const revokeOpen = ref(false);
const permanentDelete = ref(false);
const revokeTarget = ref<McpTokenModel | null>(null);
const revoking = ref(false);
const revokeError = ref('');

function openRevoke(token: McpTokenModel, event: MouseEvent, permanent = false) {
  permanentDelete.value = permanent;
  dialogTrigger = event.currentTarget as HTMLElement;
  revokeTarget.value = token;
  revokeError.value = '';
  revokeOpen.value = true;
}

async function revokeToken() {
  const token = revokeTarget.value;
  if (!token || revoking.value) return;

  revoking.value = true;
  revokeError.value = '';
  try {
    await revokeMcpToken(token.id, permanentDelete.value);
    tokens.value = permanentDelete.value ? tokens.value.filter((item) => item.id !== token.id)
      : tokens.value.map((item) => item.id === token.id ? { ...item, isRevoked: true } : item);
    selectUsableToken();
    revokeOpen.value = false;
    MessagePlugin.success(permanentDelete.value ? "已永久删除令牌。" : "已吊销令牌。");
  } catch (error) {
    revokeError.value = error instanceof Error ? error.message : '吊销失败，请重试';
  } finally {
    revoking.value = false;
  }
}

const approvals = ref<ToolApproval[]>([]);
const approvalsLoading = ref(false);
const approvalsError = ref('');
const activeDecision = ref('');
const pendingCount = computed(() => approvals.value.filter(operation => operation.status === 'pending').length);
const approvalLabels: Record<string, string> = {
  write_server_file: '覆盖文件', delete_server_file: '删除文件或目录', delete_server_instance: '删除实例',
  copy_server_file: '复制并覆盖目标', move_server_file: '移动并覆盖目标',
  download_file_to_server: '下载并覆盖文件', compress_files: '覆盖压缩包', extract_archive: '解压并覆盖目录内容',
  manage_mod_plugin_state: '删除模组或插件', restore_server_backup: '还原备份', delete_server_backup: '删除备份',
};
async function loadApprovals() {
  if (approvalsLoading.value) return;
  approvalsLoading.value = true;
  try {
    approvals.value = await listApprovals();
    approvalsError.value = '';
  } catch {
    approvalsError.value = '无法获取操作，请刷新重试。';
  } finally {
    approvalsLoading.value = false;
  }
}

async function decide(operation: ToolApproval, approved: boolean) {
  if (activeDecision.value) return;
  activeDecision.value = operation.id;
  try {
    const result = await confirmAiToolAction(operation.id, approved);
    if (result.success) MessagePlugin.success(approved ? '操作已执行。' : '已拒绝操作。');
    else MessagePlugin.error(result.message);
  } catch {
    MessagePlugin.error('无法获取执行结果，请刷新操作列表。');
  } finally {
    activeDecision.value = '';
    await loadApprovals();
  }
}

let approvalTimer: number | undefined;
onMounted(() => {
  void loadTokens();
  void loadApprovals();
  approvalTimer = window.setInterval(() => {
    if (document.visibilityState === 'visible') void loadApprovals();
  }, 10000);
});
onBeforeUnmount(() => window.clearInterval(approvalTimer));
</script>

<template>
  <div class="mcp-management">
    <section class="mcp-section" aria-labelledby="mcp-quick-heading">
      <div class="section-header">
        <div><h2 id="mcp-quick-heading">让 AI 帮你查看服务器</h2><p>当前节点 · {{ endpoints.nodeName }}</p></div>
      </div>
      <ol class="quick-steps">
        <li>
          <h3>打开你常用的 AI 工具</h3>
          <p>点击下方链接打开网页或下载客户端。</p>
          <div class="client-links">
            <a
              v-for="client in clients"
              :key="client.name"
              class="client-link"
              :href="client.url"
              target="_blank"
              rel="noopener noreferrer"
              :aria-label="`在新标签页打开${client.name}`"
            >
              {{ client.name }} <span aria-hidden="true">↗</span>
            </a>
          </div>
          <p v-if="isLocalEndpoint" id="mcp-local-address-hint" class="local-address-hint">
            现在用的是本机地址。请在运行 MSLX 的同一台电脑上打开 AI 工具。
          </p>
        </li>
        <li>
          <h3>复制这段话</h3>
          <div class="preset">
            <p>{{ presetText }}</p>
            <p v-if="!selectedToken?.readOnly" class="config-note">{{ permissionWarning }}</p>
            <details class="preview-details prompt-preview">
              <summary>查看完整内容</summary>
              <pre class="config-code" translate="no"><code>{{ promptDetailsPreview }}</code></pre>
              <p v-if="selectedToken" class="prompt-token-hint">复制时会填入所选令牌的完整值。</p>
            </details>
            <div v-if="selectedToken" class="preset-actions">
              <span class="config-note" role="status">{{ connectedLabel }}</span>
              <t-button variant="text" size="small" :loading="tokensLoading" @click="loadTokens">刷新连接状态</t-button>
            </div>
            <div class="preset-actions">
              <label class="mcp-field">
                <span>访问令牌</span>
                <t-select
                  v-model="selectedTokenId"
                  class="mcp-select"
                  :options="tokenOptions"
                  :placeholder="tokenPlaceholder"
                  :input-props="{ name: 'mcp-prompt-token' }"
                  :disabled="tokensLoading || creating || !usableTokens.length"
                />
              </label>
              <t-button
                theme="primary"
                :loading="tokensLoading || creating"
                :aria-describedby="isLocalEndpoint ? 'mcp-local-address-hint' : undefined"
                @click="handleQuickConnect"
              >
                <template #icon>
                  <CopyIcon v-if="selectedToken" aria-hidden="true" />
                  <AddIcon v-else-if="!loadError" aria-hidden="true" />
                  <RefreshIcon v-else aria-hidden="true" />
                </template>
                {{ tokensLoading ? '正在准备…' : selectedToken ? '复制这段话' : loadError ? '重新加载' : '生成钥匙并复制' }}
              </t-button>
            </div>
          </div>
          <p v-if="quickError" class="field-error" role="alert">{{ quickError }}</p>
          <p v-else-if="!tokensLoading && !selectedToken">
            {{ loadError ? '连接钥匙没加载出来，请点「重新加载」。' : '第一次使用，点一次就能生成钥匙并复制。' }}
          </p>
        </li>
        <li>
          <h3>粘贴到聊天框，再点发送</h3>
          <p role="status">{{ promptCopied ? '已复制，去你自己的 AI 对话里粘贴吧。' : '用你自己的 AI 对话发送，等它告诉你有没有连上。' }}</p>
        </li>
      </ol>
    </section>

    <details class="advanced-settings">
      <summary>待确认操作<span v-if="pendingCount"> · {{ pendingCount }} 项</span></summary>
      <section class="advanced-content" aria-label="待确认操作">
        <div class="section-header">
          <p>AI 申请删除或覆盖内容时，会在这里等你确认。</p>
          <t-button variant="outline" size="small" :loading="approvalsLoading" @click="loadApprovals">刷新</t-button>
        </div>
        <p v-if="approvalsError" class="field-error" role="alert">{{ approvalsError }}</p>
        <p v-else-if="!approvals.length" class="empty-state" role="status">暂时没有操作记录。</p>
        <ul v-else class="token-list">
          <li v-for="operation in approvals" :key="operation.id" class="token-row">
            <div class="token-details">
              <div class="token-heading">
                <strong>{{ approvalLabels[operation.tool] || operation.tool }}</strong>
                <t-tag size="small" variant="light" :theme="operation.status === 'pending' ? 'warning' : 'default'">
                  {{ statusLabels[operation.status] || operation.status }}
                </t-tag>
              </div>
              <div class="token-meta">
                <span>#{{ operation.serverId }} {{ operation.serverName }}</span>
                <span>{{ operation.target }}</span>
                <code :title="operation.id">编号 {{ operation.id }}</code>
                <span>{{ operation.source === 'mcp' ? operation.requester : '面板聊天' }}</span>
                <span v-if="operation.action === 'delete_data'">同时删除文件和存档</span>
                <span v-if="operation.action === 'remove_instance'">移除实例配置，保留文件</span>
                <span v-if="operation.approver">审批人 {{ operation.approver }}</span>
                <span v-if="operation.status === 'pending'">{{ formatExpiration(operation.expiresAt) }}</span>
              </div>
            </div>
            <div v-if="operation.status === 'pending'" class="token-actions">
              <t-button theme="danger" size="small" :disabled="!!activeDecision" @click="decide(operation, true)">确认执行</t-button>
              <t-button variant="outline" size="small" :disabled="!!activeDecision" @click="decide(operation, false)">拒绝</t-button>
            </div>
          </li>
        </ul>
      </section>
    </details>

    <details class="advanced-settings">
      <summary>高级设置</summary>
      <div class="advanced-content">
        <section class="mcp-section" aria-labelledby="mcp-connection-heading">
          <div class="section-header">
            <div>
              <h2 id="mcp-connection-heading">客户端配置</h2>
              <p>为外部 Agent 生成可复制的连接配置。</p>
            </div>
            <a class="guide-link" :href="endpoints.skillMdUrl" target="_blank" rel="noopener noreferrer" aria-label="在新标签页打开运维指南">
              <BrowseIcon aria-hidden="true" />
              运维指南
            </a>
          </div>

          <div class="connection-panel">
            <div class="connection-fields">
              <label class="mcp-field">
                <span>传输协议</span>
                <t-select
                  v-model="transport"
                  class="mcp-select"
                  :options="transportOptions"
                  :input-props="{ name: 'mcp-transport' }"
                />
              </label>
              <label class="mcp-field">
                <span>访问令牌</span>
                <t-select
                  v-model="selectedTokenId"
                  class="mcp-select"
                  :options="tokenOptions"
                  :placeholder="tokenPlaceholder"
                  :input-props="{ name: 'mcp-config-token' }"
                  :disabled="tokensLoading || creating || !usableTokens.length"
                />
              </label>
            </div>

            <div class="endpoint-row">
              <code translate="no">{{ endpoint }}</code>
              <t-button variant="text" shape="square" aria-label="复制服务地址" title="复制服务地址" @click="copyText(endpoint, '服务地址')">
                <template #icon><CopyIcon aria-hidden="true" /></template>
              </t-button>
            </div>

            <div class="preview">
              <div class="preview-toolbar">
                <span>JSON 配置</span>
                <t-button theme="primary" size="small" :disabled="tokensLoading" @click="copyConfig">
                  <template #icon><CopyIcon aria-hidden="true" /></template>
                  复制 JSON
                </t-button>
              </div>
              <details class="preview-details">
                <summary>预览配置</summary>
                <pre class="config-code" translate="no"><code>{{ configPreview }}</code></pre>
              </details>
            </div>

            <p v-if="!tokensLoading" class="config-note">
              {{ selectedToken ? '预览中隐藏令牌，复制时填入完整值。' : '创建有效令牌后，复制的配置会自动填入令牌。' }}
            </p>
          </div>
        </section>

        <section class="mcp-section" aria-labelledby="mcp-tokens-heading">
          <div class="section-header">
            <div>
              <h2 id="mcp-tokens-heading" ref="tokensHeading" tabindex="-1">访问令牌</h2>
              <p>{{ tokensLoading ? '加载中…' : `${usableTokens.length} 个可用 · 共 ${tokens.length} 个` }}</p>
            </div>
            <div class="section-actions">
              <t-button
                variant="outline"
                shape="square"
                :loading="tokensLoading"
                aria-label="刷新令牌"
                title="刷新令牌"
                @click="loadTokens"
              >
                <template #icon><RefreshIcon aria-hidden="true" /></template>
              </t-button>
              <t-button variant="outline" theme="primary" :disabled="tokensLoading || creating" @click="openCreate">
                <template #icon><AddIcon aria-hidden="true" /></template>
                新建令牌
              </t-button>
            </div>
          </div>

          <p v-if="loadError" class="empty-state" role="alert">加载失败。{{ loadError }}。请刷新重试。</p>
          <p v-else-if="!tokens.length" class="empty-state" role="status">
            {{ tokensLoading ? '正在读取令牌…' : '还没有访问令牌。新建一个令牌即可配置客户端。' }}
          </p>
          <ul v-if="tokens.length" class="token-list" :aria-busy="tokensLoading">
            <li v-for="token in tokens" :key="token.id" class="token-row">
              <div class="token-details">
                <div class="token-heading">
                  <span class="token-name">{{ token.name }}</span>
                  <t-tag :theme="tokenStatus(token).theme" variant="light" size="small">{{ tokenStatus(token).label }}</t-tag>
                </div>
                <div class="token-meta">
                  <code translate="no">{{ maskedToken(token) }}</code>
                  <span>{{ formatExpiration(token.expiresAt) }}</span>
                  <span>{{ token.readOnly ? '只读' : '全部权限' }}</span>
                  <span>{{ token.serverIds ? `实例 #${token.serverIds.join('、#')}` : '全部实例' }}</span>
                  <span>最近使用 {{ token.lastUsedAt ? dateTime.format(Date.parse(token.lastUsedAt)) : '暂无' }}</span>
                </div>
              </div>
              <div class="token-actions">
                <t-button
                  v-if="token.token && tokenStatus(token).theme === 'success'"
                  variant="text"
                  size="small"
                  :aria-label="`复制令牌 ${token.name}`"
                  @click="copyText(token.token, '令牌')"
                >
                  复制
                </t-button>
                <t-button
                  variant="text"
                  size="small"
                  theme="danger"
                  :disabled="tokensLoading"
                  v-if="tokenStatus(token).theme === 'success'"
                  :aria-label="`吊销令牌 ${token.name}`"
                  @click="openRevoke(token, $event)"
                >
                  吊销
                </t-button>
                <t-button v-else variant="text" theme="danger" size="small" @click="openRevoke(token, $event, true)">
                  永久删除
                </t-button>
              </div>
            </li>
          </ul>
        </section>
      </div>
    </details>

    <t-dialog
      v-model:visible="createOpen"
      attach="body"
      width="min(440px, 92vw)"
      class="mcp-token-dialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby="mcp-create-title"
      :close-on-overlay-click="false"
      :close-on-esc-keydown="!creating"
      :close-btn="false"
      :confirm-btn="createdToken ? '完成' : { content: '创建令牌', loading: creating }"
      :cancel-btn="createdToken || creating ? null : '取消'"
      @keydown.tab="keepDialogFocus"
      @opened="createHeading?.focus()"
      @confirm="createdToken ? createOpen = false : createToken()"
      @closed="createdToken = null; restoreDialogFocus()"
    >
      <template #header>
        <h2 id="mcp-create-title" ref="createHeading" class="mcp-dialog-title" tabindex="-1">
          {{ createdToken ? '令牌已创建' : '新建访问令牌' }}
        </h2>
      </template>
      <div v-if="createdToken" class="mcp-dialog-content">
        <p>已选用“{{ createdToken.name }}”。在客户端配置中复制时会带上这个令牌。</p>
        <div class="created-token">
          <code translate="no">{{ createdToken.token }}</code>
          <t-button variant="outline" size="small" @click="copyText(createdToken.token, '令牌')">
            <template #icon><CopyIcon aria-hidden="true" /></template>
            复制令牌
          </t-button>
        </div>
      </div>
      <form v-else class="mcp-dialog-content" novalidate @submit.prevent="createToken">
        <p v-if="!readOnly" class="config-note">{{ permissionWarning }}</p>
        <label class="mcp-field">
          <span>令牌名称</span>
          <input
            ref="nameInput"
            v-model="name"
            class="mcp-control"
            name="mcp-token-name"
            autocomplete="off"
            maxlength="30"
            placeholder="例如 Cursor 工作电脑…"
            required
            :disabled="creating"
            :aria-invalid="!!nameError"
            :aria-describedby="nameError ? 'mcp-name-error' : undefined"
            @input="nameError = ''"
            @keydown.enter="submitNameWithEnter"
          />
        </label>
        <p v-if="nameError" id="mcp-name-error" class="field-error" role="alert">{{ nameError }}</p>
        <label class="mcp-field">
          <span>有效期</span>
          <t-select
            v-model="expireDays"
            class="mcp-select"
            :options="expirationOptions"
            :input-props="{ name: 'mcp-token-expiration' }"
            :disabled="creating"
          />
        </label>
        <details>
          <summary>限制令牌权限</summary>
          <div class="mcp-dialog-content">
            <t-checkbox v-model="readOnly" :disabled="creating">只允许查看</t-checkbox>
            <t-checkbox v-model="restrictInstances" :disabled="creating">只允许访问选定实例</t-checkbox>
            <label v-if="restrictInstances" class="mcp-field">
              <span>可访问实例</span>
              <t-select v-model="serverIds" :options="instanceOptions" multiple :loading="loadingInstances" :disabled="creating" />
            </label>
          </div>
        </details>
        <p v-if="createError" class="field-error" role="alert">{{ createError }}</p>
      </form>
    </t-dialog>

    <t-dialog
      v-model:visible="revokeOpen"
      attach="body"
      width="min(440px, 92vw)"
      class="mcp-token-dialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby="mcp-revoke-title"
      :close-on-overlay-click="false"
      :close-on-esc-keydown="!revoking"
      :close-btn="false"
      :confirm-btn="{ content: permanentDelete ? '永久删除' : '吊销令牌', theme: 'danger', loading: revoking }"
      :cancel-btn="revoking ? null : '取消'"
      @keydown.tab="keepDialogFocus"
      @opened="revokeHeading?.focus()"
      @closed="restoreDialogFocus"
      @confirm="revokeToken"
    >
      <template #header>
        <h2 id="mcp-revoke-title" ref="revokeHeading" class="mcp-dialog-title" tabindex="-1">{{ permanentDelete ? "永久删除访问令牌" : "吊销访问令牌" }}</h2>
      </template>
      <div class="mcp-dialog-content">
        <p v-if="permanentDelete">从列表永久删除“{{ revokeTarget?.name }}”。</p>
        <p v-else>使用“{{ revokeTarget?.name }}”的客户端将立即失去 MCP 访问权限。</p>
        <p v-if="revokeError" class="field-error" role="alert">{{ revokeError }}</p>
      </div>
    </t-dialog>
  </div>
</template>

<style scoped>
.mcp-management {
  display: grid;
  gap: 24px;
  min-width: 0;
  padding-bottom: 8px;
  color: var(--td-text-color-primary);
}

.mcp-section {
  display: grid;
  gap: 12px;
  min-width: 0;
}

.quick-steps {
  display: grid;
  gap: 20px;
  margin: 3px 0 0;
  padding-inline-start: 23px;
}

.quick-steps > li {
  min-width: 0;
  padding-inline-start: 4px;
}

.quick-steps > li::marker {
  color: var(--td-brand-color);
  font-weight: 650;
}

.quick-steps h3 {
  margin: 0 0 5px;
  font-size: 15px;
  line-height: 1.4;
}

.quick-steps p {
  margin: 0;
  color: var(--td-text-color-secondary);
  font-size: 13px;
  line-height: 1.55;
}

.client-links {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}

.client-link {
  display: inline-flex;
  min-height: 32px;
  box-sizing: border-box;
  align-items: center;
  gap: 5px;
  padding: 5px 9px;
  border: 1px solid var(--td-component-border);
  border-radius: var(--td-radius-medium);
  color: var(--td-brand-color);
  font-size: 13px;
  text-decoration: none;
}

.client-link:hover {
  background: var(--td-bg-color-container-hover);
  text-decoration: underline;
}

.quick-steps .local-address-hint {
  margin-top: 8px;
}

.preset {
  display: grid;
  min-width: 0;
  gap: 12px;
  padding: 12px 13px;
  border: 1px solid var(--td-component-border);
  border-radius: 10px;
  background: var(--td-bg-color-secondarycontainer);
}

.preset p {
  color: var(--td-text-color-primary);
  font-size: 14px;
}

.prompt-preview {
  min-width: 0;
  border: 1px solid var(--td-component-border);
  border-radius: 8px;
}

.prompt-preview .config-code {
  background: var(--td-bg-color-container);
}

.preset .prompt-token-hint {
  padding: 8px 12px;
  color: var(--td-text-color-secondary);
  font-size: 12px;
}

.preset-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: end;
  gap: 8px 10px;
}

.preset-actions .mcp-field {
  flex: 1 1 160px;
}

.preset + p {
  margin-top: 6px;
}

.advanced-settings {
  min-width: 0;
  padding-top: 10px;
  border-top: 1px solid var(--td-component-border);
}

.advanced-settings > summary {
  width: fit-content;
  box-sizing: border-box;
  min-height: 32px;
  padding: 6px 4px;
  color: var(--td-text-color-secondary);
  font-size: 13px;
  cursor: pointer;
}

.advanced-settings > summary:hover {
  color: var(--td-text-color-primary);
}

.advanced-content {
  display: grid;
  gap: 20px;
  padding-top: 12px;
}

.section-header,
.section-actions,
.endpoint-row,
.preview-toolbar,
.token-heading,
.token-meta,
.token-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.section-header {
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 10px;
}

.section-header h2 {
  margin: 0;
  padding-inline-start: 10px;
  border-inline-start: 3px solid var(--td-brand-color);
  font-size: 16px;
  font-weight: 650;
  line-height: 1.25;
}

.section-header p {
  margin: 5px 0 0;
  color: var(--td-text-color-secondary);
  font-size: 13px;
}

.guide-link {
  display: inline-flex;
  min-height: 32px;
  align-items: center;
  gap: 6px;
  color: var(--td-brand-color);
  font-size: 13px;
  text-decoration: none;
}

.guide-link:hover {
  text-decoration: underline;
}

.connection-panel {
  min-width: 0;
  padding: 14px;
  border: 1px solid var(--td-component-border);
  border-radius: 12px;
}

.connection-fields {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}

.connection-fields > label {
  flex: 1 1 170px;
}

.mcp-field {
  display: grid;
  min-width: 0;
  gap: 6px;
  font-size: 13px;
  font-weight: 500;
}

.mcp-select {
  width: 100%;
  min-width: 0;
}

.mcp-control {
  box-sizing: border-box;
  width: 100%;
  min-width: 0;
  min-height: 34px;
  padding: 5px 8px;
  border: 1px solid var(--td-component-border);
  border-radius: var(--td-radius-medium);
  background: var(--td-bg-color-container);
  color: var(--td-text-color-primary);
  font: inherit;
}

.mcp-control:disabled {
  background: var(--td-bg-color-component-disabled);
  color: var(--td-text-color-disabled);
}

html[theme-mode="dark"] .mcp-control {
  color-scheme: dark;
}

.mcp-control:focus-visible,
.guide-link:focus-visible,
.client-link:focus-visible,
.advanced-settings > summary:focus-visible {
  outline: 2px solid var(--td-brand-color);
  outline-offset: 2px;
}

.mcp-control[aria-invalid="true"] {
  border-color: var(--td-error-color);
}

.endpoint-row {
  justify-content: space-between;
  margin-block: 8px 10px;
  padding-inline-start: 8px;
}

.endpoint-row code,
.token-meta code,
.created-token code {
  overflow-wrap: anywhere;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
}

.endpoint-row code {
  color: var(--td-text-color-secondary);
  font-size: 12px;
}

.endpoint-row .t-button {
  flex-shrink: 0;
}

.preview {
  overflow: hidden;
  border: 1px solid var(--td-component-border);
  border-radius: 9px;
}

.preview-toolbar {
  flex-wrap: wrap;
  justify-content: space-between;
  min-height: 42px;
  padding: 6px 8px;
  border-bottom: 1px solid var(--td-component-border);
}

.preview-toolbar > span {
  padding-inline-start: 4px;
  color: var(--td-text-color-primary);
  font-size: 13px;
  font-weight: 600;
}

.preview-details summary {
  padding: 7px 10px;
  color: var(--td-text-color-secondary);
  font-size: 12px;
  cursor: pointer;
}

.preview-details summary:hover {
  color: var(--td-text-color-primary);
  background: var(--td-bg-color-container-hover);
}

.preview-details summary:focus-visible {
  outline: 2px solid var(--td-brand-color);
  outline-offset: -2px;
}

.config-code {
  margin: 0;
  padding: 12px;
  background: var(--td-bg-color-secondarycontainer);
  color: var(--td-text-color-primary);
  font: 12px/1.6 ui-monospace, SFMono-Regular, Consolas, monospace;
  overflow-wrap: anywhere;
  white-space: pre-wrap;
}

.config-note,
.empty-state,
.mcp-dialog-content p {
  margin: 0;
  color: var(--td-text-color-secondary);
  font-size: 13px;
  line-height: 1.5;
  overflow-wrap: anywhere;
}

.config-note {
  margin-top: 8px;
}

.empty-state {
  padding: 16px 12px;
  border: 1px solid var(--td-component-border);
  border-radius: 10px;
}

.token-list {
  margin: 0;
  padding: 0;
  overflow: hidden;
  border: 1px solid var(--td-component-border);
  border-radius: 10px;
  list-style: none;
}

.token-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 4px 12px;
  padding: 11px 12px;
}

.token-row + .token-row {
  border-top: 1px solid var(--td-component-border);
}

.token-details {
  flex: 1 1 160px;
  min-width: 0;
}

.token-heading,
.token-meta {
  flex-wrap: wrap;
}

.token-name {
  min-width: 0;
  overflow-wrap: anywhere;
  font-size: 14px;
  font-weight: 600;
}

.token-meta {
  gap: 3px 10px;
  margin-top: 4px;
  color: var(--td-text-color-secondary);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.token-actions {
  gap: 0;
}

.mcp-dialog-content {
  display: grid;
  gap: 12px;
  overscroll-behavior: contain;
}

.mcp-dialog-title {
  margin: 0;
  font: inherit;
}

.mcp-dialog-content .field-error,
.quick-steps .field-error {
  color: var(--td-error-color);
}

.created-token {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  min-width: 0;
  padding: 10px;
  border-radius: 8px;
  background: var(--td-bg-color-secondarycontainer);
}

.created-token code {
  flex: 1 1 220px;
  min-width: 0;
  color: var(--td-text-color-primary);
}

@media (pointer: coarse) {
  .guide-link,
  .client-link,
  .advanced-settings > summary,
  .mcp-control,
  .mcp-select :deep(.t-input),
  .preview-details summary,
  .mcp-management .t-button,
  .mcp-token-dialog :deep(.t-button) {
    min-height: 44px;
  }

  .endpoint-row .t-button,
  .section-actions .t-button--shape-square {
    min-width: 44px;
  }

  .preview-details summary {
    box-sizing: border-box;
    padding-block: 12px;
  }
}
</style>
