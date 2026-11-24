<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { MessagePlugin } from 'tdesign-vue-next';
import {
  RefreshIcon,
  ChatIcon,
  AddIcon,
  CopyIcon,
  BrowseIcon,
  LinkIcon,
} from 'tdesign-icons-vue-next';
import {
  getAiSettings,
  updateAiSettings,
  getMcpTokens,
  createMcpToken,
  revokeMcpToken,
  getMcpEndpointUrls,
  type AiSettingsModel,
  type McpTokenModel,
} from '../api/aiApi';

// ================= 主 Tab 导航 =================
const mainActiveTab = ref<'internal' | 'mcp'>('internal');

// ================= Tab 1: 内置 AI 设置 =================
const loading = ref(false);
const submitLoading = ref(false);

const formData = ref<AiSettingsModel>({
  aiEnabled: false,
  aiApiKey: '',
  aiBaseUrl: 'https://api.deepseek.com/v1',
  aiModelName: 'deepseek-chat',
});

const emit = defineEmits<{
  (e: 'refresh'): void;
}>();

const loadData = async () => {
  loading.value = true;
  try {
    const res = await getAiSettings();
    if (res) {
      formData.value = {
        aiEnabled: res.aiEnabled ?? false,
        aiApiKey: res.aiApiKey ?? '',
        aiBaseUrl: res.aiBaseUrl || 'https://api.deepseek.com/v1',
        aiModelName: res.aiModelName || 'deepseek-chat',
      };
    }
  } catch (e: any) {
    MessagePlugin.error(`加载 AI 配置失败: ${e.message || e}`);
  } finally {
    loading.value = false;
  }
};

const handleSave = async () => {
  submitLoading.value = true;
  try {
    await updateAiSettings(formData.value);
    MessagePlugin.success('AI 助手配置保存成功');
    await loadData();
  } catch (e: any) {
    MessagePlugin.error(`保存失败: ${e.message || e}`);
  } finally {
    submitLoading.value = false;
  }
};

// ================= Tab 2: 外部 Agent / MCP 接入 =================
const tokensLoading = ref(false);
const tokenList = ref<McpTokenModel[]>([]);

// 端点信息
const mcpUrls = computed(() => getMcpEndpointUrls());
const mcpBaseUrl = computed(() => {
  const sse = mcpUrls.value.sseUrl;
  return sse.replace(/\/sse$/, '');
});

// 新建令牌弹窗
const createTokenModalVisible = ref(false);
const createTokenSubmitting = ref(false);
const tokenForm = ref<{ name: string; expireDays: number }>({
  name: '',
  expireDays: 30,
});

// 令牌生成成功提示明文弹窗
const createdTokenDialogVisible = ref(false);
const newlyCreatedToken = ref<McpTokenModel | null>(null);

// 快捷配置选中的令牌
const activeCleartextToken = ref<string>('');
const selectedTokenForGuides = ref<string>('');

// Agent 指引 Tab
const agentTab = ref<'doubao' | 'workbuddy' | 'qoder' | 'antigravity' | 'ide'>('doubao');
const ideSubTab = ref<'cursor' | 'claude' | 'windsurf'>('cursor');

const loadTokens = async () => {
  tokensLoading.value = true;
  try {
    const res = await getMcpTokens();
    tokenList.value = Array.isArray(res) ? res : [];
  } catch (err: any) {
    console.warn('获取 MCP 令牌失败:', err);
  } finally {
    tokensLoading.value = false;
  }
};

const handleRefresh = async () => {
  await Promise.all([loadData(), loadTokens()]);
  emit('refresh');
};

const initData = async () => {
  await Promise.all([loadData(), loadTokens()]);
};
defineExpose({ initData });

onMounted(() => {
  loadData();
  loadTokens();
});

// 复制文本辅助函数
const copyText = async (text: string, label = '内容') => {
  try {
    if (navigator?.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      MessagePlugin.success(`${label}已复制到剪贴板`);
      return;
    }
  } catch (e) {
    // fallback
  }
  try {
    const textarea = document.createElement('textarea');
    textarea.value = text;
    textarea.style.position = 'fixed';
    textarea.style.opacity = '0';
    document.body.appendChild(textarea);
    textarea.select();
    document.execCommand('copy');
    document.body.removeChild(textarea);
    MessagePlugin.success(`${label}已复制到剪贴板`);
  } catch (err: any) {
    MessagePlugin.error(`复制失败: ${err.message || err}`);
  }
};

const openUrl = (url: string) => {
  window.open(url, '_blank');
};

const formatDate = (dateStr: string | null | undefined): string => {
  if (!dateStr) return '-';
  try {
    const d = new Date(dateStr);
    if (isNaN(d.getTime())) return dateStr;
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
  } catch {
    return dateStr;
  }
};

const isExpired = (dateStr: string | null | undefined): boolean => {
  if (!dateStr) return false;
  try {
    const d = new Date(dateStr);
    return !isNaN(d.getTime()) && d.getTime() < Date.now();
  } catch {
    return false;
  }
};

const maskToken = (token: string | undefined): string => {
  if (!token) return 'mslx_mcp_******';
  if (token.length <= 16) return token;
  return `${token.substring(0, 12)}******${token.substring(token.length - 4)}`;
};

// 令牌表格列配置
const tokenColumns = [
  { colKey: 'name', title: '令牌名称', minWidth: 140 },
  { colKey: 'token', title: '令牌密钥 (Masked)', minWidth: 200 },
  { colKey: 'status', title: '状态', width: 90 },
  { colKey: 'createdAt', title: '创建时间', width: 150 },
  { colKey: 'expiresAt', title: '过期时间', width: 150 },
  { colKey: 'lastUsedAt', title: '最近使用', width: 150 },
  { colKey: 'op', title: '操作', width: 80, fixed: 'right' as const },
];

const handleOpenCreateModal = () => {
  tokenForm.value = {
    name: '',
    expireDays: 30,
  };
  createTokenModalVisible.value = true;
};

const handleConfirmCreateToken = async () => {
  if (!tokenForm.value.name.trim()) {
    MessagePlugin.warning('请输入令牌名称');
    return;
  }
  createTokenSubmitting.value = true;
  try {
    const created = await createMcpToken({
      name: tokenForm.value.name.trim(),
      expireDays: tokenForm.value.expireDays === 0 ? null : tokenForm.value.expireDays,
    });
    createTokenModalVisible.value = false;
    newlyCreatedToken.value = created;
    if (created?.token) {
      activeCleartextToken.value = created.token;
      selectedTokenForGuides.value = created.token;
    }
    createdTokenDialogVisible.value = true;
    MessagePlugin.success('MCP 访问令牌创建成功');
    await loadTokens();
  } catch (err: any) {
    MessagePlugin.error(`创建令牌失败: ${err.message || err}`);
  } finally {
    createTokenSubmitting.value = false;
  }
};

const handleRevokeToken = async (id: string) => {
  try {
    await revokeMcpToken(id);
    MessagePlugin.success('令牌已成功撤销');
    await loadTokens();
  } catch (err: any) {
    MessagePlugin.error(`撤销失败: ${err.message || err}`);
  }
};

// 选中的或当前注入指引配置的 Token
const currentGuideToken = computed(() => {
  if (selectedTokenForGuides.value) {
    return selectedTokenForGuides.value;
  }
  if (activeCleartextToken.value) {
    return activeCleartextToken.value;
  }
  const validTokens = tokenList.value.filter((t) => !t.isRevoked && !isExpired(t.expiresAt));
  if (validTokens.length > 0) {
    return validTokens[0].token || validTokens[0].maskedToken || '<YOUR_MCP_TOKEN>';
  }
  return '<YOUR_MCP_TOKEN>';
});

// 可选的 Token 下拉项列表
const availableTokenOptions = computed(() => {
  const opts = tokenList.value
    .filter((t) => !t.isRevoked && !isExpired(t.expiresAt))
    .map((t) => ({
      label: `${t.name} (${t.maskedToken || maskToken(t.token)})`,
      value: t.token || t.maskedToken || '',
    }));
  return opts;
});

// 1. 豆包 / 纯文本 AI 完整预设提示词
const doubaoSystemPrompt = computed(() => {
  const token = currentGuideToken.value;
  const sseUrl = mcpUrls.value.sseUrl;
  const skillUrl = mcpUrls.value.skillMdUrl;
  return `# Role: Minecraft 智能运维专家 (MSLX AI Assistant)

## 身份定位
你是由 MSLX (Minecraft Server Launcher X) 驱动的专业 Minecraft 服务器运维顾问与智能体。你精通各类 Minecraft 服务端架构（Paper、Purpur、Fabric、Forge、Folia 等），熟悉服务生命周期控制、玩家管理、备份容灾、网络映射与生态配置。

## 宿主系统 MCP 接入端点
- MCP SSE 服务接口: ${sseUrl}
- MCP Skill 协议规范: ${skillUrl}
- 访问授权凭证 (Token): ${token}
- 鉴权方式: 在请求 Header 附加 \`Authorization: Bearer <Token>\`，或在 SSE URL 查询参数附加 \`?token=<Token>\`。

## 核心行为与安全规范
1. **高危操作防范**:
   - 严禁擅自执行强制终止进程 (force_kill)、清空/删除服务端目录 (delete_server_instance) 或破坏性备份还原等高危动作。
   - 在进行任何核心升级、插件增删或配置文件调整前，必须提醒管理员确认或协助生成快照备份。
2. **版本与环境严谨匹配**:
   - 严格匹配 Minecraft 内核与 Java 运行环境（MC 1.20.5+ 须 Java 21+，1.17 - 1.20.4 须 Java 17+，1.16.5 推荐 Java 8/11）。
   - 针对模组服优先检查客户端纯净模组 (Client-only mods)，避免服务端因加载客户端 GUI/渲染模组而崩溃。
3. **运维工具能力**:
   - 实例生命周期: 查询实例列表、实例状态、CPU/内存占用、启动/停止/重启。
   - 游戏内交互: 发送控制台 RCON 指令、查询在线玩家、白名单维护、OP 授权、封禁治理。
   - 容灾与穿透: 触发服务器备份、查询与还原备份归档、FRP 穿透隧道启停与状态监测。
   - 资源检索: Modrinth / CurseForge 插件与模组检索、版本筛选与下载。

请根据管理员的要求，专业、详尽且严谨地执行服务器运维与技术排障任务。`;
});

// 2. Workbuddy (腾讯工友) JSON 配置
const workbuddySseConfig = computed(() => {
  const token = currentGuideToken.value;
  const sseUrl = mcpUrls.value.sseUrl;
  return JSON.stringify(
    {
      mcpServers: {
        'mslx-server-ops': {
          url: sseUrl,
          headers: {
            Authorization: `Bearer ${token}`,
          },
        },
      },
    },
    null,
    2
  );
});

const workbuddyStdioConfig = computed(() => {
  const token = currentGuideToken.value;
  const baseUrl = mcpBaseUrl.value;
  return JSON.stringify(
    {
      mcpServers: {
        'mslx-server-ops': {
          command: 'npx',
          args: ['-y', '@mslteam/mslx-agent-skill', '--url', baseUrl, '--token', token],
        },
      },
    },
    null,
    2
  );
});

// 3. Qoder JSON 配置
const qoderConfig = computed(() => {
  const token = currentGuideToken.value;
  const baseUrl = mcpBaseUrl.value;
  return JSON.stringify(
    {
      mcpServers: {
        'mslx-mc-ops': {
          command: 'npx',
          args: ['-y', '@mslteam/mslx-agent-skill', '--url', baseUrl, '--token', token],
        },
      },
    },
    null,
    2
  );
});

// 4. Google 反重力 (Antigravity) JSON 配置
const antigravityConfig = computed(() => {
  const token = currentGuideToken.value;
  const baseUrl = mcpBaseUrl.value;
  return JSON.stringify(
    {
      mcpServers: {
        'mslx-agent': {
          command: 'npx',
          args: ['-y', '@mslteam/mslx-agent-skill', '--url', baseUrl, '--token', token],
        },
      },
    },
    null,
    2
  );
});

// 5. Cursor / Claude Desktop / Windsurf JSON 配置
const cursorConfig = computed(() => {
  const token = currentGuideToken.value;
  const baseUrl = mcpBaseUrl.value;
  return JSON.stringify(
    {
      mcpServers: {
        'mslx-agent': {
          command: 'npx',
          args: ['-y', '@mslteam/mslx-agent-skill', '--url', baseUrl, '--token', token],
        },
      },
    },
    null,
    2
  );
});

const claudeDesktopConfig = computed(() => {
  const token = currentGuideToken.value;
  const baseUrl = mcpBaseUrl.value;
  return JSON.stringify(
    {
      mcpServers: {
        'mslx-minecraft-ops': {
          command: 'npx',
          args: ['-y', '@mslteam/mslx-agent-skill', '--url', baseUrl, '--token', token],
        },
      },
    },
    null,
    2
  );
});

const windsurfConfig = computed(() => {
  const token = currentGuideToken.value;
  const baseUrl = mcpBaseUrl.value;
  return JSON.stringify(
    {
      mcpServers: {
        'mslx-agent': {
          command: 'npx',
          args: ['-y', '@mslteam/mslx-agent-skill', '--url', baseUrl, '--token', token],
        },
      },
    },
    null,
    2
  );
});
</script>

<template>
  <div
    class="design-card relative flex flex-col rounded-2xl border border-[var(--td-component-border)] shadow-sm p-5 sm:p-6 transition-all duration-300"
  >
    <t-loading :loading="loading" show-overlay>
      <div>
        <!-- 头部标题栏 -->
        <div
          class="flex flex-col sm:flex-row sm:items-center justify-between gap-4 mb-4 pb-4 border-b border-dashed border-[var(--td-component-border)]"
        >
          <div class="flex items-center gap-3">
            <div
              class="w-1.5 h-5 bg-[var(--color-primary)] rounded-full shadow-[0_0_8px_var(--color-primary-light)] opacity-90"
            ></div>
            <div class="flex flex-col">
              <h2 class="text-lg font-bold text-[var(--td-text-color-primary)] m-0 leading-none tracking-tight">
                AI 智能运维与外部 Agent 接入
              </h2>
              <span class="text-xs text-[var(--td-text-color-secondary)] mt-1.5 font-medium">
                配置大模型 API 密钥开启自然语言运维，或通过 MCP 协议将 MSLX 实例挂载至外部智能体（Workbuddy / Cursor / 豆包等）。
              </span>
            </div>
          </div>
          <t-button variant="dashed" size="small" class="!bg-transparent" @click="handleRefresh">
            <template #icon><refresh-icon /></template>
            刷新数据
          </t-button>
        </div>

        <!-- 主 Tab 切换 -->
        <t-tabs v-model="mainActiveTab" theme="normal" class="mb-5">
          <t-tab-panel value="internal" label="内置 AI 对话助手" />
          <t-tab-panel value="mcp" label="外部 Agent / MCP 接入" />
        </t-tabs>

        <!-- ======================= Tab 1: 内置 AI 设置 ======================= -->
        <div v-show="mainActiveTab === 'internal'">
          <t-form :data="formData" :label-width="140" label-align="left" @submit="handleSave">
            <div class="flex items-center gap-3 mt-2 mb-6">
              <span class="text-xs font-extrabold text-[var(--td-text-color-secondary)] uppercase tracking-widest"
                >基础开关</span
              >
              <div class="h-px bg-[var(--td-component-border)] flex-1"></div>
            </div>

            <t-form-item label="启用 AI 助手">
              <template #help>
                <span class="text-[11px] font-medium text-[var(--td-text-color-secondary)] mt-1 inline-block">
                  开启后，系统将在全局顶部快捷栏展示 AI 助手入口。
                </span>
              </template>
              <div class="flex items-center gap-3">
                <t-switch v-model="formData.aiEnabled" />
                <span
                  class="text-[11px] font-extrabold px-2 py-0.5 rounded-md transition-colors"
                  :class="
                    formData.aiEnabled
                      ? 'bg-[var(--td-brand-color-light)] text-[var(--td-brand-color)] border border-[var(--td-brand-color)]'
                      : 'bg-[var(--td-bg-color-secondarycontainer)] text-[var(--td-text-color-secondary)] border border-[var(--td-component-border)]'
                  "
                >
                  {{ formData.aiEnabled ? 'AI 助手已激活' : '已关闭' }}
                </span>
              </div>
            </t-form-item>

            <template v-if="formData.aiEnabled">
              <div class="flex items-center gap-3 mt-8 mb-6">
                <span class="text-xs font-extrabold text-[var(--td-text-color-secondary)] uppercase tracking-widest"
                  >模型配置</span
                >
                <div class="h-px bg-[var(--td-component-border)] flex-1"></div>
              </div>

              <t-form-item label="API Base URL">
                <template #help>
                  <span class="text-[11px] font-medium text-[var(--td-text-color-secondary)] mt-1 inline-block">
                    请填写包含 <code>/v1</code> 的完整地址，例如 <code>https://api.deepseek.com/v1</code> 或 <code>https://dashscope.aliyuncs.com/compatible-mode/v1</code>。
                  </span>
                </template>
                <t-input
                  v-model="formData.aiBaseUrl"
                  placeholder="https://api.deepseek.com/v1"
                  class="!w-full sm:!w-[400px] !font-mono !text-sm"
                />
              </t-form-item>

              <t-form-item label="API Key">
                <template #help>
                  <span class="text-[11px] font-medium text-[var(--td-text-color-secondary)] mt-1 inline-block">
                    大模型的 API 访问密钥，通常以 <code>sk-</code> 开头。
                  </span>
                </template>
                <t-input
                  v-model="formData.aiApiKey"
                  type="password"
                  placeholder="请输入大模型的 API Key (例如 sk-xxxxx)"
                  class="!w-full sm:!w-[400px] !font-mono !text-sm"
                />
              </t-form-item>

              <t-form-item label="模型名称">
                <template #help>
                  <span class="text-[11px] font-medium text-[var(--td-text-color-secondary)] mt-1 inline-block">
                    指定模型标识，例如 <code>deepseek-chat</code>、<code>qwen-max</code>、<code>gpt-4o-mini</code> 等。
                  </span>
                </template>
                <t-input
                  v-model="formData.aiModelName"
                  placeholder="如 deepseek-chat, qwen-max, gpt-4o-mini"
                  class="!w-full sm:!w-[400px] !font-mono !text-sm"
                />
              </t-form-item>
            </template>

            <div
              class="mt-8 pt-5 border-t border-dashed border-[var(--td-component-border)] flex flex-col sm:flex-row items-center justify-between gap-4"
            >
              <t-button
                theme="primary"
                type="submit"
                :loading="submitLoading"
                class="!h-10 !w-full sm:!w-auto sm:!px-10 !font-bold tracking-widest !rounded-xl shadow-md transition-shadow"
              >
                保存 AI 配置
              </t-button>

              <span class="text-xs text-[var(--td-text-color-placeholder)] hidden sm:flex items-center gap-1">
                <chat-icon /> 配置生效后即刻可用
              </span>
            </div>
          </t-form>
        </div>

        <!-- ======================= Tab 2: 外部 Agent / MCP 接入 ======================= -->
        <div v-show="mainActiveTab === 'mcp'" class="space-y-6">
          <!-- 1. 服务端点信息卡片 (Endpoint Card) -->
          <div class="rounded-xl border border-[var(--td-component-border)] bg-[var(--td-bg-color-secondarycontainer)] p-4 sm:p-5">
            <div class="flex items-center justify-between mb-3">
              <div class="flex items-center gap-2">
                <link-icon class="text-[var(--color-primary)] text-base" />
                <h3 class="text-sm font-bold text-[var(--td-text-color-primary)] m-0">
                  MCP 协议端点 (Model Context Protocol)
                </h3>
                <t-tag theme="primary" variant="light" size="small">标准协议 2024-11-05</t-tag>
              </div>
            </div>
            <p class="text-xs text-[var(--td-text-color-secondary)] mb-4 leading-relaxed">
              外部 Agent 客户端或开发插件可通过标准 SSE 接口建立连接并动态发现 MSLX 运维工具，或直接加载 Skill Markdown 规范。
            </p>

            <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
              <!-- SSE 端点 -->
              <div class="rounded-lg border border-[var(--td-component-border)] bg-[var(--td-bg-color-container)] p-3.5 flex flex-col justify-between">
                <div>
                  <div class="flex items-center justify-between mb-1.5">
                    <span class="text-xs font-bold text-[var(--td-text-color-primary)]">MCP SSE 通信端点</span>
                    <t-tag theme="success" variant="light" size="small">SSE 流式传输</t-tag>
                  </div>
                  <div class="font-mono text-xs text-[var(--td-text-color-secondary)] break-all select-all py-1.5 px-2 bg-[var(--td-bg-color-secondarycontainer)] rounded border border-[var(--td-component-border)]">
                    {{ mcpUrls.sseUrl }}
                  </div>
                </div>
                <div class="mt-3 flex items-center justify-end">
                  <t-button size="small" variant="outline" @click="copyText(mcpUrls.sseUrl, 'MCP SSE 端点')">
                    <template #icon><copy-icon /></template>
                    一键复制
                  </t-button>
                </div>
              </div>

              <!-- Skill.md 端点 -->
              <div class="rounded-lg border border-[var(--td-component-border)] bg-[var(--td-bg-color-container)] p-3.5 flex flex-col justify-between">
                <div>
                  <div class="flex items-center justify-between mb-1.5">
                    <span class="text-xs font-bold text-[var(--td-text-color-primary)]">Skill 协议规范定义 (Markdown)</span>
                    <t-tag theme="default" variant="light" size="small">text/markdown</t-tag>
                  </div>
                  <div class="font-mono text-xs text-[var(--td-text-color-secondary)] break-all select-all py-1.5 px-2 bg-[var(--td-bg-color-secondarycontainer)] rounded border border-[var(--td-component-border)]">
                    {{ mcpUrls.skillMdUrl }}
                  </div>
                </div>
                <div class="mt-3 flex items-center justify-end gap-2">
                  <t-button size="small" variant="outline" @click="copyText(mcpUrls.skillMdUrl, 'Skill.md 规范地址')">
                    <template #icon><copy-icon /></template>
                    一键复制
                  </t-button>
                  <t-button size="small" variant="text" @click="openUrl(mcpUrls.skillMdUrl)">
                    <template #icon><browse-icon /></template>
                    在浏览器预览
                  </t-button>
                </div>
              </div>
            </div>
          </div>

          <!-- 2. 访问令牌管理 (Token Management) -->
          <div class="rounded-xl border border-[var(--td-component-border)] p-4 sm:p-5">
            <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-3 mb-4">
              <div>
                <h3 class="text-sm font-bold text-[var(--td-text-color-primary)] m-0 flex items-center gap-2">
                  MCP 访问令牌管理
                  <span class="text-xs font-normal text-[var(--td-text-color-secondary)]">
                    (共 {{ tokenList.length }} 个令牌)
                  </span>
                </h3>
                <span class="text-xs text-[var(--td-text-color-secondary)] mt-1 block">
                  外部 Agent 通过携带此令牌连接并操作当前 MSLX 实例，支持细粒度鉴权与独立撤销。
                </span>
              </div>
              <t-button theme="primary" size="small" @click="handleOpenCreateModal">
                <template #icon><add-icon /></template>
                新建令牌
              </t-button>
            </div>

            <!-- 令牌表格 -->
            <t-table
              :data="tokenList"
              :columns="tokenColumns"
              row-key="id"
              :loading="tokensLoading"
              size="small"
              hover
              empty="暂无 MCP 访问令牌，请点击上方「新建令牌」创建"
            >
              <template #token="{ row }">
                <div class="flex items-center gap-1.5">
                  <span class="font-mono text-xs">{{ row.maskedToken || maskToken(row.token) }}</span>
                  <t-button
                    size="small"
                    variant="text"
                    shape="square"
                    @click="copyText(row.token || row.maskedToken || '', '令牌密钥')"
                  >
                    <copy-icon class="text-xs" />
                  </t-button>
                </div>
              </template>

              <template #status="{ row }">
                <t-tag v-if="row.isRevoked" theme="danger" variant="light" size="small">已撤销</t-tag>
                <t-tag v-else-if="isExpired(row.expiresAt)" theme="warning" variant="light" size="small">已过期</t-tag>
                <t-tag v-else theme="success" variant="light" size="small">有效</t-tag>
              </template>

              <template #createdAt="{ row }">
                <span class="text-xs">{{ formatDate(row.createdAt) }}</span>
              </template>

              <template #expiresAt="{ row }">
                <span class="text-xs" :class="{ 'text-[var(--td-error-color)]': isExpired(row.expiresAt) }">
                  {{ row.expiresAt ? formatDate(row.expiresAt) : '永久有效' }}
                </span>
              </template>

              <template #lastUsedAt="{ row }">
                <span class="text-xs text-[var(--td-text-color-secondary)]">
                  {{ row.lastUsedAt ? formatDate(row.lastUsedAt) : '从未调用' }}
                </span>
              </template>

              <template #op="{ row }">
                <t-popconfirm
                  content="确定要撤销此令牌吗？撤销后使用此令牌的外部 Agent 将无法继续访问 MCP 接口。"
                  @confirm="handleRevokeToken(row.id)"
                >
                  <t-button size="small" variant="text" theme="danger" :disabled="row.isRevoked">
                    撤销
                  </t-button>
                </t-popconfirm>
              </template>
            </t-table>
          </div>

          <!-- 3. 各大 Agent 接入指引与即拷配置 (Quick-Setup Card with Tabs) -->
          <div class="rounded-xl border border-[var(--td-component-border)] p-4 sm:p-5">
            <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--td-component-border)]">
              <div>
                <h3 class="text-sm font-bold text-[var(--td-text-color-primary)] m-0">
                  国内与全球主流 Agent 接入指引 & 配置
                </h3>
                <span class="text-xs text-[var(--td-text-color-secondary)] mt-1 block">
                  选择下方客户端或平台，即可一键复制已嵌入当前令牌与端点的标准配置代码或系统提示词。
                </span>
              </div>

              <!-- 令牌选择器 -->
              <div v-if="availableTokenOptions.length > 0" class="flex items-center gap-2">
                <span class="text-xs text-[var(--td-text-color-secondary)] whitespace-nowrap">带入配置的令牌:</span>
                <t-select
                  v-model="selectedTokenForGuides"
                  size="small"
                  class="!w-48 sm:!w-60"
                  placeholder="选择当前带入配置的令牌"
                >
                  <t-option
                    v-for="opt in availableTokenOptions"
                    :key="opt.value"
                    :value="opt.value"
                    :label="opt.label"
                  />
                </t-select>
              </div>
            </div>

            <!-- Agent 子 Tabs -->
            <t-tabs v-model="agentTab" theme="card" class="mt-4">
              <!-- 豆包 / 纯文本 AI -->
              <t-tab-panel value="doubao" label="🇨🇳 豆包 / 纯文本 AI">
                <div class="p-3 sm:p-4 space-y-4">
                  <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-3 bg-[var(--td-bg-color-secondarycontainer)] p-3 rounded-lg border border-[var(--td-component-border)]">
                    <div>
                      <h4 class="text-xs font-bold text-[var(--td-text-color-primary)] m-0">
                        网页版豆包 / 纯文本对话模型接入方案
                      </h4>
                      <p class="text-xs text-[var(--td-text-color-secondary)] mt-1 mb-0">
                        无需安装任何本地客户端，将完整预设提示词粘贴至豆包的「系统人设」或首条消息中，即可赋予模型专业的 MSLX 运维知识库与规范认知。
                      </p>
                    </div>
                    <t-button theme="primary" size="medium" @click="copyText(doubaoSystemPrompt, '豆包预设提示词')">
                      <template #icon><copy-icon /></template>
                      一键复制完整预设提示词
                    </t-button>
                  </div>

                  <!-- 提示词预览容器 -->
                  <div class="relative">
                    <div class="text-xs font-bold text-[var(--td-text-color-secondary)] mb-1 flex items-center justify-between">
                      <span>预设提示词预览 (已嵌入当前 MCP 端点与令牌)</span>
                    </div>
                    <div class="relative bg-[#1e1e1e] dark:bg-[#141414] text-[#d4d4d4] rounded-xl p-4 font-mono text-xs overflow-x-auto max-h-[320px] shadow-inner border border-[var(--td-component-border)] select-all leading-relaxed whitespace-pre-wrap">
                      {{ doubaoSystemPrompt }}
                    </div>
                  </div>
                </div>
              </t-tab-panel>

              <!-- Workbuddy (腾讯工友) -->
              <t-tab-panel value="workbuddy" label="🇨🇳 Workbuddy (腾讯工友)">
                <div class="p-3 sm:p-4 space-y-4">
                  <div class="bg-[var(--td-bg-color-secondarycontainer)] p-3 rounded-lg border border-[var(--td-component-border)]">
                    <h4 class="text-xs font-bold text-[var(--td-text-color-primary)] m-0">接入步骤</h4>
                    <ol class="text-xs text-[var(--td-text-color-secondary)] mt-2 mb-0 pl-4 space-y-1">
                      <li>打开 <strong>腾讯工友 (Workbuddy)</strong> 客户端，点击左下角设置图标进入「MCP 扩展设置」。</li>
                      <li>添加自定义 MCP 服务，配置名称填写 <code>mslx-server-ops</code>。</li>
                      <li>选择 <strong>SSE 模式</strong>（推荐直连）或 <strong>Stdio 模式</strong>（基于 npm CLI 桥接），粘贴下方对应配置即可。</li>
                    </ol>
                  </div>

                  <div class="space-y-3">
                    <div>
                      <div class="flex items-center justify-between mb-1.5">
                        <span class="text-xs font-bold text-[var(--td-text-color-primary)]">方案 A: SSE 直连配置 (推荐)</span>
                        <t-button size="small" variant="outline" @click="copyText(workbuddySseConfig, 'Workbuddy SSE 配置')">
                          <template #icon><copy-icon /></template>
                          复制 SSE 配置
                        </t-button>
                      </div>
                      <div class="bg-[#1e1e1e] dark:bg-[#141414] text-[#9cdcfe] rounded-xl p-4 font-mono text-xs overflow-x-auto shadow-inner border border-[var(--td-component-border)] select-all">
                        <pre class="m-0">{{ workbuddySseConfig }}</pre>
                      </div>
                    </div>

                    <div>
                      <div class="flex items-center justify-between mb-1.5">
                        <span class="text-xs font-bold text-[var(--td-text-color-primary)]">方案 B: CLI Stdio 桥接配置</span>
                        <t-button size="small" variant="outline" @click="copyText(workbuddyStdioConfig, 'Workbuddy Stdio 配置')">
                          <template #icon><copy-icon /></template>
                          复制 Stdio 配置
                        </t-button>
                      </div>
                      <div class="bg-[#1e1e1e] dark:bg-[#141414] text-[#9cdcfe] rounded-xl p-4 font-mono text-xs overflow-x-auto shadow-inner border border-[var(--td-component-border)] select-all">
                        <pre class="m-0">{{ workbuddyStdioConfig }}</pre>
                      </div>
                    </div>
                  </div>
                </div>
              </t-tab-panel>

              <!-- Qoder -->
              <t-tab-panel value="qoder" label="🇨🇳 Qoder">
                <div class="p-3 sm:p-4 space-y-4">
                  <div class="bg-[var(--td-bg-color-secondarycontainer)] p-3 rounded-lg border border-[var(--td-component-border)]">
                    <h4 class="text-xs font-bold text-[var(--td-text-color-primary)] m-0">接入步骤</h4>
                    <ol class="text-xs text-[var(--td-text-color-secondary)] mt-2 mb-0 pl-4 space-y-1">
                      <li>在 <strong>Qoder</strong> 扩展设置中，找到「MCP Servers」配置项或打开 <code>mcp_servers.json</code>。</li>
                      <li>在 <code>mcpServers</code> 节点中添加 <code>mslx-mc-ops</code> 工具链配置。</li>
                      <li>保存配置后，Qoder 将自动注册 MSLX 实例管理工具与运维函数。</li>
                    </ol>
                  </div>

                  <div>
                    <div class="flex items-center justify-between mb-1.5">
                      <span class="text-xs font-bold text-[var(--td-text-color-primary)]">Qoder MCP 配置代码</span>
                      <t-button size="small" variant="outline" @click="copyText(qoderConfig, 'Qoder 配置代码')">
                        <template #icon><copy-icon /></template>
                        一键复制 Qoder 配置
                      </t-button>
                    </div>
                    <div class="bg-[#1e1e1e] dark:bg-[#141414] text-[#9cdcfe] rounded-xl p-4 font-mono text-xs overflow-x-auto shadow-inner border border-[var(--td-component-border)] select-all">
                      <pre class="m-0">{{ qoderConfig }}</pre>
                    </div>
                  </div>
                </div>
              </t-tab-panel>

              <!-- Google 反重力 (Antigravity) -->
              <t-tab-panel value="antigravity" label="🌐 Google 反重力 (Antigravity)">
                <div class="p-3 sm:p-4 space-y-4">
                  <div class="bg-[var(--td-bg-color-secondarycontainer)] p-3 rounded-lg border border-[var(--td-component-border)]">
                    <h4 class="text-xs font-bold text-[var(--td-text-color-primary)] m-0">Google Antigravity 双模接入方式</h4>
                    <p class="text-xs text-[var(--td-text-color-secondary)] mt-2 mb-0 leading-relaxed">
                      Google Antigravity 原生支持 <strong>Skill 机制</strong> 与 <strong>MCP Servers 规范</strong>。你可以直接将本系统导出的 Skill Markdown 作为自定义技能引用，也可以通过 MCP 协议挂载全量工具。
                    </p>
                  </div>

                  <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                    <!-- 方式 1: Skill.md 直接引用 -->
                    <div class="rounded-lg border border-[var(--td-component-border)] p-3.5 bg-[var(--td-bg-color-container)]">
                      <h5 class="text-xs font-bold text-[var(--td-text-color-primary)] m-0 mb-1.5">方式 1: 直接引用 Skill 规范</h5>
                      <p class="text-xs text-[var(--td-text-color-secondary)] mb-2">
                        在 Antigravity 提示词或项目指令中加入：
                      </p>
                      <div class="bg-[var(--td-bg-color-secondarycontainer)] p-2 rounded text-xs font-mono text-[var(--td-text-color-primary)] break-all border border-[var(--td-component-border)] mb-3">
                        参考 Skill: {{ mcpUrls.skillMdUrl }} 进行服务器运维
                      </div>
                      <div class="flex items-center gap-2">
                        <t-button size="small" variant="outline" @click="copyText(mcpUrls.skillMdUrl, 'Skill.md 规范地址')">
                          <template #icon><copy-icon /></template>
                          复制 Skill 地址
                        </t-button>
                        <t-button size="small" variant="text" @click="openUrl(mcpUrls.skillMdUrl)">
                          <template #icon><browse-icon /></template>
                          在浏览器打开
                        </t-button>
                      </div>
                    </div>

                    <!-- 方式 2: MCP Server 桥接 -->
                    <div class="rounded-lg border border-[var(--td-component-border)] p-3.5 bg-[var(--td-bg-color-container)]">
                      <h5 class="text-xs font-bold text-[var(--td-text-color-primary)] m-0 mb-1.5">方式 2: MCP Servers 配置</h5>
                      <p class="text-xs text-[var(--td-text-color-secondary)] mb-2">
                        在 Antigravity MCP 配置文件中追加：
                      </p>
                      <div class="bg-[#1e1e1e] dark:bg-[#141414] text-[#9cdcfe] rounded-lg p-2.5 font-mono text-xs overflow-x-auto shadow-inner border border-[var(--td-component-border)] select-all mb-3">
                        <pre class="m-0">{{ antigravityConfig }}</pre>
                      </div>
                      <t-button size="small" variant="outline" @click="copyText(antigravityConfig, 'Antigravity MCP 配置')">
                        <template #icon><copy-icon /></template>
                        一键复制 MCP 配置
                      </t-button>
                    </div>
                  </div>
                </div>
              </t-tab-panel>

              <!-- Cursor / Claude Desktop / Windsurf -->
              <t-tab-panel value="ide" label="🌐 Cursor / Claude / Windsurf">
                <div class="p-3 sm:p-4 space-y-4">
                  <!-- 子切换 -->
                  <div class="flex items-center gap-2">
                    <t-button
                      size="small"
                      :variant="ideSubTab === 'cursor' ? 'base' : 'outline'"
                      :theme="ideSubTab === 'cursor' ? 'primary' : 'default'"
                      @click="ideSubTab = 'cursor'"
                    >
                      Cursor
                    </t-button>
                    <t-button
                      size="small"
                      :variant="ideSubTab === 'claude' ? 'base' : 'outline'"
                      :theme="ideSubTab === 'claude' ? 'primary' : 'default'"
                      @click="ideSubTab = 'claude'"
                    >
                      Claude Desktop
                    </t-button>
                    <t-button
                      size="small"
                      :variant="ideSubTab === 'windsurf' ? 'base' : 'outline'"
                      :theme="ideSubTab === 'windsurf' ? 'primary' : 'default'"
                      @click="ideSubTab = 'windsurf'"
                    >
                      Windsurf
                    </t-button>
                  </div>

                  <!-- Cursor -->
                  <div v-show="ideSubTab === 'cursor'" class="space-y-3">
                    <div class="bg-[var(--td-bg-color-secondarycontainer)] p-3 rounded-lg border border-[var(--td-component-border)]">
                      <span class="text-xs font-bold text-[var(--td-text-color-primary)]">配置文件路径:</span>
                      <code class="text-xs ml-1.5 font-mono">.cursor/mcp.json</code> 或进入 Cursor「Settings」->「Features」->「MCP」。
                    </div>
                    <div>
                      <div class="flex items-center justify-between mb-1.5">
                        <span class="text-xs font-bold text-[var(--td-text-color-primary)]">Cursor MCP 配置代码</span>
                        <t-button size="small" variant="outline" @click="copyText(cursorConfig, 'Cursor 配置')">
                          <template #icon><copy-icon /></template>
                          复制 Cursor 配置
                        </t-button>
                      </div>
                      <div class="bg-[#1e1e1e] dark:bg-[#141414] text-[#9cdcfe] rounded-xl p-4 font-mono text-xs overflow-x-auto shadow-inner border border-[var(--td-component-border)] select-all">
                        <pre class="m-0">{{ cursorConfig }}</pre>
                      </div>
                    </div>
                  </div>

                  <!-- Claude Desktop -->
                  <div v-show="ideSubTab === 'claude'" class="space-y-3">
                    <div class="bg-[var(--td-bg-color-secondarycontainer)] p-3 rounded-lg border border-[var(--td-component-border)]">
                      <span class="text-xs font-bold text-[var(--td-text-color-primary)]">配置文件路径:</span>
                      <code class="text-xs ml-1.5 font-mono">~/Library/Application Support/Claude/claude_desktop_config.json</code> (macOS) 或 <code class="text-xs font-mono">%APPDATA%\Claude\claude_desktop_config.json</code> (Windows)。
                    </div>
                    <div>
                      <div class="flex items-center justify-between mb-1.5">
                        <span class="text-xs font-bold text-[var(--td-text-color-primary)]">Claude Desktop 配置代码</span>
                        <t-button size="small" variant="outline" @click="copyText(claudeDesktopConfig, 'Claude Desktop 配置')">
                          <template #icon><copy-icon /></template>
                          复制 Claude Desktop 配置
                        </t-button>
                      </div>
                      <div class="bg-[#1e1e1e] dark:bg-[#141414] text-[#9cdcfe] rounded-xl p-4 font-mono text-xs overflow-x-auto shadow-inner border border-[var(--td-component-border)] select-all">
                        <pre class="m-0">{{ claudeDesktopConfig }}</pre>
                      </div>
                    </div>
                  </div>

                  <!-- Windsurf -->
                  <div v-show="ideSubTab === 'windsurf'" class="space-y-3">
                    <div class="bg-[var(--td-bg-color-secondarycontainer)] p-3 rounded-lg border border-[var(--td-component-border)]">
                      <span class="text-xs font-bold text-[var(--td-text-color-primary)]">配置文件路径:</span>
                      <code class="text-xs ml-1.5 font-mono">~/.codeium/windsurf/mcp_config.json</code>。
                    </div>
                    <div>
                      <div class="flex items-center justify-between mb-1.5">
                        <span class="text-xs font-bold text-[var(--td-text-color-primary)]">Windsurf MCP 配置代码</span>
                        <t-button size="small" variant="outline" @click="copyText(windsurfConfig, 'Windsurf 配置')">
                          <template #icon><copy-icon /></template>
                          复制 Windsurf 配置
                        </t-button>
                      </div>
                      <div class="bg-[#1e1e1e] dark:bg-[#141414] text-[#9cdcfe] rounded-xl p-4 font-mono text-xs overflow-x-auto shadow-inner border border-[var(--td-component-border)] select-all">
                        <pre class="m-0">{{ windsurfConfig }}</pre>
                      </div>
                    </div>
                  </div>
                </div>
              </t-tab-panel>
            </t-tabs>
          </div>
        </div>
      </div>
    </t-loading>

    <!-- 新建令牌弹窗 -->
    <t-dialog
      v-model:visible="createTokenModalVisible"
      attach="body"
      header="新建 MCP 访问令牌"
      :confirm-btn="{
        content: '立即生成',
        loading: createTokenSubmitting,
      }"
      cancel-btn="取消"
      width="480px"
      @confirm="handleConfirmCreateToken"
    >
      <div class="py-2 space-y-4">
        <t-form :data="tokenForm" :label-width="90" label-align="left">
          <t-form-item label="令牌名称" required-mark>
            <t-input
              v-model="tokenForm.name"
              placeholder="例如：腾讯工友 Agent、Cursor、豆包助手"
            />
          </t-form-item>

          <t-form-item label="有效期限">
            <t-select v-model="tokenForm.expireDays" placeholder="选择有效期">
              <t-option :value="7" label="7 天" />
              <t-option :value="30" label="30 天 (推荐)" />
              <t-option :value="90" label="90 天" />
              <t-option :value="180" label="180 天" />
              <t-option :value="0" label="永久有效" />
            </t-select>
          </t-form-item>
        </t-form>
      </div>
    </t-dialog>

    <!-- 令牌生成成功（明文唯一展示窗口） -->
    <t-dialog
      v-model:visible="createdTokenDialogVisible"
      attach="body"
      header="🎉 访问令牌生成成功"
      :confirm-btn="{ content: '我已妥善保存，关闭' }"
      :cancel-btn="null"
      width="540px"
      @confirm="createdTokenDialogVisible = false"
    >
      <div class="py-3 space-y-4">
        <div class="rounded-lg bg-[var(--td-warning-color-light)] border border-[var(--td-warning-color)] p-3 text-xs text-[var(--td-warning-color-dark)] flex items-start gap-2">
          <span class="text-base leading-none">⚠️</span>
          <span>
            出于安全考虑，<strong>此密钥仅在本次创建时以完整明文形式展示一次</strong>。关闭此窗口后将无法再次找回明文，请立即复制并妥善保存至您的 Agent 客户端中。
          </span>
        </div>

        <div>
          <span class="text-xs font-bold text-[var(--td-text-color-primary)] block mb-1.5">生成的完整访问令牌 (Token)</span>
          <div class="flex items-center gap-2">
            <div class="flex-1 font-mono text-xs bg-[var(--td-bg-color-secondarycontainer)] p-3 rounded-lg border border-[var(--td-component-border)] break-all select-all text-[var(--color-primary)] font-bold">
              {{ newlyCreatedToken?.token }}
            </div>
            <t-button
              theme="primary"
              variant="base"
              @click="copyText(newlyCreatedToken?.token || '', '完整令牌密钥')"
            >
              <template #icon><copy-icon /></template>
              复制
            </t-button>
          </div>
        </div>

        <div class="text-xs text-[var(--td-text-color-secondary)]">
          令牌名称: <span class="font-medium text-[var(--td-text-color-primary)]">{{ newlyCreatedToken?.name }}</span>
          <span class="mx-2">|</span>
          过期时间: <span class="font-medium text-[var(--td-text-color-primary)]">{{ newlyCreatedToken?.expiresAt ? formatDate(newlyCreatedToken.expiresAt) : '永久有效' }}</span>
        </div>
      </div>
    </t-dialog>
  </div>
</template>

<style scoped>
</style>
