import AIChatDrawer from './views/AIChatDrawer.vue';
import './style.css';

export const pluginConfig = {
  name: 'MSLX.Plugin.AiAgents',
  version: '1.0.0',

  routes: [],

  extensions: [
    {
      slot: 'header-action',
      type: 'drawer',
      id: 'ai-assistant',
      label: 'AI 助手',
      icon: 'chat-bubble-help',
      component: AIChatDrawer,
      roleCode: ['admin'],
    },
  ],
};
