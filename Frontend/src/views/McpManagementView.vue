<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { MessagePlugin } from 'tdesign-vue-next';
import {
  AddIcon,
  CopyIcon,
  BrowseIcon,
  RefreshIcon,
  SwapIcon,
} from 'tdesign-icons-vue-next';
import {
  getMcpTokens,
  createMcpToken,
  revokeMcpToken,
  getMcpEndpointUrls,
  type McpTokenModel,
} from '../api/aiApi';

const emit = defineEmits<{
  (e: 'switchMode'): void;
}>();

// ================= 端点信息 =================
const mcpUrls = computed(() => getMcpEndpointUrls());

// ================= 令牌数据 =================
const tokensLoading = ref(false);
const tokenList = ref<McpTokenModel[]>([]);
const selectedTokenId = ref<string>('');

// 新建令牌弹窗
const createTokenModalVisible = ref(false);
const createTokenSubmitting = ref(false);
const tokenForm = ref<{ name: string; expireDays: number }>({
  name: '',
  expireDays: 30,
});

// 令牌创建成功明文弹窗
const createdTokenDialogVisible = ref(false);
const newlyCreatedToken = ref<McpTokenModel | null>(null);

// 表格列定义 (遵循 TDesign 标准)
const columns = [
  { colKey: 'name', title: '名称 / 备注', width: 140 },
  { colKey: 'token', title: '令牌标识', minWidth: 180 },
  { colKey: 'expiresAt', title: '有效期 / 到期', width: 150 },
  { colKey: 'status', title: '状态', width: 90 },
  { colKey: 'action', title: '操作', width: 90, align: 'center' },
];

const loadTokens = async () => {
  tokensLoading.value = true;
  try {
    const res = await getMcpTokens();
    tokenList.value = Array.isArray(res) ? res : [];
    // 默认选中第一个有效令牌
    if (!selectedTokenId.value && tokenList.value.length > 0) {
      const active = tokenList.value.find((t) => !t.isRevoked && !isExpired(t.expiresAt));
      selectedTokenId.value = active ? active.id : tokenList.value[0].id;
    }
  } catch (err: any) {
    MessagePlugin.error(`获取 MCP 令牌失败: ${err.message || err}`);
  } finally {
    tokensLoading.value = false;
  }
};

const isExpired = (expiresAt?: string | null) => {
  if (!expiresAt) return false;
  return new Date(expiresAt).getTime() < Date.now();
};

// 当前选中的完整令牌字符串 (用于复制和配置文件填充)
const currentSelectedTokenStr = computed(() => {
  if (!selectedTokenId.value) return '';
  const item = tokenList.value.find((t) => t.id === selectedTokenId.value);
  return item ? item.token : '';
});

// 令牌选择下拉选项
const tokenSelectOptions = computed(() =>
  tokenList.value.map((item) => ({
    label: `${item.name} (${item.maskedToken || (item.token.length > 14 ? `${item.token.slice(0, 10)}...${item.token.slice(-4)}` : item.token)})`,
    value: item.id,
    disabled: item.isRevoked || isExpired(item.expiresAt),
  }))
);

// ================= 复制文本工具 =================
const copyText = async (text: string, label: string) => {
  try {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      await navigator.clipboard.writeText(text);
    } else {
      const textarea = document.createElement('textarea');
      textarea.value = text;
      document.body.appendChild(textarea);
      textarea.select();
      document.execCommand('copy');
      document.body.removeChild(textarea);
    }
    MessagePlugin.success(`${label}已复制至剪贴板`);
  } catch {
    MessagePlugin.error('复制失败，请手动选中文本复制');
  }
};

// ================= 1. 一句话接入模式预设文本 =================
const oneSentencePrompt = computed(() => {
  const token = currentSelectedTokenStr.value || 'YOUR_MCP_TOKEN';
  const skillUrl = mcpUrls.value.skillMdUrl;

  return `你是一个专业的 MSLX Minecraft 服务器智能运维助手。你将通过本地 MCP (Model Context Protocol) 接口协助管理员管理服务器。

【服务接入信息】
- Streamable HTTP：${mcpUrls.value.streamableHttpUrl}
- 旧版 SSE：${mcpUrls.value.sseUrl}
- 运维规范与工具字典：${skillUrl}

【访问令牌】
在 HTTP 请求头中附加 "Authorization: Bearer ${token}"。

【使用指导】
请直接参考上述 Skill 规范 (${skillUrl}) 中的运维指南与工具字典，通过上述 MCP 端点调取工具来执行所有 Minecraft 服务器运维操作。`;
});

// ================= 3. 标准 MCP JSON 通用配置 =================
const standardMcpConfig = computed(() => {
  const token = currentSelectedTokenStr.value || 'YOUR_MCP_TOKEN';
  return JSON.stringify(
    {
      mcpServers: {
        'mslx-server-ops': {
          url: mcpUrls.value.streamableHttpUrl,
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

// ================= 令牌创建与吊销 =================
const openCreateTokenModal = () => {
  tokenForm.value = {
    name: '',
    expireDays: 30,
  };
  createTokenModalVisible.value = true;
};

const handleCreateToken = async () => {
  if (!tokenForm.value.name.trim()) {
    MessagePlugin.warning('请输入令牌备注名称');
    return;
  }
  createTokenSubmitting.value = true;
  try {
    const res = await createMcpToken({
      name: tokenForm.value.name.trim(),
      expireDays: tokenForm.value.expireDays,
    });
    createTokenModalVisible.value = false;
    newlyCreatedToken.value = res;
    if (res?.id) {
      selectedTokenId.value = res.id;
    }
    createdTokenDialogVisible.value = true;
    MessagePlugin.success('访问令牌生成成功');
    await loadTokens();
  } catch (err: any) {
    MessagePlugin.error(`创建失败: ${err.message || err}`);
  } finally {
    createTokenSubmitting.value = false;
  }
};

const handleRevokeToken = async (item: McpTokenModel) => {
  try {
    await revokeMcpToken(item.id);
    MessagePlugin.success(`已吊销令牌: ${item.name}`);
    await loadTokens();
  } catch (err: any) {
    MessagePlugin.error(`吊销失败: ${err.message || err}`);
  }
};

const previewSkillMd = () => {
  window.open(mcpUrls.value.skillMdUrl, '_blank');
};

onMounted(() => {
  loadTokens();
});
</script>

<template>
  <div class="mcp-management-view space-y-6 pb-6">
    <!-- 顶部状态栏与切换模式入口 -->
    <div class="flex items-center justify-between p-3.5 rounded-xl border border-[var(--td-component-border)] bg-[var(--td-bg-color-container)]">
      <div class="flex items-center gap-2.5">
        <div class="w-2.5 h-2.5 rounded-full bg-emerald-500 shadow-[0_0_8px_rgba(16,185,129,0.5)]"></div>
        <div>
          <span class="text-xs font-bold text-[var(--td-text-color-primary)]">MCP 本地服务就绪</span>
          <span class="text-[11px] text-[var(--td-text-color-secondary)] ml-2">复用宿主端口，无额外网络占用</span>
        </div>
      </div>
      <div class="flex items-center gap-2">
        <t-button variant="outline" size="small" @click="emit('switchMode')">
          <template #icon><swap-icon /></template>
          切换工作模式
        </t-button>
        <t-button variant="text" size="small" shape="square" @click="loadTokens">
          <template #icon><refresh-icon /></template>
        </t-button>
      </div>
    </div>

    <!-- 1. 一句话接入模式 (最上方) -->
    <div class="design-card rounded-xl border border-[var(--td-component-border)] bg-[var(--td-bg-color-container)] p-4 sm:p-5">
      <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-[var(--td-component-border)]">
        <div>
          <h3 class="text-sm font-bold text-[var(--td-text-color-primary)] m-0">
            一句话接入模式
          </h3>
          <p class="text-xs text-[var(--td-text-color-secondary)] mt-1 mb-0 leading-relaxed">
            适用于豆包、Workbuddy、各类本地智能体或纯提示词 AI。已将 MCP 端点、Skill 规范与访问令牌整合成标准预设指令，直接复制粘贴至客户端即可。
          </p>
        </div>

        <div class="flex items-center gap-2 shrink-0">
          <t-button theme="primary" size="small" @click="copyText(oneSentencePrompt, '一句话接入预设指令')">
            <template #icon><copy-icon /></template>
            一键复制预设指令
          </t-button>
        </div>
      </div>

      <!-- 令牌选择关联 -->
      <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-2 mt-3.5 mb-2">
        <div class="text-xs text-[var(--td-text-color-secondary)] flex items-center gap-2">
          <span>带入的访问令牌:</span>
          <t-select
            v-if="tokenList.length > 0"
            v-model="selectedTokenId"
            :options="tokenSelectOptions"
            size="small"
            style="width: 260px"
            placeholder="选择带入的访问令牌"
          />
          <span v-else class="text-amber-500 font-medium text-xs">
            暂无有效令牌，请在下方创建
          </span>
        </div>
        <span class="text-[11px] text-[var(--td-text-color-placeholder)]">
          复制后直接粘贴到客户端的系统人设或对话输入框
        </span>
      </div>

      <!-- 预设指令预览 -->
      <div class="relative bg-[#1e1e1e] dark:bg-[#141414] text-[#d4d4d4] rounded-xl p-3.5 font-mono text-xs overflow-x-auto max-h-[200px] shadow-inner border border-[var(--td-component-border)] select-all leading-relaxed whitespace-pre-wrap">
        {{ oneSentencePrompt }}
      </div>
    </div>

    <!-- 2. 访问令牌管理 (中间) -->
    <div class="design-card rounded-xl border border-[var(--td-component-border)] bg-[var(--td-bg-color-container)] p-4 sm:p-5">
      <div class="flex items-center justify-between pb-3 border-b border-[var(--td-component-border)]">
        <div>
          <h3 class="text-sm font-bold text-[var(--td-text-color-primary)] m-0">
            访问令牌管理
          </h3>
          <p class="text-xs text-[var(--td-text-color-secondary)] mt-1 mb-0">
            MCP 接口使用独立的访问令牌进行安全鉴权，不依赖宿主登录状态。支持按需设置有效期或随时吊销。
          </p>
        </div>
        <t-button theme="primary" size="small" @click="openCreateTokenModal">
          <template #icon><add-icon /></template>
          新建令牌
        </t-button>
      </div>

      <!-- 令牌列表表格 (标准 TDesign Table) -->
      <div class="mt-4">
        <t-table
          :data="tokenList"
          :columns="columns"
          :loading="tokensLoading"
          row-key="id"
          size="small"
          hover
          :empty="tokensLoading ? '正在加载令牌...' : '暂无 MCP 访问令牌，请点击右上角新建'"
        >
          <template #name="{ row }">
            <span class="font-bold text-xs text-[var(--td-text-color-primary)]">{{ row.name }}</span>
          </template>

          <template #token="{ row }">
            <div class="flex items-center gap-1.5">
              <code class="text-xs bg-[var(--td-bg-color-secondarycontainer)] px-1.5 py-0.5 rounded text-[var(--td-text-color-secondary)] font-mono">
                {{ row.maskedToken || row.token }}
              </code>
              <t-tooltip content="复制完整令牌">
                <t-button variant="text" shape="square" size="small" @click="copyText(row.token, '完整访问令牌')">
                  <template #icon><copy-icon /></template>
                </t-button>
              </t-tooltip>
            </div>
          </template>

          <template #expiresAt="{ row }">
            <span v-if="!row.expiresAt" class="text-xs text-[var(--td-text-color-secondary)]">永久有效</span>
            <span v-else-if="isExpired(row.expiresAt)" class="text-xs text-rose-500">已过期</span>
            <span v-else class="text-xs text-[var(--td-text-color-secondary)]">{{ row.expiresAt }}</span>
          </template>

          <template #status="{ row }">
            <t-tag v-if="row.isRevoked" theme="danger" variant="light" size="small">已吊销</t-tag>
            <t-tag v-else-if="isExpired(row.expiresAt)" theme="warning" variant="light" size="small">已过期</t-tag>
            <t-tag v-else theme="success" variant="light" size="small">有效</t-tag>
          </template>

          <template #action="{ row }">
            <t-popconfirm
              v-if="!row.isRevoked"
              content="确定要吊销此令牌吗？吊销后使用该令牌的 Agent 将无法再调用 MCP 接口。"
              theme="danger"
              @confirm="handleRevokeToken(row)"
            >
              <t-button variant="text" theme="danger" size="small">吊销</t-button>
            </t-popconfirm>
            <span v-else class="text-xs text-[var(--td-text-color-placeholder)]">-</span>
          </template>
        </t-table>
      </div>
    </div>

    <!-- 3. 标准接口配置 (最下方) -->
    <div class="design-card rounded-xl border border-[var(--td-component-border)] bg-[var(--td-bg-color-container)] p-4 sm:p-5">
      <div class="pb-3 border-b border-[var(--td-component-border)]">
        <h3 class="text-sm font-bold text-[var(--td-text-color-primary)] m-0">
          标准接口配置
        </h3>
        <p class="text-xs text-[var(--td-text-color-secondary)] mt-1 mb-0">
          适用于支持标准 MCP 协议客户端的通用接口地址与配置规范。
        </p>
      </div>

      <div class="mt-4 space-y-4">
        <!-- 端点列表 -->
        <div class="grid grid-cols-1 gap-2.5">
          <!-- Streamable HTTP 端点 -->
          <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-2 p-2.5 rounded-lg bg-[var(--td-bg-color-secondarycontainer)] border border-[var(--td-component-border)]">
            <div class="min-w-0">
              <div class="text-[11px] font-bold text-[var(--td-text-color-secondary)]">Streamable HTTP</div>
              <div class="text-xs font-mono text-[var(--td-text-color-primary)] truncate mt-0.5 select-all">{{ mcpUrls.streamableHttpUrl }}</div>
            </div>
            <t-button size="small" variant="outline" class="shrink-0" @click="copyText(mcpUrls.streamableHttpUrl, 'Streamable HTTP 地址')">
              <template #icon><copy-icon /></template>
              复制 HTTP 地址
            </t-button>
          </div>

          <!-- 旧版 SSE 端点 -->
          <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-2 p-2.5 rounded-lg bg-[var(--td-bg-color-secondarycontainer)] border border-[var(--td-component-border)]">
            <div class="min-w-0">
              <div class="text-[11px] font-bold text-[var(--td-text-color-secondary)]">旧版 SSE</div>
              <div class="text-xs font-mono text-[var(--td-text-color-primary)] truncate mt-0.5 select-all">{{ mcpUrls.sseUrl }}</div>
            </div>
            <t-button size="small" variant="outline" class="shrink-0" @click="copyText(mcpUrls.sseUrl, 'SSE 地址')">
              <template #icon><copy-icon /></template>
              复制 SSE 地址
            </t-button>
          </div>

          <!-- Skill.md 规范 -->
          <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-2 p-2.5 rounded-lg bg-[var(--td-bg-color-secondarycontainer)] border border-[var(--td-component-border)]">
            <div class="min-w-0">
              <div class="text-[11px] font-bold text-[var(--td-text-color-secondary)] uppercase">Skill 规范直连 (GET Markdown)</div>
              <div class="text-xs font-mono text-[var(--td-text-color-primary)] truncate mt-0.5 select-all">{{ mcpUrls.skillMdUrl }}</div>
            </div>
            <div class="flex items-center gap-1.5 shrink-0">
              <t-button size="small" variant="outline" @click="copyText(mcpUrls.skillMdUrl, 'Skill.md 规范地址')">
                <template #icon><copy-icon /></template>
                复制链接
              </t-button>
              <t-button size="small" variant="dashed" @click="previewSkillMd">
                <template #icon><browse-icon /></template>
                在线预览
              </t-button>
            </div>
          </div>
        </div>

        <!-- 通用 JSON 配置 (带令牌选择与完整 Token 填充) -->
        <div class="mt-4 pt-3 border-t border-[var(--td-component-border)]">
          <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-2 mb-2">
            <div class="flex items-center gap-2 text-xs text-[var(--td-text-color-secondary)]">
              <span class="font-bold text-[var(--td-text-color-primary)]">配置文件填入的令牌:</span>
              <t-select
                v-if="tokenList.length > 0"
                v-model="selectedTokenId"
                :options="tokenSelectOptions"
                size="small"
                style="width: 260px"
                placeholder="选择配置使用的访问令牌"
              />
              <span v-else class="text-amber-500 font-medium text-xs">
                暂无有效令牌，将使用占位符
              </span>
            </div>
            <div class="flex items-center gap-2 shrink-0">
              <t-button size="small" variant="outline" @click="copyText(standardMcpConfig, '通用 MCP JSON 配置')">
                <template #icon><copy-icon /></template>
                复制配置 (含完整 Token)
              </t-button>
            </div>
          </div>
          <div class="bg-[#1e1e1e] dark:bg-[#141414] text-[#9cdcfe] rounded-xl p-3.5 font-mono text-xs overflow-x-auto shadow-inner border border-[var(--td-component-border)] select-all">
            <pre class="m-0">{{ standardMcpConfig }}</pre>
          </div>
        </div>
      </div>
    </div>

    <!-- 新建令牌弹窗 -->
    <t-dialog
      v-model:visible="createTokenModalVisible"
      header="新建 MCP 访问令牌"
      width="440px"
      attach="body"
      :confirm-btn="{ content: '生成令牌', loading: createTokenSubmitting }"
      @confirm="handleCreateToken"
    >
      <div class="space-y-4 py-2">
        <t-form-item label="备注名称" required-mark>
          <t-input
            v-model="tokenForm.name"
            placeholder="例如：本地 Workbuddy、公司 Cursor"
            maxlength="30"
          />
        </t-form-item>

        <t-form-item label="有效期">
          <t-select v-model="tokenForm.expireDays">
            <t-option :value="7" label="7 天" />
            <t-option :value="30" label="30 天 (推荐)" />
            <t-option :value="90" label="90 天" />
            <t-option :value="180" label="180 天" />
            <t-option :value="0" label="永久有效" />
          </t-select>
        </t-form-item>
      </div>
    </t-dialog>

    <!-- 令牌生成成功提示明文弹窗 -->
    <t-dialog
      v-model:visible="createdTokenDialogVisible"
      header="令牌生成成功"
      width="480px"
      attach="body"
      :footer="false"
    >
      <div class="py-2 space-y-4">
        <t-alert theme="warning" class="text-xs">
          请立即复制并妥善保管此令牌！出于安全考虑，完整的明文令牌仅在此处展示一次，关闭后将无法再次查看完整内容。
        </t-alert>

        <div v-if="newlyCreatedToken" class="space-y-2">
          <div class="text-xs font-bold text-[var(--td-text-color-primary)]">
            {{ newlyCreatedToken.name }}
          </div>
          <div class="p-3 bg-[var(--td-bg-color-secondarycontainer)] rounded-lg font-mono text-xs text-[var(--td-text-color-primary)] break-all border border-[var(--td-component-border)] select-all">
            {{ newlyCreatedToken.token }}
          </div>
        </div>

        <div class="flex justify-end gap-2 pt-2">
          <t-button
            theme="primary"
            @click="copyText(newlyCreatedToken?.token || '', '完整令牌'); createdTokenDialogVisible = false;"
          >
            <template #icon><copy-icon /></template>
            复制并完成
          </t-button>
        </div>
      </div>
    </t-dialog>
  </div>
</template>
