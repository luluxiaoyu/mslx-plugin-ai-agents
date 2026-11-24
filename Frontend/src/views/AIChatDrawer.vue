<script setup lang="ts">
import { ref, watch, onMounted, onBeforeUnmount } from 'vue';
import { useRoute } from 'vue-router';
import { MessagePlugin, Loading as TLoading } from 'tdesign-vue-next';
import { Chat as TChat, ChatItem as TChatItem, ChatSender as TChatSender } from '@tdesign-vue-next/chat';
import '@tdesign-vue-next/chat/es/style/index.css';
import { MdPreview, type Themes } from 'md-editor-v3';
import 'md-editor-v3/lib/preview.css';
import {
  DesktopIcon,
  DeleteIcon,
  ErrorCircleFilledIcon,
  CheckCircleIcon,
  CloseCircleIcon,
  CheckCircleFilledIcon,
  CloseCircleFilledIcon,
  CodeIcon,
  CopyIcon,
  RefreshIcon,
  LightbulbIcon,
  CloseIcon,
  ViewListIcon,
  MoveIcon,
  SettingIcon,
  SwapIcon,
} from 'tdesign-icons-vue-next';
import ModeSelectView from './ModeSelectView.vue';
import McpManagementView from './McpManagementView.vue';
import {
  sendAiChatStream,
  abortAiChatStream,
  type ChatMessage,
  confirmAiToolAction,
  getAiSettings,
  updateAiSettings,
} from '../api/aiApi';

const props = defineProps<{
  visible: boolean;
}>();

const emit = defineEmits<{
  (e: 'update:visible', val: boolean): void;
}>();

const route = useRoute();

const mdTheme = ref<Themes>('light');
let themeObserver: MutationObserver | null = null;

const updateTheme = () => {
  const isDark = document.documentElement.getAttribute('theme-mode') === 'dark';
  mdTheme.value = isDark ? 'dark' : 'light';
};

const getUserInfo = () => {
  try {
    const stores = (window as any).MSLX_Stores;
    const store = stores?.useUserStore ? stores.useUserStore() : (stores?.getUserStore ? stores.getUserStore() : null);
    return {
      name: store?.userInfo?.name || store?.userInfo?.username || '用户',
      avatar: store?.userInfo?.avatar || 'https://tdesign.gtimg.com/site/avatar.jpg',
    };
  } catch {
    return { name: '用户', avatar: 'https://tdesign.gtimg.com/site/avatar.jpg' };
  }
};

interface DisplayMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  time?: string;
  toolData?: { tool: string; data: any };
  toolExpanded?: string[];
}

const AI_CHAT_HISTORY_KEY = 'mslx_ai_chat_history';

const displayMode = ref<'drawer' | 'floating'>(
  (localStorage.getItem('mslx_ai_display_mode') as 'drawer' | 'floating') || 'drawer'
);

// ================= 工作模式选择与记忆 =================
const selectedMode = ref<'mcp' | 'apikey' | null>(
  (localStorage.getItem('mslx_ai_selected_mode') as 'mcp' | 'apikey') || null
);

const handleSelectMode = (mode: 'mcp' | 'apikey') => {
  selectedMode.value = mode;
  localStorage.setItem('mslx_ai_selected_mode', mode);
};

const handleSwitchToModeSelect = () => {
  selectedMode.value = null;
  localStorage.removeItem('mslx_ai_selected_mode');
};

// ================= API Key 模型设置弹窗 =================
const settingsModalVisible = ref(false);
const settingsLoading = ref(false);
const settingsSubmitting = ref(false);

const aiSettingsForm = ref({
  aiApiKey: '',
  aiBaseUrl: 'https://api.deepseek.com/v1',
  aiModelName: 'deepseek-chat',
});

const openSettingsModal = async () => {
  settingsModalVisible.value = true;
  settingsLoading.value = true;
  try {
    const res = await getAiSettings();
    if (res) {
      aiSettingsForm.value = {
        aiApiKey: res.aiApiKey || '',
        aiBaseUrl: res.aiBaseUrl || 'https://api.deepseek.com/v1',
        aiModelName: res.aiModelName || 'deepseek-chat',
      };
    }
  } catch (e: any) {
    console.warn('加载设置失败:', e);
  } finally {
    settingsLoading.value = false;
  }
};

const handleSaveAiSettings = async () => {
  if (!aiSettingsForm.value.aiApiKey.trim()) {
    MessagePlugin.warning('请输入 API Key');
    return;
  }
  settingsSubmitting.value = true;
  try {
    await updateAiSettings({
      aiEnabled: true,
      aiApiKey: aiSettingsForm.value.aiApiKey.trim(),
      aiBaseUrl: aiSettingsForm.value.aiBaseUrl.trim() || 'https://api.deepseek.com/v1',
      aiModelName: aiSettingsForm.value.aiModelName.trim() || 'deepseek-chat',
    });
    MessagePlugin.success('AI 助手模型配置保存成功');
    settingsModalVisible.value = false;
  } catch (e: any) {
    MessagePlugin.error(`保存失败: ${e.message || e}`);
  } finally {
    settingsSubmitting.value = false;
  }
};

const floatPos = ref<{ x: number; y: number }>({
  x: Math.max(20, window.innerWidth - 580),
  y: 80,
});

let isDragging = false;
let dragOffset = { x: 0, y: 0 };

const startDrag = (e: MouseEvent) => {
  const target = e.target as HTMLElement;
  if (target.closest('.header-actions') || target.closest('.clear-btn') || target.closest('.t-button')) return;
  isDragging = true;
  dragOffset.x = e.clientX - floatPos.value.x;
  dragOffset.y = e.clientY - floatPos.value.y;
  window.addEventListener('mousemove', onDrag);
  window.addEventListener('mouseup', stopDrag);
};

const onDrag = (e: MouseEvent) => {
  if (!isDragging) return;
  const newX = Math.max(0, Math.min(window.innerWidth - 300, e.clientX - dragOffset.x));
  const newY = Math.max(0, Math.min(window.innerHeight - 150, e.clientY - dragOffset.y));
  floatPos.value = { x: newX, y: newY };
};

const stopDrag = () => {
  isDragging = false;
  window.removeEventListener('mousemove', onDrag);
  window.removeEventListener('mouseup', stopDrag);
};

const toggleDisplayMode = () => {
  displayMode.value = displayMode.value === 'drawer' ? 'floating' : 'drawer';
  localStorage.setItem('mslx_ai_display_mode', displayMode.value);
};

const formatMarkdown = (raw: string): string => {
  if (!raw) return '';
  let text = raw;

  // 1. 彻底剔除 <<<SUGGESTIONS:...>>> 及其任何未完成前缀（支持多行匹配及流式末尾截断）
  text = text.replace(/<<<SUGGESTIONS:?[\s\S]*?(?:>>>|$)/gi, '');
  text = text.replace(/<<<SUGGESTIONS:?[\s\S]*$/gi, '');
  text = text.replace(/<<<[\w]*$/gi, '');

  // 剔除任何历史或分片遗留的孤立 GGEST / SUGGESTIONS 碎片（如折腾配置。GGEST）
  text = text.replace(/([。！？.!?\s]|^)\s*(?:SUGGESTIONS?|GGEST(?:IONS)?)\b[\s\S]*$/gi, '$1');
  text = text.replace(/(?:^|\s)(?:SUGGESTIONS?|GGEST(?:IONS)?)[:\[][\s\S]*$/gi, '');
  text = text.replace(/(?:^|\s|\b)GGEST\b[\s\S]*$/gi, '');

  // 2. 移除完整的或流式中的 DSML 标签
  text = text.replace(/<｜DSML｜[\s\S]*?(?:<\/｜DSML｜\w+>|$)/gi, '');
  text = text.replace(/<｜DSML｜[\s\S]*$/gi, '');

  // 3. 修复标题缺少空格（CommonMark 规范要求 # 后面必须加空格才能被正确解析为 h1~h6 标签）
  // 匹配行首或换行后的 1~6 个 # 后面紧跟非空格、非 # 字符，自动插入空格
  // 例如：###👥玩家与权限治理 -> ### 👥玩家与权限治理
  text = text.replace(/(^|\n)(#{1,6})([^\s#])/g, '$1$2 $3');

  // 4. 修复无序列表与有序列表缺少空格问题（例如 -在线玩家 -> - 在线玩家）
  text = text.replace(/(^|\n)([-*+])([^\s\-*+])/g, '$1$2 $3');
  text = text.replace(/(^|\n)(\d+\.)([^\s\d])/g, '$1$2 $3');

  return text.trim();
};

const cleanStreamedContent = (raw: string): string => formatMarkdown(raw);

const formatTime = (d: Date = new Date()): string => {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
};

const defaultWelcomeMessage: DisplayMessage = {
  id: 'welcome',
  role: 'assistant',
  content: '你好！我是 MSLX AI 智能运维助手。你可以随时与我交流，例如切换 Java 版本、修改参数、重启服务器或创建新的 Minecraft 服务端。',
};

const getCurrentServerId = (): number | null => {
  try {
    if (route && route.params && route.params.id) {
      const idNum = parseInt(String(route.params.id), 10);
      if (!isNaN(idNum) && idNum > 0) {
        return idNum;
      }
    }
    const match = route?.path?.match(/\/instance\/[^\/]+\/(\d+)/);
    if (match) {
      return parseInt(match[1], 10);
    }
  } catch (e) {
    console.warn('获取当前页面 Server ID 失败:', e);
  }
  return null;
};

const loadHistory = (): DisplayMessage[] => {
  try {
    const saved = localStorage.getItem(AI_CHAT_HISTORY_KEY);
    if (saved) {
      const parsed = JSON.parse(saved);
      if (Array.isArray(parsed) && parsed.length > 0) {
        const cleaned = parsed
          .filter((m) => !(m.role === 'assistant' && m.content === '正在思考与处理...' && !m.toolData))
          .map((m) => {
            if (m.role === 'assistant' && m.content === '正在思考与处理...') {
              return { ...m, content: '⚠️ 上次对话因页面刷新而中断。' };
            }
            if (m.role === 'assistant') {
              return { ...m, content: cleanStreamedContent(m.content) };
            }
            return m;
          });
        if (cleaned.length > 0) return cleaned;
      }
    }
  } catch (e) {
    console.error('加载 AI 聊天历史记录异常:', e);
  }
  return [defaultWelcomeMessage];
};

const chatList = ref<DisplayMessage[]>(loadHistory());
const inputText = ref('');
const loading = ref(false);
const suggestedReplies = ref<string[]>([]);
const confirmingToolId = ref<string | null>(null);
const activeStreamingId = ref<string | null>(null);

watch(
  chatList,
  (newVal) => {
    try {
      const toSave = newVal.filter(
        (m) => !(m.role === 'assistant' && m.content === '正在思考与处理...' && !m.toolData)
      );
      localStorage.setItem(AI_CHAT_HISTORY_KEY, JSON.stringify(toSave));
    } catch (e) {
      console.error('保存 AI 聊天历史失败:', e);
    }
  },
  { deep: true }
);

const handleClose = () => {
  emit('update:visible', false);
};

const handleClearHistory = () => {
  if (loading.value) {
    handleAbort();
  }
  chatList.value = [{ ...defaultWelcomeMessage, id: `welcome_${Date.now()}` }];
  suggestedReplies.value = [];
  localStorage.removeItem(AI_CHAT_HISTORY_KEY);
  MessagePlugin.success('AI 对话历史已成功清空');
};

const handleAbort = () => {
  if (!loading.value) return;
  abortAiChatStream();
  loading.value = false;

  const lastMsg = chatList.value[chatList.value.length - 1];
  if (lastMsg && lastMsg.role === 'assistant') {
    if (lastMsg.content === '正在思考与处理...') {
      lastMsg.content = '🛑 对话已被用户主动中断。';
    } else {
      lastMsg.content += '\n\n🛑 对话已被用户主动中断。';
    }
  }
  MessagePlugin.warning('已手动中断 AI 对话响应');
};

const cleanToolResultText = (text: string): string =>
  text.replace(/^\[(?:SUCCESS|ERROR|INFO|CANCELLED|PENDING_CONFIRMATION|REQUIRES_CONFIRMATION)\]\s*/, '');

const toolFriendlyNames: Record<string, string> = {
  create_server: '下载与部署服务端核心',
  create_mc_server: '下载与部署服务端核心',
  query_available_server_cores: '查询可用服务端核心',
  query_java_environments: '扫描 Java 运行环境',
  install_online_java: '在线下载与安装 Java 环境',
  query_creation_status: '查询开服进度',
  update_instance_settings: '修改服务器配置',
  update_server_config: '更新服务端配置文件',
  list_server_files: '读取服务器文件列表',
  read_server_file: '读取文件内容',
  read_server_log: '读取崩溃/运行日志',
  copy_server_file: '复制文件与目录',
  move_server_file: '移动/重命名文件',
  query_system_metrics: '监测系统与实例性能',
  list_server_mods_plugins: '读取 Mod/插件列表',
  write_server_file: '写入/修改文件内容',
  delete_server_file: '删除服务器文件',
  control_server: '控制服务器状态(启动/停止/重启)',
  query_server_status: '查询服务器在线状态',
  force_kill_server: '强制终止服务器进程',
  delete_server_instance: '彻底删除服务器实例',
  agree_server_eula: '同意服务器 EULA 协议',
  query_players: '查询在线与历史玩家',
  manage_whitelist: '管理服务器白名单',
  manage_ops: '管理服务器管理员 (OP)',
  manage_bans: '管理玩家及 IP 封禁',
  create_server_backup: '创建服务器快照备份',
  list_server_backups: '获取服务器备份历史',
  restore_server_backup: '还原服务器备份',
  delete_server_backup: '删除指定历史备份',
  query_frp_status: '查询内网穿透状态与地址',
  control_frp_tunnel: '控制内网穿透隧道',
  manage_scheduled_tasks: '管理服务器定时任务',
  search_mc_resources: '搜索 Modrinth/CurseForge 资源',
  query_resource_versions: '查询资源可用版本',
  download_file_to_server: '下载文件至服务器目录',
  audit_client_only_mods: '深度排查纯客户端模组',
  manage_mod_plugin_state: '批量启停/删除模组或插件',
  query_python_environments: '探测 Python 及 MCDR 环境',
  extract_archive: '解压服务器压缩包',
  compress_files: '压缩服务器文件',
};

const getToolFriendlyName = (toolName?: string): string => {
  if (!toolName) return '思考与处理';
  return toolFriendlyNames[toolName] || toolName;
};

const appendToolResult = (msg: DisplayMessage, resultText: string) => {
  const text = cleanToolResultText(resultText) || '操作已处理。';
  if (!msg.content || msg.content === '正在思考与处理...') {
    msg.content = text;
  } else {
    msg.content = `${msg.content}\n\n${text}`;
  }
};

const syncHostStores = (toolName?: string) => {
  try {
    const stores = (window as any).MSLX_Stores;
    const pinia = (window as any).Pinia;

    // 1. 同步后台任务状态（创建服务端、在线安装 Java、备份、文件解压缩、文件下载、实例设置等）
    const taskRelatedTools = [
      'create_server',
      'create_mc_server',
      'install_online_java',
      'create_server_backup',
      'restore_server_backup',
      'delete_server_backup',
      'download_file_to_server',
      'compress_files',
      'extract_archive',
      'update_instance_settings',
      'update_server_config'
    ];

    const isTaskTool = !toolName || taskRelatedTools.includes(toolName);
    if (isTaskTool) {
      if (stores && typeof stores.useTaskStore === 'function') {
        const taskStore = stores.useTaskStore();
        taskStore?.fetchTasks?.();
        taskStore?.startPolling?.();
        console.log('[MSLX AI] 已同步宿主 Pinia 后台任务状态 (fetchTasks & startPolling)');
      } else if (pinia?.getActivePinia) {
        const activePinia = pinia.getActivePinia();
        const taskStore = activePinia?._s?.get('backgroundTask');
        taskStore?.fetchTasks?.();
        taskStore?.startPolling?.();
      }
    }

    // 2. 同步实例列表（创建服务端、删除服务端、修改实例设置、启动/停止/重启服务器等）
    const instanceRelatedTools = [
      'create_server',
      'create_mc_server',
      'delete_server_instance',
      'update_instance_settings',
      'update_server_config',
      'control_server',
      'force_kill_server'
    ];

    const isInstanceTool = !toolName || instanceRelatedTools.includes(toolName);
    if (isInstanceTool) {
      if (stores && typeof stores.useInstanceListStore === 'function') {
        const instanceStore = stores.useInstanceListStore();
        instanceStore?.refreshInstanceList?.();
        console.log('[MSLX AI] 已同步宿主 Pinia 实例列表状态 (refreshInstanceList)');
      } else if (pinia?.getActivePinia) {
        const activePinia = pinia.getActivePinia();
        const instanceStore = activePinia?._s?.get('instanceList');
        instanceStore?.refreshInstanceList?.();
      }
    }

    // 3. 同步内网穿透 FRP 列表
    const frpRelatedTools = ['control_frp_tunnel'];
    if (toolName && frpRelatedTools.includes(toolName)) {
      if (stores && typeof stores.useTunnelsStore === 'function') {
        const tunnelsStore = stores.useTunnelsStore();
        tunnelsStore?.getTunnels?.();
        console.log('[MSLX AI] 已同步宿主 Pinia 内网穿透状态 (getTunnels)');
      } else if (pinia?.getActivePinia) {
        const activePinia = pinia.getActivePinia();
        const tunnelsStore = activePinia?._s?.get('tunnels');
        tunnelsStore?.getTunnels?.();
      }
    }
  } catch (err) {
    console.warn('[MSLX AI] 同步宿主 Pinia Store 状态失败:', err);
  }
};

const handleConfirmToolAction = async (msg: DisplayMessage) => {
  if (!msg.toolData?.data?.confirmationId || confirmingToolId.value) return;
  confirmingToolId.value = msg.id;
  try {
    const result = await confirmAiToolAction(msg.toolData.data.confirmationId, true);
    const resultData = (result.data || {}) as Record<string, any>;
    msg.toolData.data = { ...msg.toolData.data, ...resultData, confirmed: true };
    appendToolResult(msg, result.message);
    if (result.message && result.message.includes('[ERROR]')) {
      MessagePlugin.error(cleanToolResultText(result.message));
    } else {
      MessagePlugin.success('已确认授权，操作已执行');
      syncHostStores(msg.toolData.tool);
    }
  } catch (e: any) {
    MessagePlugin.error(`确认失败: ${e.message || e}`);
  } finally {
    confirmingToolId.value = null;
  }
};

const handleRejectToolAction = async (msg: DisplayMessage) => {
  if (!msg.toolData?.data?.confirmationId || confirmingToolId.value) return;
  confirmingToolId.value = msg.id;
  try {
    const result = await confirmAiToolAction(msg.toolData.data.confirmationId, false);
    msg.toolData.data = { ...msg.toolData.data, rejected: true, confirmed: false };
    appendToolResult(msg, result.message || '已拒绝该敏感操作。');
    MessagePlugin.info('已拒绝该敏感文件操作');
  } catch (e: any) {
    MessagePlugin.error(`操作失败: ${e.message || e}`);
  } finally {
    confirmingToolId.value = null;
  }
};

const handleQuickSend = (text: string) => {
  inputText.value = text;
  handleSend();
};

const handleCopyMessage = async (msg: DisplayMessage) => {
  try {
    await navigator.clipboard.writeText(msg.content);
    MessagePlugin.success('已复制回复内容');
  } catch {
    MessagePlugin.error('复制失败');
  }
};

const handleReplayMessage = (msg: DisplayMessage) => {
  const idx = chatList.value.findIndex((m) => m.id === msg.id);
  if (idx <= 0) {
    MessagePlugin.warning('没有可重发的上一条用户消息');
    return;
  }
  const prevUser = chatList.value[idx - 1];
  if (!prevUser || prevUser.role !== 'user') {
    MessagePlugin.warning('没有可重发的上一条用户消息');
    return;
  }
  chatList.value = chatList.value.slice(0, idx);
  inputText.value = prevUser.content;
  handleSend();
};

const handleSend = async (value?: string) => {
  const query = (value ?? inputText.value).trim();
  if (!query || loading.value) return;

  const userMsgId = `user_${Date.now()}`;
  chatList.value.push({
    id: userMsgId,
    role: 'user',
    content: query,
    time: formatTime(),
  });

  inputText.value = '';
  loading.value = true;

  const botMsgId = `bot_${Date.now()}`;
  chatList.value.push({
    id: botMsgId,
    role: 'assistant',
    content: '正在思考与处理...',
    time: formatTime(),
    toolExpanded: [],
  });
  activeStreamingId.value = botMsgId;

  const activeServerId = getCurrentServerId();

  const apiMessages: ChatMessage[] = [
    ...(activeServerId
      ? [
          {
            role: 'system' as const,
            content: `【当前用户界面上下文】：用户目前正处于服务器 (ID: ${activeServerId}) 的控制台或设置页面。如果用户提出的操作指令未显式指明服务器 ID（如说“把 Java 切换成 25”、“重启服务器”、“修改端口”），请直接默认作用于该服务器 (ID: ${activeServerId})！`,
          },
        ]
      : []),
    ...chatList.value
      .filter((m) => m.id !== botMsgId)
      .map((m) => ({
        role: m.role,
        content: m.content,
      })),
  ];

  let isFirstChunk = true;

  await sendAiChatStream(
    apiMessages,
    (chunk) => {
      const target = chatList.value.find((m) => m.id === botMsgId);
      if (target) {
        if (isFirstChunk) {
          target.content = chunk;
          isFirstChunk = false;
        } else {
          target.content += chunk;
        }
      }
    },
    (toolName, toolData) => {
      if (toolName === 'suggested_replies' && Array.isArray(toolData)) {
        suggestedReplies.value = toolData;
        return;
      }
      const target = chatList.value.find((m) => m.id === botMsgId);
      if (target) {
        target.toolData = { tool: toolName, data: toolData };
        target.toolExpanded = [];
      }
      syncHostStores(toolName);
    },
    (err) => {
      const target = chatList.value.find((m) => m.id === botMsgId);
      activeStreamingId.value = null;
      if (target) {
        target.content = `❌ ${err}`;
      } else {
        MessagePlugin.error(err);
      }
    },
    () => {
      const target = chatList.value.find((m) => m.id === botMsgId);
      activeStreamingId.value = null;
      if (target) {
        target.content = cleanStreamedContent(target.content);
        if (target.content === '正在思考与处理...') {
          target.content = '';
        }
        target.time = formatTime();
        if (target.toolData?.tool) {
          syncHostStores(target.toolData.tool);
          setTimeout(() => syncHostStores(target?.toolData?.tool), 1500);
        }
      }
      loading.value = false;
    }
  );
};

onMounted(() => {
  updateTheme();
  themeObserver = new MutationObserver(() => {
    updateTheme();
  });
  themeObserver.observe(document.documentElement, {
    attributes: true,
    attributeFilter: ['theme-mode'],
  });
});

onBeforeUnmount(() => {
  window.removeEventListener('mousemove', onDrag);
  window.removeEventListener('mouseup', stopDrag);
  if (themeObserver) {
    themeObserver.disconnect();
    themeObserver = null;
  }
});
</script>

<template>
  <div>
    <!-- 模式 1：右侧抽屉模式 -->
    <t-drawer
      v-if="displayMode === 'drawer'"
      attach="body"
      :visible="visible"
      size="560px"
      :footer="false"
      :body-style="{ background: 'var(--td-bg-color-container)', height: 'calc(100% - 48px)', padding: selectedMode === 'apikey' ? '16px' : '0px' }"
      @close="handleClose"
    >
      <template #header>
        <div class="drawer-header-row">
          <div class="drawer-title">
            <template v-if="!selectedMode">
              <span>MSLX AI 助手</span>
            </template>
            <template v-else-if="selectedMode === 'mcp'">
              <span>MSLX AI 助手 (MCP 本地模式)</span>
            </template>
            <template v-else>
              <span>MSLX AI 智能运维助手</span>
              <t-tag v-if="getCurrentServerId()" theme="primary" variant="outline" size="small" style="margin-left: 8px">
                当前选中服: #{{ getCurrentServerId() }}
              </t-tag>
            </template>
          </div>
          <div class="header-actions">
            <t-tooltip v-if="selectedMode" content="切换工作模式">
              <t-button variant="text" size="small" class="mode-switch-btn" @click="handleSwitchToModeSelect">
                <template #icon><swap-icon /></template>
                切换模式
              </t-button>
            </t-tooltip>
            <t-tooltip v-if="selectedMode === 'apikey'" content="模型配置">
              <t-button variant="text" shape="square" size="small" class="settings-btn" @click="openSettingsModal">
                <template #icon><setting-icon /></template>
              </t-button>
            </t-tooltip>
            <t-button v-if="selectedMode === 'apikey'" variant="text" theme="danger" size="small" class="clear-btn" @click="handleClearHistory">
              <template #icon><delete-icon /></template>
              清空历史
            </t-button>
            <t-tooltip content="切换至自由悬浮小窗">
              <t-button variant="text" shape="square" size="small" class="mode-btn" @click="toggleDisplayMode">
                <template #icon><desktop-icon class="mode-icon" /></template>
              </t-button>
            </t-tooltip>
          </div>
        </div>
      </template>

      <!-- 模式选择引导视图 -->
      <div v-if="!selectedMode" class="h-full overflow-y-auto p-4">
        <ModeSelectView @select-mode="handleSelectMode" />
      </div>

      <!-- MCP 管理视图 -->
      <div v-else-if="selectedMode === 'mcp'" class="h-full overflow-y-auto p-4">
        <McpManagementView @switch-mode="handleSwitchToModeSelect" />
      </div>

      <!-- 统一的 AI 对话核心界面组件 (API Key 模式) -->
      <div v-else-if="selectedMode === 'apikey'" class="ai-chat-container">
        <div class="chat-list-wrapper">
          <t-chat
            class="mslx-chat-component"
            :auto-scroll="true"
            :show-scroll-button="true"
            :default-scroll-to="'bottom'"
          >
            <t-chat-item
              v-for="msg in chatList"
              :key="msg.id"
              :role="msg.role"
              :name="msg.role === 'assistant' ? 'MSLX AI' : getUserInfo().name"
              :avatar="msg.role === 'assistant' ? 'https://www.mslmc.cn/logo.png' : getUserInfo().avatar"
              :datetime="msg.time"
              :animation="'gradient'"
            >
              <template #content>
                <!-- 用户消息文本 -->
                <div v-if="msg.role === 'user'" class="chat-bubble-text">
                  {{ msg.content }}
                </div>

                <!-- AI 回复 Markdown -->
                <template v-else>
                  <div v-if="msg.content && msg.content !== '正在思考与处理...'" class="md-preview-wrapper">
                    <md-preview
                      :editor-id="`ai-msg-${msg.id}`"
                      :model-value="formatMarkdown(msg.content)"
                      :theme="mdTheme"
                      :no-katex="true"
                      :no-mermaid="true"
                      class="custom-md-preview bg-transparent text-left !p-0"
                      style="background: transparent; padding: 0"
                    />
                  </div>

                  <!-- 只要请求未结束(loading 为 true)，且正在对当前消息流式响应或后台调取工具，常驻展示动态思考/执行动效胶囊 -->
                  <div v-if="loading && msg.id === activeStreamingId" class="thinking-state active-thinking">
                    <t-loading
                      size="small"
                      :text="msg.toolData?.tool ? `正在后台【${getToolFriendlyName(msg.toolData.tool)}】...` : 'AI 正在思考并处理中...'"
                    />
                  </div>
                </template>

                <div
                  v-if="msg.toolData?.data?.requiresConfirmation && !msg.toolData?.data?.confirmed && !msg.toolData?.data?.rejected"
                  class="action-confirm-card danger-theme"
                >
                  <div class="card-header">
                    <div class="header-left">
                      <div class="pulse-icon danger">
                        <error-circle-filled-icon />
                      </div>
                      <span class="card-title">需要您的授权确认</span>
                    </div>
                    <t-tag size="small" theme="danger" variant="light-outline">高风险操作</t-tag>
                  </div>
                  <div class="card-body">
                    AI 申请对文件/目录 <code class="file-code">{{ msg.toolData.data.filePath }}</code> 执行
                    <strong class="action-highlight red">
                      {{ msg.toolData.data.action === 'delete' ? '【彻底删除】' : '【覆盖修改】' }}
                    </strong> 操作。此操作不可逆！
                  </div>
                  <div class="card-footer">
                    <t-button
                      theme="danger"
                      size="small"
                      class="action-btn"
                      :loading="confirmingToolId === msg.id"
                      :disabled="!!confirmingToolId"
                      @click="handleConfirmToolAction(msg)"
                    >
                      <template #icon><check-circle-icon /></template>
                      确认授权{{ msg.toolData.data.action === 'delete' ? '删除' : '写入' }}
                    </t-button>
                    <t-button
                      theme="default"
                      variant="outline"
                      size="small"
                      class="action-btn"
                      :disabled="!!confirmingToolId"
                      @click="handleRejectToolAction(msg)"
                    >
                      <template #icon><close-circle-icon /></template>
                      拒绝操作
                    </t-button>
                  </div>
                </div>

                <div v-else-if="msg.toolData?.data?.confirmed || msg.toolData?.data?.rejected" class="action-status-bar">
                  <t-tag v-if="msg.toolData?.data?.confirmed" theme="success" variant="light" size="small">
                    <template #icon><check-circle-filled-icon /></template>
                    已授权{{ msg.toolData.data.action === 'delete' ? '删除' : '写入' }}: {{ msg.toolData.data.filePath }}
                  </t-tag>
                  <t-tag v-else theme="danger" variant="light" size="small">
                    <template #icon><close-circle-filled-icon /></template>
                    已拒绝{{ msg.toolData.data.action === 'delete' ? '删除' : '写入' }}操作
                  </t-tag>
                </div>

                <div v-if="msg.toolData" class="tool-debug-collapse">
                  <t-collapse v-model="msg.toolExpanded" borderless size="small">
                    <t-collapse-panel value="tool_detail">
                      <template #header>
                        <div class="tool-debug-title">
                          <code-icon />
                          <span>调取工具: {{ msg.toolData.tool }}</span>
                        </div>
                      </template>
                      <pre class="tool-json-code">{{ JSON.stringify(msg.toolData.data, null, 2) }}</pre>
                    </t-collapse-panel>
                  </t-collapse>
                </div>
              </template>
              <template #actions>
                <div
                  v-if="msg.role === 'assistant' && msg.content && msg.content !== '正在思考与处理...'"
                  class="msg-actions"
                >
                  <t-tooltip content="复制">
                    <t-button
                      variant="text"
                      shape="square"
                      size="small"
                      class="msg-action-btn"
                      @click="handleCopyMessage(msg)"
                    >
                      <template #icon><copy-icon /></template>
                    </t-button>
                  </t-tooltip>
                  <t-tooltip content="重新生成">
                    <t-button
                      variant="text"
                      shape="square"
                      size="small"
                      class="msg-action-btn"
                      @click="handleReplayMessage(msg)"
                    >
                      <template #icon><refresh-icon /></template>
                    </t-button>
                  </t-tooltip>
                </div>
              </template>
            </t-chat-item>
          </t-chat>
        </div>

        <div v-if="suggestedReplies.length > 0 && !loading" class="suggested-replies-bar">
          <span class="sug-hint"><lightbulb-icon /> 快捷回复：</span>
          <t-tag
            v-for="(sug, index) in suggestedReplies"
            :key="index"
            theme="primary"
            variant="light"
            class="sug-chip"
            @click="handleQuickSend(sug)"
          >
            {{ sug }}
          </t-tag>
        </div>

        <div class="chat-input-area">
          <t-chat-sender
            v-model="inputText"
            :loading="loading"
            placeholder="请输入指令，如“把 Java 切换成 25”、“重启服务器”..."
            @send="handleSend"
            @stop="handleAbort"
          />
        </div>
      </div>
    </t-drawer>

    <!-- 模式 2：自由悬浮拖拽小窗模式 (使用 Teleport 直接挂载至 Document Body) -->
    <teleport to="body">
      <div
        v-if="visible && displayMode === 'floating'"
        class="floating-ai-card"
        :style="{ left: `${floatPos.x}px`, top: `${floatPos.y}px` }"
      >
        <div class="floating-header" @mousedown="startDrag">
          <div class="drawer-title draggable-title">
            <move-icon class="drag-icon" />
            <template v-if="!selectedMode">
              <span>MSLX AI 助手</span>
            </template>
            <template v-else-if="selectedMode === 'mcp'">
              <span>MSLX AI 助手 (MCP 本地模式)</span>
            </template>
            <template v-else>
              <span>MSLX AI 智能运维助手</span>
              <t-tag v-if="getCurrentServerId()" theme="primary" variant="outline" size="small" style="margin-left: 8px">
                当前选中服: #{{ getCurrentServerId() }}
              </t-tag>
            </template>
          </div>
          <div class="header-actions">
            <t-tooltip v-if="selectedMode" content="切换工作模式">
              <t-button variant="text" size="small" class="mode-switch-btn" @click="handleSwitchToModeSelect">
                <template #icon><swap-icon /></template>
                切换模式
              </t-button>
            </t-tooltip>
            <t-tooltip v-if="selectedMode === 'apikey'" content="模型配置">
              <t-button variant="text" shape="square" size="small" class="settings-btn" @click="openSettingsModal">
                <template #icon><setting-icon /></template>
              </t-button>
            </t-tooltip>
            <t-button v-if="selectedMode === 'apikey'" variant="text" theme="danger" size="small" class="clear-btn" @click="handleClearHistory">
              <template #icon><delete-icon /></template>
              清空历史
            </t-button>
            <t-tooltip content="切换至侧边抽屉">
              <t-button variant="text" shape="square" size="small" class="mode-btn" @click="toggleDisplayMode">
                <template #icon><view-list-icon class="mode-icon" /></template>
              </t-button>
            </t-tooltip>
            <t-button variant="text" shape="square" size="small" class="close-btn" @click="handleClose">
              <template #icon><close-icon /></template>
            </t-button>
          </div>
        </div>

        <div class="floating-body">
          <!-- 模式选择引导视图 -->
          <div v-if="!selectedMode" class="h-full overflow-y-auto p-4">
            <ModeSelectView @select-mode="handleSelectMode" />
          </div>

          <!-- MCP 管理视图 -->
          <div v-else-if="selectedMode === 'mcp'" class="h-full overflow-y-auto p-4">
            <McpManagementView @switch-mode="handleSwitchToModeSelect" />
          </div>

          <!-- 统一的 AI 对话核心界面组件 (API Key 模式) -->
          <div v-else-if="selectedMode === 'apikey'" class="ai-chat-container">
            <div class="chat-list-wrapper">
              <t-chat
                class="mslx-chat-component"
                :auto-scroll="true"
                :show-scroll-button="true"
                :default-scroll-to="'bottom'"
              >
                <t-chat-item
                  v-for="msg in chatList"
                  :key="msg.id"
                  :role="msg.role"
                  :name="msg.role === 'assistant' ? 'MSLX AI' : getUserInfo().name"
                  :avatar="msg.role === 'assistant' ? 'https://www.mslmc.cn/logo.png' : getUserInfo().avatar"
                  :datetime="msg.time"
                  :animation="'gradient'"
                >
                  <template #content>
                    <!-- 用户消息文本 -->
                    <div v-if="msg.role === 'user'" class="chat-bubble-text">
                      {{ msg.content }}
                    </div>

                    <!-- AI 回复 Markdown -->
                    <template v-else>
                      <div v-if="msg.content && msg.content !== '正在思考与处理...'" class="md-preview-wrapper">
                        <md-preview
                          :editor-id="`ai-msg-float-${msg.id}`"
                          :model-value="formatMarkdown(msg.content)"
                          :theme="mdTheme"
                          :no-katex="true"
                          :no-mermaid="true"
                          class="custom-md-preview bg-transparent text-left !p-0"
                          style="background: transparent; padding: 0"
                        />
                      </div>

                      <!-- 只要请求未结束(loading 为 true)，且正在对当前消息流式响应或后台调取工具，常驻展示动态思考/执行动效胶囊 -->
                      <div v-if="loading && msg.id === activeStreamingId" class="thinking-state active-thinking">
                        <t-loading
                          size="small"
                          :text="msg.toolData?.tool ? `正在后台【${getToolFriendlyName(msg.toolData.tool)}】...` : 'AI 正在思考并处理中...'"
                        />
                      </div>
                    </template>

                    <div
                      v-if="msg.toolData?.data?.requiresConfirmation && !msg.toolData?.data?.confirmed && !msg.toolData?.data?.rejected"
                      class="action-confirm-card danger-theme"
                    >
                      <div class="card-header">
                        <div class="header-left">
                          <div class="pulse-icon danger">
                            <error-circle-filled-icon />
                          </div>
                          <span class="card-title">需要您的授权确认</span>
                        </div>
                        <t-tag size="small" theme="danger" variant="light-outline">高风险操作</t-tag>
                      </div>
                      <div class="card-body">
                        AI 申请对文件/目录 <code class="file-code">{{ msg.toolData.data.filePath }}</code> 执行
                        <strong class="action-highlight red">
                          {{ msg.toolData.data.action === 'delete' ? '【彻底删除】' : '【覆盖修改】' }}
                        </strong> 操作。此操作不可逆！
                      </div>
                      <div class="card-footer">
                        <t-button
                          theme="danger"
                          size="small"
                          class="action-btn"
                          :loading="confirmingToolId === msg.id"
                          :disabled="!!confirmingToolId"
                          @click="handleConfirmToolAction(msg)"
                        >
                          <template #icon><check-circle-icon /></template>
                          确认授权{{ msg.toolData.data.action === 'delete' ? '删除' : '写入' }}
                        </t-button>
                        <t-button
                          theme="default"
                          variant="outline"
                          size="small"
                          class="action-btn"
                          :disabled="!!confirmingToolId"
                          @click="handleRejectToolAction(msg)"
                        >
                          <template #icon><close-circle-icon /></template>
                          拒绝操作
                        </t-button>
                      </div>
                    </div>

                    <div v-else-if="msg.toolData?.data?.confirmed || msg.toolData?.data?.rejected" class="action-status-bar">
                      <t-tag v-if="msg.toolData?.data?.confirmed" theme="success" variant="light" size="small">
                        <template #icon><check-circle-filled-icon /></template>
                        已授权{{ msg.toolData.data.action === 'delete' ? '删除' : '写入' }}: {{ msg.toolData.data.filePath }}
                      </t-tag>
                      <t-tag v-else theme="danger" variant="light" size="small">
                        <template #icon><close-circle-filled-icon /></template>
                        已拒绝{{ msg.toolData.data.action === 'delete' ? '删除' : '写入' }}操作
                      </t-tag>
                    </div>

                    <div v-if="msg.toolData" class="tool-debug-collapse">
                      <t-collapse v-model="msg.toolExpanded" borderless size="small">
                        <t-collapse-panel value="tool_detail">
                          <template #header>
                            <div class="tool-debug-title">
                              <code-icon />
                              <span>调取工具: {{ msg.toolData.tool }}</span>
                            </div>
                          </template>
                          <pre class="tool-json-code">{{ JSON.stringify(msg.toolData.data, null, 2) }}</pre>
                        </t-collapse-panel>
                      </t-collapse>
                    </div>
                  </template>
                  <template #actions>
                    <div
                      v-if="msg.role === 'assistant' && msg.content && msg.content !== '正在思考与处理...'"
                      class="msg-actions"
                    >
                      <t-tooltip content="复制">
                        <t-button
                          variant="text"
                          shape="square"
                          size="small"
                          class="msg-action-btn"
                          @click="handleCopyMessage(msg)"
                        >
                          <template #icon><copy-icon /></template>
                        </t-button>
                      </t-tooltip>
                      <t-tooltip content="重新生成">
                        <t-button
                          variant="text"
                          shape="square"
                          size="small"
                          class="msg-action-btn"
                          @click="handleReplayMessage(msg)"
                        >
                          <template #icon><refresh-icon /></template>
                        </t-button>
                      </t-tooltip>
                    </div>
                  </template>
                </t-chat-item>
              </t-chat>
            </div>

            <div v-if="suggestedReplies.length > 0 && !loading" class="suggested-replies-bar">
              <span class="sug-hint"><lightbulb-icon /> 快捷回复：</span>
              <t-tag
                v-for="(sug, index) in suggestedReplies"
                :key="index"
                theme="primary"
                variant="light"
                class="sug-chip"
                @click="handleQuickSend(sug)"
              >
                {{ sug }}
              </t-tag>
            </div>

            <div class="chat-input-area">
              <t-chat-sender
                v-model="inputText"
                :loading="loading"
                placeholder="请输入指令，如“把 Java 切换成 25”、“重启服务器”..."
                @send="handleSend"
                @stop="handleAbort"
              />
            </div>
          </div>
        </div>
      </div>
    </teleport>

    <!-- API Key 模型配置弹窗 -->
    <t-dialog
      v-model:visible="settingsModalVisible"
      header="AI 助手模型配置"
      width="480px"
      attach="body"
      :confirm-btn="{ content: '保存配置', loading: settingsSubmitting }"
      @confirm="handleSaveAiSettings"
    >
      <div class="space-y-4 py-2">
        <t-alert theme="info" class="text-xs mb-3">
          配置大模型 API Key 后，即可在此窗口内使用 AI 智能运维助手。支持 DeepSeek、OpenAI、OneAPI 等兼容标准接口。
        </t-alert>

        <t-form-item label="API Key" required-mark>
          <t-input
            v-model="aiSettingsForm.aiApiKey"
            type="password"
            placeholder="请输入 sk- 开头的大模型 API Key"
          />
        </t-form-item>

        <t-form-item label="Base URL">
          <t-input
            v-model="aiSettingsForm.aiBaseUrl"
            placeholder="默认 https://api.deepseek.com/v1"
          />
        </t-form-item>

        <t-form-item label="模型名称">
          <t-input
            v-model="aiSettingsForm.aiModelName"
            placeholder="默认 deepseek-chat"
          />
        </t-form-item>
      </div>
    </t-dialog>
  </div>
</template>

<style scoped>
.mode-switch-btn {
  color: var(--td-text-color-primary) !important;
  font-size: 12px !important;
}

.settings-btn {
  color: var(--td-text-color-primary) !important;
}

.mode-switch-btn:hover,
.settings-btn:hover {
  background-color: var(--td-bg-color-container-hover) !important;
}
.drawer-header-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  padding-right: 12px;
}

.drawer-title {
  display: flex;
  align-items: center;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

.mode-btn {
  color: var(--td-text-color-primary) !important;
}

.mode-icon {
  font-size: 16px !important;
  color: var(--td-text-color-primary) !important;
}

/* 自由悬浮小窗样式：纯实心高不透明背景，消除背景内容透穿与字迹模糊 */
.floating-ai-card {
  position: fixed;
  z-index: 2000;
  width: 540px;
  height: 660px;
  background-color: var(--td-bg-color-container) !important;
  border: 1px solid var(--td-component-border);
  border-radius: 16px;
  box-shadow: 0 16px 48px rgba(0, 0, 0, 0.28);
  display: flex;
  flex-direction: column;
  overflow: hidden;
  opacity: 1 !important;
}

.floating-header {
  padding: 12px 16px;
  background-color: var(--td-bg-color-secondarycontainer) !important;
  border-bottom: 1px solid var(--td-component-border);
  cursor: move;
  user-select: none;
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.floating-body {
  flex: 1;
  min-height: 0;
  padding: 12px 16px;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background-color: var(--td-bg-color-container) !important;
}

.draggable-title {
  display: flex;
  align-items: center;
  cursor: move;
}

.drag-icon {
  margin-right: 6px;
  color: var(--td-brand-color);
}

.ai-chat-container {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.chat-list-wrapper {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding: 12px 0;
  position: relative;
}

.mslx-chat-component {
  width: 100%;
  height: 100%;
}

/* 优雅重置与定位【划到最下面】(t-chat__to-bottom) 悬浮按钮，消除 left:50% 死板居中与双重圆角嵌套 */
.mslx-chat-component :deep(.t-chat__to-bottom) {
  position: absolute !important;
  left: auto !important;
  right: 24px !important;
  margin-left: 0 !important;
  bottom: 16px !important;
  z-index: 50 !important;
  width: 40px !important;
  height: 40px !important;
  border-radius: 50% !important;
  padding: 0 !important;
  border: none !important;
  background: transparent !important;
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.15) !important;
  overflow: hidden !important;
  transition: all 0.2s cubic-bezier(0.38, 0, 0.24, 1) !important;
}

.mslx-chat-component :deep(.t-chat__to-bottom-inner) {
  width: 40px !important;
  height: 40px !important;
  border-radius: 50% !important;
  padding: 0 !important;
  margin: 0 !important;
  display: flex !important;
  align-items: center !important;
  justify-content: center !important;
  background-color: var(--td-bg-color-container) !important;
  border: 1px solid var(--td-component-border) !important;
  box-shadow: none !important;
  backdrop-filter: blur(8px) !important;
  -webkit-backdrop-filter: blur(8px) !important;
}

.mslx-chat-component :deep(.t-chat__to-bottom-inner .t-icon) {
  font-size: 18px !important;
  color: var(--td-text-color-primary) !important;
}

.mslx-chat-component :deep(.t-chat__to-bottom:hover .t-chat__to-bottom-inner) {
  background-color: var(--td-bg-color-container-hover) !important;
  border-color: var(--td-brand-color) !important;
}

.mslx-chat-component :deep(.t-chat__to-bottom:hover .t-icon) {
  color: var(--td-brand-color) !important;
}

.chat-bubble-text {
  font-size: 14px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
}

/* === Markdown 组件样式：1:1 复刻 MSLX 宿主样式 === */
.md-preview-wrapper {
  font-size: 14px;
  line-height: 1.6;
  word-break: break-word;
}

:deep(.custom-md-preview) {
  --md-bk-color: transparent !important;
  --md-color: var(--td-text-color-primary) !important;
  text-align: left !important;
}

:deep(.md-editor-preview) {
  font-size: 14px !important;
  line-height: 1.6 !important;
  color: var(--td-text-color-primary) !important;
  --md-bk-color: transparent !important;
}

/* 恢复 markdown 编辑器/预览器中的列表样式 (复刻 MSLX reset.less) */
:deep(.md-editor-preview ul) {
  list-style-type: disc !important;
  padding-left: 1.5rem !important;
  margin-top: 0.5rem !important;
  margin-bottom: 0.5rem !important;
}

:deep(.md-editor-preview ol) {
  list-style-type: decimal !important;
  padding-left: 1.5rem !important;
  margin-top: 0.5rem !important;
  margin-bottom: 0.5rem !important;
}

:deep(.md-editor-preview li) {
  list-style: inherit !important;
  margin-bottom: 4px !important;
}

:deep(.md-editor-preview p) {
  margin-top: 0.5rem !important;
  margin-bottom: 0.5rem !important;
  line-height: 1.6 !important;
}

/* 覆盖MD编辑器链接颜色，使用主题色 (复刻 MSLX Announcement.vue / LogAnalysis.vue) */
:deep(.md-editor-preview a) {
  color: var(--td-brand-color, var(--color-primary)) !important;
  text-decoration: none !important;
  font-weight: 500 !important;
  &:hover {
    text-decoration: underline !important;
  }
}

/* 覆盖行内代码块颜色，采用微透明质感 */
:deep(.md-editor-preview code:not([class*="language-"])) {
  color: var(--td-brand-color, var(--color-primary)) !important;
  background-color: color-mix(in srgb, var(--td-brand-color, var(--color-primary)), transparent 90%) !important;
  border-radius: 4px !important;
  padding: 2px 6px !important;
  font-family: monospace !important;
  font-size: 13px !important;
}

/* 代码块 pre */
:deep(.md-editor-preview pre) {
  border-radius: 8px !important;
  background-color: var(--td-bg-color-secondarycontainer) !important;
  border: 1px solid var(--td-component-border) !important;
  padding: 12px !important;
  margin: 10px 0 !important;
}

/* 引用块样式 */
:deep(.md-editor div.default-theme) {
  --md-theme-quote-border: 4px solid var(--td-brand-color, var(--color-primary));
}

:deep(.md-editor-preview blockquote) {
  background-color: color-mix(in srgb, var(--td-brand-color, var(--color-primary)), transparent 95%) !important;
  border-radius: 0 6px 6px 0 !important;
  margin: 8px 0 !important;
  padding: 8px 14px !important;
  border-left: 4px solid var(--td-brand-color, var(--color-primary)) !important;
}

/* 表格样式 (复刻 MSLX Announcement.vue / resourceCenter) */
:deep(.md-editor-preview table) {
  width: 100% !important;
  border-collapse: collapse !important;
  margin: 12px 0 !important;
  font-size: 13px !important;
}

:deep(.md-editor-preview table th) {
  background-color: var(--td-bg-color-secondarycontainer) !important;
  color: var(--td-text-color-primary) !important;
  border: 1px solid var(--td-component-border) !important;
  padding: 8px 12px !important;
  font-weight: 600 !important;
}

:deep(.md-editor-preview table td) {
  border: 1px solid var(--td-component-border) !important;
  padding: 8px 12px !important;
}

:deep(.md-editor-preview table tr:nth-child(2n)) {
  background-color: transparent !important;
}

:deep(.md-editor-preview table tr:nth-child(n)) {
  background-color: transparent !important;
}

/* 标题样式 (完整还原各级标题尺寸与边距) */
:deep(.md-editor-preview h1),
:deep(.md-editor-preview h2),
:deep(.md-editor-preview h3),
:deep(.md-editor-preview h4),
:deep(.md-editor-preview h5),
:deep(.md-editor-preview h6) {
  color: var(--td-text-color-primary) !important;
  font-weight: 600 !important;
  margin-top: 14px !important;
  margin-bottom: 8px !important;
  line-height: 1.4 !important;
}

:deep(.md-editor-preview h1) {
  font-size: 19px !important;
  border-bottom: 1px solid var(--td-component-border) !important;
  padding-bottom: 6px !important;
}

:deep(.md-editor-preview h2) {
  font-size: 16px !important;
  border-bottom: 1px solid var(--td-component-border) !important;
  padding-bottom: 4px !important;
}

:deep(.md-editor-preview h3) {
  font-size: 15px !important;
}

:deep(.md-editor-preview h4) {
  font-size: 14px !important;
}

/* 图片 */
:deep(.md-editor-preview img) {
  max-width: 100% !important;
  border-radius: 8px !important;
  margin: 12px 0 !important;
}

/* 分割线 */
:deep(.md-editor-preview hr) {
  margin: 16px 0 !important;
  border: none !important;
  border-top: 1px solid var(--td-component-border) !important;
}

/* 代码语法高亮：亮色/暗色适配 (复刻 MSLX reset.less) */
:deep(.md-editor-preview) {
  .hljs-keyword, .hljs-selector-tag, .hljs-subst { color: #a626a4; }
  .hljs-string, .hljs-title, .hljs-section, .hljs-attribute, .hljs-literal, .hljs-template-tag, .hljs-template-variable, .hljs-type, .hljs-addition { color: #50a14f; }
  .hljs-comment, .hljs-quote { color: #a0a1a7; font-style: italic; }
  .hljs-number, .hljs-regexp, .hljs-link { color: #986801; }
  .hljs-variable, .hljs-template-variable { color: #e45649; }
  .hljs-symbol, .hljs-bullet { color: #4078f2; }
  .hljs-built_in, .hljs-class .hljs-title { color: #c18401; }
  .hljs-meta { color: #4078f2; }
  .hljs-deletion { color: #e45649; }
}

:global(html[theme-mode='dark']) :deep(.md-editor-preview),
:global(html.dark) :deep(.md-editor-preview) {
  .hljs-keyword, .hljs-selector-tag, .hljs-subst { color: #c678dd; }
  .hljs-string, .hljs-title, .hljs-section, .hljs-attribute, .hljs-literal, .hljs-template-tag, .hljs-template-variable, .hljs-type, .hljs-addition { color: #98c379; }
  .hljs-comment, .hljs-quote { color: #5c6370; font-style: italic; }
  .hljs-number, .hljs-regexp, .hljs-link { color: #d19a66; }
  .hljs-variable, .hljs-template-variable { color: #e06c75; }
  .hljs-symbol, .hljs-bullet { color: #61aeee; }
  .hljs-built_in, .hljs-class .hljs-title { color: #e6c07b; }
  .hljs-meta { color: #61aeee; }
  .hljs-deletion { color: #e06c75; }
}

.thinking-state {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  color: var(--td-text-color-secondary);
}

.active-thinking {
  margin-top: 8px;
  padding: 6px 12px;
  background-color: var(--td-bg-color-secondarycontainer);
  border-radius: 8px;
  display: inline-flex;
  border: 1px solid var(--td-component-border);
}

.msg-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-top: 4px;
}

.msg-action-btn {
  color: var(--td-text-color-secondary) !important;
}

.msg-action-btn:hover {
  color: var(--td-brand-color) !important;
}

/* 敏感授权操作卡片 */
.action-confirm-card {
  margin-top: 12px;
  border-radius: 12px;
  padding: 12px 14px;
  background: var(--td-bg-color-secondarycontainer);
  border: 1px solid var(--td-component-border);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.05);
}

.action-confirm-card.danger-theme {
  border-left: 4px solid var(--td-error-color);
}

.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 8px;
}

.pulse-icon.danger {
  font-size: 18px;
  color: var(--td-error-color);
  display: flex;
  align-items: center;
}

.card-title {
  font-weight: 600;
  font-size: 13px;
  color: var(--td-text-color-primary);
}

.card-body {
  font-size: 13px;
  line-height: 1.6;
  color: var(--td-text-color-secondary);
  margin-bottom: 12px;
}

.file-code {
  background: var(--td-bg-color-component);
  color: var(--td-text-color-primary);
  padding: 2px 6px;
  border-radius: 6px;
  font-family: monospace;
  font-weight: 600;
  border: 1px solid var(--td-component-border);
}

.action-highlight.red {
  color: var(--td-error-color);
}

.card-footer {
  display: flex;
  gap: 10px;
}

.action-btn {
  border-radius: 8px;
}

.action-status-bar {
  margin-top: 8px;
}

/* 工具调试日志（极简且默认收起） */
.tool-debug-collapse {
  margin-top: 8px;
  opacity: 0.7;
}

.tool-debug-title {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--td-text-color-placeholder);
}

.tool-json-code {
  font-family: monospace;
  font-size: 11px;
  margin: 0;
  padding: 8px;
  background-color: var(--td-bg-color-page);
  border-radius: 6px;
  color: var(--td-text-color-secondary);
  white-space: pre-wrap;
  word-break: break-all;
}

.suggested-replies-bar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  padding: 8px 0;
  margin-top: 4px;
  margin-bottom: 4px;
}

.sug-hint {
  font-size: 12px;
  color: var(--td-brand-color);
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 4px;
}

.sug-chip {
  cursor: pointer;
  user-select: none;
  transition: all 0.2s ease;
}

.sug-chip:hover {
  transform: translateY(-1px);
  opacity: 0.9;
}

/* 优化输入框外观：显著边框、底色微弱对比、平滑阴影、悬停及聚焦高亮动效 */
.chat-input-area {
  padding-top: 12px;
  border-top: 1px solid var(--td-component-border);
}

.chat-input-area :deep(.t-chat-sender) {
  position: relative !important;
  border: 1.5px solid var(--td-border-level-2-color, var(--td-component-border)) !important;
  border-radius: 14px !important;
  background-color: var(--td-bg-color-secondarycontainer, #f6f8fa) !important;
  padding: 12px 14px 8px 14px !important;
  box-sizing: border-box !important;
  box-shadow: 0 2px 6px rgba(0, 0, 0, 0.04) !important;
  transition: all 0.2s cubic-bezier(0.38, 0, 0.24, 1) !important;
}

.chat-input-area :deep(.t-chat-sender:hover) {
  border-color: var(--td-brand-color-hover, var(--td-brand-color)) !important;
  background-color: var(--td-bg-color-container) !important;
  box-shadow: 0 3px 10px rgba(0, 0, 0, 0.07) !important;
}

.chat-input-area :deep(.t-chat-sender:focus-within) {
  border-color: var(--td-brand-color) !important;
  background-color: var(--td-bg-color-container) !important;
  box-shadow: 0 0 0 2px var(--td-brand-color-focus, rgba(0, 82, 217, 0.18)), 0 4px 14px rgba(0, 0, 0, 0.08) !important;
}

/* 专门针对可移动悬浮窗增加输入框清晰度强化 */
.floating-ai-card .chat-input-area {
  padding-top: 10px;
}

.floating-ai-card .chat-input-area :deep(.t-chat-sender) {
  border: 1.5px solid var(--td-component-stroke, var(--td-border-level-2-color, #c8cdd4)) !important;
  background-color: var(--td-bg-color-secondarycontainer, #f4f6f8) !important;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.06) !important;
}

.floating-ai-card .chat-input-area :deep(.t-chat-sender:hover) {
  border-color: var(--td-brand-color) !important;
  background-color: var(--td-bg-color-container) !important;
  box-shadow: 0 3px 12px rgba(0, 0, 0, 0.08) !important;
}

.floating-ai-card .chat-input-area :deep(.t-chat-sender:focus-within) {
  border-color: var(--td-brand-color) !important;
  background-color: var(--td-bg-color-container) !important;
  box-shadow: 0 0 0 2px var(--td-brand-color-focus, rgba(0, 82, 217, 0.2)), 0 4px 16px rgba(0, 0, 0, 0.1) !important;
}

/* 彻底剥离所有内部元素的边框、阴影、背景与聚焦边框 */
.chat-input-area :deep(.t-chat-sender__textarea),
.chat-input-area :deep(.t-textarea),
.chat-input-area :deep(.t-textarea__inner),
.chat-input-area :deep(textarea) {
  border: none !important;
  outline: none !important;
  box-shadow: none !important;
  background: transparent !important;
  border-radius: 0 !important;
  padding: 0 !important;
  margin: 0 !important;
}

.chat-input-area :deep(.t-chat-sender__textarea--focus),
.chat-input-area :deep(.t-textarea__inner.t-is-focused) {
  border: none !important;
  box-shadow: none !important;
}

.chat-input-area :deep(textarea) {
  font-size: 14px !important;
  line-height: 1.6 !important;
  color: var(--td-text-color-primary) !important;
  min-height: 48px !important;
}

.chat-input-area :deep(.t-chat-sender__footer) {
  display: flex !important;
  align-items: center !important;
  justify-content: flex-end !important;
  margin-top: 4px !important;
  padding-top: 0 !important;
  border: none !important;
  background: transparent !important;
}

.chat-input-area :deep(.t-chat-sender__upload) {
  display: none !important;
}

/* 解决发送按钮内部图标被组件库原生 display:none 隐藏的问题 */
.chat-input-area :deep(.t-chat-sender__button__default > div) {
  display: flex !important;
  align-items: center !important;
  justify-content: center !important;
  width: 100% !important;
  height: 100% !important;
}

.chat-input-area :deep(.t-chat-sender__button__default) {
  background-color: var(--td-brand-color) !important;
  color: var(--td-text-color-anti) !important;
}

.chat-input-area :deep(.t-chat-sender__button__default .t-icon),
.chat-input-area :deep(.t-chat-sender__button__default svg) {
  color: var(--td-text-color-anti) !important;
  fill: currentColor !important;
  display: inline-block !important;
  opacity: 1 !important;
}

/* 彻底隐藏 TChat 组件内置在对话列表底部自带的“清空历史”分割线与按钮 */
.mslx-chat-component :deep(.t-chat__list .clear-btn),
.mslx-chat-component :deep(.clear-btn-text),
.mslx-chat-component :deep(.t-chat__list .t-divider) {
  display: none !important;
}
</style>
