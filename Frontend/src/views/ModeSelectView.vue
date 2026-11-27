<script setup lang="ts">
import { DesktopIcon, ChatIcon, ChevronRightIcon } from 'tdesign-icons-vue-next';

const emit = defineEmits<{
  (e: 'selectMode', mode: 'mcp' | 'apikey'): void;
}>();

const handleSelect = (mode: 'mcp' | 'apikey') => {
  localStorage.setItem('mslx_ai_selected_mode', mode);
  emit('selectMode', mode);
};
</script>

<template>
  <div class="mode-select-view flex flex-col h-full justify-between p-2 sm:p-4 select-none">
    <div class="space-y-6">
      <!-- 顶部欢迎语与说明 -->
      <div class="text-center pt-3 pb-1">
        <h2 class="text-base sm:text-lg font-bold text-[var(--td-text-color-primary)] m-0 tracking-tight">
          选择 AI 助手工作模式
        </h2>
        <p class="text-xs text-[var(--td-text-color-secondary)] mt-2 mb-0 leading-relaxed max-w-md mx-auto">
          MSLX 提供了两种不同的 AI 接入与交互方案。请根据您的需求选择，后续可随时在界面中切换。
          <a
            href="https://mslx.mslmc.cn/docs/server/ai/"
            target="_blank"
            rel="noopener noreferrer"
            class="text-[var(--td-brand-color)] hover:underline ml-1"
          >查看使用指南 ↗</a>
        </p>
      </div>

      <!-- 两种模式卡片 -->
      <div class="space-y-4">
        <!-- 模式 1: MCP 本地 Agent 模式 -->
        <div
          class="mode-card design-card group relative rounded-2xl border border-[var(--td-component-border)] bg-[var(--td-bg-color-container)] hover:border-[var(--td-brand-color)] p-5 transition-all duration-300 hover:shadow-lg cursor-pointer"
          @click="handleSelect('mcp')"
        >
          <div class="flex items-start gap-4">
            <div class="w-11 h-11 rounded-xl bg-[var(--td-brand-color-light)] text-[var(--td-brand-color)] flex items-center justify-center shrink-0 transition-transform group-hover:scale-105">
              <desktop-icon size="22px" />
            </div>

            <div class="flex-1 min-w-0">
              <div class="flex items-center justify-between">
                <h3 class="text-sm font-bold text-[var(--td-text-color-primary)] m-0 flex items-center gap-2">
                  MCP (本地 Agent 模式)
                  <span class="text-[10px] px-1.5 py-0.5 rounded bg-[var(--td-brand-color-light)] text-[var(--td-brand-color)] font-medium">
                    推荐
                  </span>
                </h3>
                <chevron-right-icon class="text-[var(--td-text-color-placeholder)] group-hover:text-[var(--td-brand-color)] group-hover:translate-x-0.5 transition-all" size="18px" />
              </div>

              <p class="text-xs text-[var(--td-text-color-secondary)] mt-2 mb-3 leading-relaxed">
                将 MSLX 作为本地工具服务接入您现有的外部 AI 工具（如 Workbuddy、豆包、Cursor、反重力等）。零消耗面板 API Token，完全使用您外部客户端的大模型驱动运维操作。
              </p>

              <div class="flex flex-wrap gap-2">
                <span class="text-[11px] px-2 py-0.5 rounded-full bg-[var(--td-bg-color-secondarycontainer)] text-[var(--td-text-color-secondary)] border border-[var(--td-component-border)]">
                  零 Token 成本
                </span>
                <span class="text-[11px] px-2 py-0.5 rounded-full bg-[var(--td-bg-color-secondarycontainer)] text-[var(--td-text-color-secondary)] border border-[var(--td-component-border)]">
                  支持 Workbuddy / 豆包
                </span>
                <span class="text-[11px] px-2 py-0.5 rounded-full bg-[var(--td-bg-color-secondarycontainer)] text-[var(--td-text-color-secondary)] border border-[var(--td-component-border)]">
                  一句话快速接入
                </span>
              </div>
            </div>
          </div>
        </div>

        <!-- 模式 2: API Key 在线助手模式 -->
        <div
          class="mode-card design-card group relative rounded-2xl border border-[var(--td-component-border)] bg-[var(--td-bg-color-container)] hover:border-[var(--td-brand-color)] p-5 transition-all duration-300 hover:shadow-lg cursor-pointer"
          @click="handleSelect('apikey')"
        >
          <div class="flex items-start gap-4">
            <div class="w-11 h-11 rounded-xl bg-[var(--td-brand-color-light)] text-[var(--td-brand-color)] flex items-center justify-center shrink-0 transition-transform group-hover:scale-105">
              <chat-icon size="22px" />
            </div>

            <div class="flex-1 min-w-0">
              <div class="flex items-center justify-between">
                <h3 class="text-sm font-bold text-[var(--td-text-color-primary)] m-0">
                  API Key 在线助手模式
                </h3>
                <chevron-right-icon class="text-[var(--td-text-color-placeholder)] group-hover:text-[var(--td-brand-color)] group-hover:translate-x-0.5 transition-all" size="18px" />
              </div>

              <p class="text-xs text-[var(--td-text-color-secondary)] mt-2 mb-3 leading-relaxed">
                配置您的大模型 API Key（支持 DeepSeek、OpenAI 等标准兼容接口），直接在面板抽屉和悬浮小窗内与内置 AI 智能运维助手实时对话，开箱即用。
              </p>

              <div class="flex flex-wrap gap-2">
                <span class="text-[11px] px-2 py-0.5 rounded-full bg-[var(--td-bg-color-secondarycontainer)] text-[var(--td-text-color-secondary)] border border-[var(--td-component-border)]">
                  内置交互界面
                </span>
                <span class="text-[11px] px-2 py-0.5 rounded-full bg-[var(--td-bg-color-secondarycontainer)] text-[var(--td-text-color-secondary)] border border-[var(--td-component-border)]">
                  开箱即用对话
                </span>
                <span class="text-[11px] px-2 py-0.5 rounded-full bg-[var(--td-bg-color-secondarycontainer)] text-[var(--td-text-color-secondary)] border border-[var(--td-component-border)]">
                  全套 40+ 运维工具
                </span>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- 底部说明 -->
    <div class="text-center pt-4 pb-2 border-t border-[var(--td-component-border)]">
      <span class="text-[11px] text-[var(--td-text-color-placeholder)]">
        模式选择后将自动保存在当前浏览器中，后续可在右上角随时切换
      </span>
    </div>
  </div>
</template>

<style scoped>
.mode-card {
  box-shadow: 0 1px 3px 0 rgba(0, 0, 0, 0.04);
}
</style>
