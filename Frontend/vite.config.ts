import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import path from 'path';
import createExternal from 'vite-plugin-external';
import cssInjectedByJsPlugin from 'vite-plugin-css-injected-by-js';
import UnoCSS from 'unocss/vite';

const tdesignVirtualPlugin = () => {
  const virtualId = '\0virtual:tdesign-vue-next';
  return {
    name: 'tdesign-vue-next-esm',
    enforce: 'pre' as const,
    resolveId(id: string) {
      if (id === 'tdesign-vue-next') {
        return virtualId;
      }
    },
    load(id: string) {
      if (id === virtualId) {
        return `
          const T = typeof window !== 'undefined' && window.TDesign ? window.TDesign : {};
          export default T;
          export const {
            Affix, Alert, Anchor, AnchorItem, AnchorTarget, AutoComplete, Avatar, AvatarGroup, BackTop, Badge, Breadcrumb, BreadcrumbItem,
            Button, Calendar, Card, Cascader, CheckTag, Checkbox, CheckboxGroup, Col, Collapse, CollapsePanel, ColorPicker, Comment,
            ConfigProvider, DatePicker, DateRangePicker, Dialog, Divider, Drawer, Dropdown, DropdownItem, DropdownMenu, Form, FormItem,
            Grid, GridItem, Guide, Header, Aside, Content, Footer, Layout, Icon, Image, ImageViewer, Input, InputAdornment, InputGroup,
            InputNumber, Link, List, ListItem, ListItemMeta, Loading, Menu, MenuItem, MenuGroup, Submenu, Message, MessagePlugin,
            Notification, NotificationPlugin, Option, OptionGroup, Pagination, Popconfirm, Popup, Progress, Radio, RadioButton, RadioGroup,
            RangeInput, RangeInputPopup, Rate, Row, Select, SelectInput, Skeleton, Slider, Space, Statistic, Steps, StepItem, StickyItem,
            StickyTool, Swiper, SwiperItem, Switch, Table, BaseTable, PrimaryTable, EnhancedTable, Tabs, TabPanel, Tag, CheckTagGroup,
            TagInput, Textarea, TimePicker, TimeRangePicker, Timeline, TimelineItem, Tooltip, Transfer, Tree, TreeSelect, Upload, Watermark
          } = T;
        `;
      }
    }
  };
};

export default defineConfig({
  define: {
    'process.env.NODE_ENV': JSON.stringify('production'),
    'process.env': '{}',
  },
  plugins: [
    tdesignVirtualPlugin(),
    createExternal({
      externals: {
        vue: 'Vue',
        'vue-router': 'VueRouter',
        pinia: 'Pinia',
        'mslx-request': 'mslxRequest'
      }
    }),

    vue({
      template: { compilerOptions: { hoistStatic: false } }
    }),
    UnoCSS({
      mode: 'vue-scoped'
    }),
    cssInjectedByJsPlugin(),
  ],

  server: {
    port: 5001,
    cors: true,
  },
  preview: {
    port: 5001,
    cors: true,
  },


  build: {
    lib: {
      entry: path.resolve(__dirname, 'src/pluginEntry.ts'),
      name: 'MslxPlugin',
      formats: ['es'],
      fileName: () => 'mslx-plugin-entry.js'
    },
    rollupOptions: {
      external: ['vue', 'vue-router', 'pinia', 'mslx-request'],
      output: {
        banner: 'if (typeof window !== "undefined" && !window.process) { window.process = { env: { NODE_ENV: "production" } }; }',
        assetFileNames: 'assets/[name]-[hash].[ext]'
      }
    }
  }
});