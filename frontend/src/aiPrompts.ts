import type { ApiEndpointDoc, Lang, McpFocusContext, McpToolDef } from './types';

export type AiPromptPresetKey =
  | 'all_in_one'
  | 'cards_lua'
  | 'settings_js'
  | 'content_ypk'
  | 'rest_api'
  | 'focus_script';

export interface AiPromptBuildContext {
  lang: Lang;
  apiBaseUrl: string;
  mcpUrl: string;
  mcpSseUrl?: string;
  workspaceRoot?: string;
  gameVersion?: string | null;
  activeModId?: string | null;
  activeModName?: string | null;
  focus?: McpFocusContext | null;
  tools?: McpToolDef[];
  endpoints?: ApiEndpointDoc[];
  customTask?: string;
}

export interface AiPromptPresetMeta {
  key: AiPromptPresetKey;
  labelZh: string;
  labelEn: string;
  descZh: string;
  descEn: string;
}

export const AI_PROMPT_PRESETS: AiPromptPresetMeta[] = [
  {
    key: 'all_in_one',
    labelZh: '全能总控（MCP + 指南 + API）',
    labelEn: 'All-in-One (MCP + Guides + API)',
    descZh: '包含内置 MCP 连接地址、全部 23 个工具、12 份规范指南、REST API 与自动热加载流程',
    descEn: 'Includes MCP endpoint, all 23 tools, 12 spec guides, REST API & live hot-reload workflow',
  },
  {
    key: 'cards_lua',
    labelZh: '卡牌 & ocgcore Lua 脚本',
    labelEn: 'Cards & ocgcore Lua',
    descZh: '引导 AI 先查阅原版卡脚本与位掩码指南，再精准写卡、写 Lua 并热加载验证',
    descEn: 'Guides AI to inspect vanilla card scripts & bitmasks before writing cards & Lua scripts',
  },
  {
    key: 'settings_js',
    labelZh: '游戏内 F9 设置 & JS/CSS 插件',
    labelEn: 'F9 MOD Settings & JS/CSS',
    descZh: '让 AI 编写难度/倍速/颜色等 F9 游戏内实时设置与自动导入的 JS/CSS 插件',
    descEn: 'Builds in-game F9 settings (difficulty/speed/colors) + auto-imported JS/CSS plugins',
  },
  {
    key: 'content_ypk',
    labelZh: '角色 · 遗物 · 卡包 · 修改器 · YPK',
    labelEn: 'Chars · Relics · Packs · YPK',
    descZh: '引导 AI 通过 MCP 创建角色、遗物、扩展包、预组卡组、F8 修改器或导入 .ypk',
    descEn: 'Creates characters, relics, packs, decks, F8 trainers or imports .ypk via MCP',
  },
  {
    key: 'rest_api',
    labelZh: '本地 HTTP REST API 自动化',
    labelEn: 'Local REST API Automation',
    descZh: '适合无 MCP 的 AI / 脚本，直接通过本地 HTTP + SSE 控制 MOD 管理器与热加载',
    descEn: 'For non-MCP AI tools or scripts using local HTTP REST + SSE endpoints directly',
  },
  {
    key: 'focus_script',
    labelZh: '精修当前创作台选中脚本',
    labelEn: 'Edit Focused Studio Script',
    descZh: '自动锁定当前在创作台打开的 MOD、条目与脚本路径，直接 patch 并触发热加载',
    descEn: 'Targets the exact MOD, entity & script open in Studio for surgical patching & hot-reload',
  },
];

export function buildAiPrompt(preset: AiPromptPresetKey, ctx: AiPromptBuildContext): string {
  const isZh = ctx.lang === 'zh';
  const apiBase = (ctx.apiBaseUrl || 'http://127.0.0.1:17850').replace(/\/+$/, '');
  const mcpUrl = ctx.mcpUrl || `${apiBase}/mcp`;
  const sseUrl = ctx.mcpSseUrl || `${apiBase}/mcp/sse`;
  const modId = ctx.focus?.modId || ctx.activeModId || '<target-mod-id>';
  const modName = ctx.focus?.modName || ctx.activeModName || modId;
  const scriptPath = ctx.focus?.scriptPath || '';
  const itemName = ctx.focus?.itemName || '';
  const toolNames =
    ctx.tools && ctx.tools.length > 0
      ? ctx.tools.map((x) => x.name).join(', ')
      : 'get_studio_context, list_mod_files, get_vanilla_card_script, read_mod_file, write_mod_file, patch_mod_file, delete_mod_file, trigger_hot_reload, get_mod_guide, get_manager_status, search_game_data, create_mod, update_mod_metadata, import_ypk, add_mod_card, add_mod_character, add_mod_relic, add_mod_pack, add_mod_preconstructed_deck, configure_mod_trainer, validate_mod, pack_mod_or_workspace, apply_or_restore_mods';

  const customBlock = ctx.customTask?.trim()
    ? isZh
      ? `\n\n## 我的具体需求\n${ctx.customTask.trim()}`
      : `\n\n## My Specific Task\n${ctx.customTask.trim()}`
    : isZh
      ? `\n\n## 我的具体需求\n（请在这里写下你想要制作或修改的卡牌、效果、难度/倍速设置或功能）`
      : `\n\n## My Specific Task\n(Describe the card, Lua script, F9 setting, relic, character, or feature you want here)`;

  if (preset === 'rest_api') {
    const epList =
      ctx.endpoints && ctx.endpoints.length > 0
        ? ctx.endpoints.map((e) => `- \`${e.method} ${e.path}\` — ${e.description}`).join('\n')
        : [
            '- `GET /api/status` — 获取管理器状态、全部 MOD、分类/封面、自动导入的 JS/CSS/Lua 库与冲突信息',
            '- `GET /api/docs?lang=zh` — 获取完整实时 REST API 文档清单',
            '- `GET /api/events` — SSE 实时日志、进度、热加载与 MCP 同步事件流',
            '- `POST /api/mods/create` / `POST /api/mods/create-example` — 新建空白模板或完整规范示例 MOD',
            '- `POST /api/mods/metadata` — 修改 MOD 分类 (`category`)、封面、标签、版本号与 `autoDiscoverEntries`',
            '- `POST /api/mods/import` / `POST /api/mods/import-upload?fileName=...` — 导入 `.zip` / `.ypk` / 工作区整合包',
            '- `POST /api/mods/reorder` / `POST /api/mods/toggle` — 调整加载顺序与启用开关',
            '- `GET /api/studio/catalog?modId=...&category=cards` — 查询 MOD 图鉴数据',
            '- `GET /api/studio/file` / `POST /api/studio/file` — 读写 MOD 内任意 Lua/JS/CSS/JSON 文件并触发热加载',
            '- `GET /api/vanilla/catalog` / `GET /api/vanilla/detail` — 查阅原版图鉴与原版 ocgcore Lua 脚本',
            '- `POST /api/hotreload/start` / `POST /api/hotreload/reload` — 启动或触发游戏实时热加载',
            '- `POST /api/apply` / `POST /api/restore` — 零侵入虚拟版本包注入 / 无痕还原纯净原版',
            '- `POST /api/mcp/call` — 直接通过 HTTP JSON 调用任意内置 MCP 工具：`{"tool":"get_mod_guide","arguments":{"topic":"overview"}}`',
          ].join('\n');

    return isZh
      ? `你正在协助我通过本地 HTTP REST API 自动化开发与管理 **Yu-Gi-Oh! Rogue Duelist** MOD。

## 连接信息
- 本地 REST API 根地址：\`${apiBase}\`
- 内置 MCP（若支持也可直连）：\`${mcpUrl}\`
- 当前工作区：\`${ctx.workspaceRoot || '(默认工作区)'}\`
- 当前选中 MOD：\`${modId}\`（${modName}）· 原版游戏版本：\`${ctx.gameVersion || 'auto'}\`

## 常用本地 REST API 端点
${epList}

## 推荐调用步骤
1. 先调用 \`GET ${apiBase}/api/status\` 与 \`GET ${apiBase}/api/docs?lang=zh\` 确认实时状态与端点。
2. 如需查阅 Mod Spec v1 规范文档，可直接 \`POST ${apiBase}/api/mcp/call\` 传入 \`{"tool":"get_mod_guide","arguments":{"topic":"overview|settings|card|lua|js|dsl|relic|character|trainer|bitmasks"}}\`。
3. 修改文件后调用 \`POST ${apiBase}/api/studio/file\` 或 \`POST ${apiBase}/api/hotreload/reload\` 即时生效，切勿直接修改原版游戏目录。${customBlock}`
      : `You are helping me automate **Yu-Gi-Oh! Rogue Duelist** MOD authoring via the local HTTP REST API.

## Connection Info
- Local REST API Base URL: \`${apiBase}\`
- Built-in MCP Endpoint (if supported): \`${mcpUrl}\`
- Active Workspace: \`${ctx.workspaceRoot || '(default)'}\`
- Active MOD: \`${modId}\` (${modName}) · Base Game: \`${ctx.gameVersion || 'auto'}\`

## Key REST Endpoints
${epList}

## Recommended Flow
1. Call \`GET ${apiBase}/api/status\` and \`GET ${apiBase}/api/docs?lang=en\` to inspect live state.
2. Fetch any Mod Spec v1 guide via \`POST ${apiBase}/api/mcp/call\` with \`{"tool":"get_mod_guide","arguments":{"topic":"overview|settings|card|lua|js|dsl|relic|character|trainer|bitmasks"}}\`.
3. Write edits via \`POST ${apiBase}/api/studio/file\` and refresh with \`POST ${apiBase}/api/hotreload/reload\`. Never mutate vanilla game files directly.${customBlock}`;
  }

  if (preset === 'cards_lua') {
    return isZh
      ? `请使用已连接的 **ygo-rogue-mod** 内置 MCP 服务器（\`${mcpUrl}\`）为 **Yu-Gi-Oh! Rogue Duelist** 编写或优化自定义卡牌与 ocgcore-wasm Lua 决斗脚本。

## 当前上下文
- 目标 MOD：\`${modId}\`（${modName}）${scriptPath ? `\n- 当前打开脚本：\`${scriptPath}\`` : ''}
- 原版游戏版本：\`${ctx.gameVersion || 'auto'}\`

## 请按以下 MCP 流程执行
1. 调用 \`get_studio_context\` 确认我当前在创作台选中的卡牌和脚本。
2. 调用 \`get_mod_guide(topic="card")\`、\`get_mod_guide(topic="lua")\` 和 \`get_mod_guide(topic="bitmasks")\` 核对卡表字段与位掩码（自定义卡号推荐 \`190010000–199999999\`）。
3. 编写效果前，先调用 \`get_vanilla_card_script(query="相近效果的原版卡名或卡号")\` 参考本游戏真实的 ocgcore-wasm 0.1.2 写法（支持 3 返回值 \`local s, id, o = GetID()\`）。
4. 使用 \`add_mod_card\` 新增卡牌，或使用 \`patch_mod_file\` / \`write_mod_file\` 修改 \`lua/cards/c<ID>.lua\`（共享工具函数放 \`lua/lib/*.lua\` 会自动导入）。
5. 最后调用 \`validate_mod(modId="${modId}")\` 与 \`trigger_hot_reload\`，让改动立即在游戏试玩画面生效！${customBlock}`
      : `Use the connected **ygo-rogue-mod** MCP server (\`${mcpUrl}\`) to author or refine custom cards and ocgcore-wasm Lua scripts for **Yu-Gi-Oh! Rogue Duelist**.

## Context
- Target MOD: \`${modId}\` (${modName})${scriptPath ? `\n- Open Script: \`${scriptPath}\`` : ''}
- Base Game: \`${ctx.gameVersion || 'auto'}\`

## MCP Workflow
1. Call \`get_studio_context\` to inspect the currently selected card/script.
2. Call \`get_mod_guide(topic="card")\`, \`get_mod_guide(topic="lua")\`, and \`get_mod_guide(topic="bitmasks")\` (use IDs \`190010000–199999999\`).
3. Call \`get_vanilla_card_script(query="...")\` to study authentic vanilla ocgcore-wasm 0.1.2 patterns (\`local s, id, o = GetID()\`).
4. Use \`add_mod_card\`, \`patch_mod_file\`, or \`write_mod_file\` (shared helpers in \`lua/lib/*.lua\` are auto-imported).
5. Run \`validate_mod(modId="${modId}")\` and \`trigger_hot_reload\` to verify live.${customBlock}`;
  }

  if (preset === 'settings_js') {
    return isZh
      ? `请使用已连接的 **ygo-rogue-mod** 内置 MCP 服务器（\`${mcpUrl}\`）为 MOD \`${modId}\` 开发 **游戏内 F9 MOD 设置面板** 与 **JS / CSS 插件**（例如调节难度、动画/决斗倍速、掉落倍率、主题强调色或 HUD 开关）。

## 核心机制（Mod Spec v1 · Runtime v2.2）
- **所有 Entries 自动导入**：\`autoDiscoverEntries\` 默认为 \`true\`，放入 \`js/**/*.js|mjs\`、\`styles/**/*.css\`、\`css/**/*.css\`、\`lua/lib/**/*.lua\` 的文件全部自动加载（无需手写 \`jsEntries\`/\`cssEntries\`；需要排除辅助文件时在数组中写 \`"!js/helper.js"\`）。
- **游戏内 MOD 设置**：在 \`mod-manifest.json -> "settingsSchema"\` 或独立 \`settings.json\` 中声明控件（支持 \`boolean\` / \`range\` / \`number\` / \`select\` / \`text\` / \`color\` + \`zhLabel\` / \`zhDescription\`），游戏内按 **F9 →「MOD 设置」** 自动渲染并持久化到 \`localStorage\`。
- **JS 运行时 API**：在 \`export async function init(ctx)\` 中使用 \`ctx.settings.get(key, fallback)\`、\`ctx.settings.onChange((k, v, all) => ...)\`、\`ctx.settings.open()\`、\`ctx.addStyle(css, suffix)\`、\`ctx.onFetch(regex, fn)\`、\`ctx.onUiChange(fn)\`。

## 请按以下 MCP 流程执行
1. 先调用 \`get_mod_guide(topic="settings")\` 和 \`get_mod_guide(topic="js")\` 查看完整 Schema 与 API 规范。
2. 调用 \`list_mod_files(modId="${modId}")\` 了解现有文件结构。
3. 使用 \`write_mod_file\` / \`patch_mod_file\` 编写 \`settings.json\`（或更新 \`mod-manifest.json\`）以及 \`js/*.js\`、\`styles/*.css\`（严格遵守本地 CSP：禁止引用外部 CDN 脚本/图片）。
4. 调用 \`validate_mod(modId="${modId}")\` 和 \`trigger_hot_reload\` 立即在游戏内按 F9 测试！${customBlock}`
      : `Use the connected **ygo-rogue-mod** MCP server (\`${mcpUrl}\`) to build **in-game F9 MOD Settings** and **auto-imported JS/CSS plugins** for MOD \`${modId}\` (e.g. difficulty scaling, speed multiplier, reward rate, UI toggles, theme colors).

## Core Capabilities (Mod Spec v1 · Runtime v2.2)
- **All-Entries Auto-Import**: \`autoDiscoverEntries: true\` auto-loads all \`js/**/*.js|mjs\`, \`styles/**/*.css\`, \`css/**/*.css\`, and \`lua/lib/**/*.lua\` (exclude helper files with \`"!js/helper.js"\`).
- **In-Game F9 MOD Settings**: Define \`settingsSchema\` in \`mod-manifest.json\` or standalone \`settings.json\` (\`boolean\`, \`range\`, \`number\`, \`select\`, \`text\`, \`color\` with bilingual \`label\`/\`zhLabel\`) — auto-rendered in **F9 → MOD Settings** with persistence.
- **Runtime JS API**: \`ctx.settings.get/set/getAll/reset/onChange/define/open\`, \`ctx.addStyle\`, \`ctx.onFetch\`, \`ctx.onUiChange\`, \`ctx.showToast\`.

## MCP Workflow
1. Call \`get_mod_guide(topic="settings")\` and \`get_mod_guide(topic="js")\`.
2. Call \`list_mod_files(modId="${modId}")\` to inspect existing files.
3. Author \`settings.json\` + \`js/*.js\` + \`styles/*.css\` via \`write_mod_file\` / \`patch_mod_file\` (CSP-safe: no external CDN scripts).
4. Call \`validate_mod(modId="${modId}")\` and \`trigger_hot_reload\`.${customBlock}`;
  }

  if (preset === 'content_ypk') {
    return isZh
      ? `请使用已连接的 **ygo-rogue-mod** 内置 MCP 服务器（\`${mcpUrl}\`）为 MOD \`${modId}\` 扩展角色、遗物、卡包、预组卡组、修改器，或导入 YGOPro/MDPro3 \`.ypk\` 扩展卡包。

## 可用 MCP 创作工具
- 查阅规范：\`get_mod_guide(topic="character|relic|pack|deck|trainer|dsl")\`
- 查原版数据：\`search_game_data(kind="cards|characters|relics|packs", query="...")\`
- 声明式创作：\`add_mod_character\`、\`add_mod_relic\`、\`add_mod_pack\`、\`add_mod_preconstructed_deck\`、\`configure_mod_trainer\`
- 导入 YGOPro/MDPro3 扩展包：\`import_ypk(ypkPath="...")\`（内置纯 C# SQLite \`.cdb\` 解析，自动提取卡表、Lua 脚本、卡图、\`.ydk\` 并生成扩展卡包）
- 整理元数据与封面：\`update_mod_metadata(modId="${modId}", category="...", cover="...")\`
- 校验与生效：\`validate_mod\` → \`trigger_hot_reload\` / \`apply_or_restore_mods\`

请先查询相关指南与原版数据，再执行修改并触发热加载！${customBlock}`
      : `Use the connected **ygo-rogue-mod** MCP server (\`${mcpUrl}\`) to author characters, relics, booster packs, preconstructed decks, trainers, or import YGOPro/MDPro3 \`.ypk\` archives for MOD \`${modId}\`.

## MCP Authoring Tools
- Guides: \`get_mod_guide(topic="character|relic|pack|deck|trainer|dsl")\`
- Vanilla lookup: \`search_game_data(kind="cards|characters|relics|packs", query="...")\`
- Authoring: \`add_mod_character\`, \`add_mod_relic\`, \`add_mod_pack\`, \`add_mod_preconstructed_deck\`, \`configure_mod_trainer\`
- YPK Import: \`import_ypk(ypkPath="...")\` (pure-C# SQLite \`.cdb\` parser extracts cards, Lua scripts, art & \`.ydk\` decks)
- Metadata: \`update_mod_metadata(modId="${modId}", category="...", cover="...")\`
- Verify: \`validate_mod\` → \`trigger_hot_reload\` / \`apply_or_restore_mods\`${customBlock}`;
  }

  if (preset === 'focus_script') {
    return isZh
      ? `请通过已连接的 **ygo-rogue-mod** 内置 MCP 服务器（\`${mcpUrl}\`）直接精修我当前在创作台打开的脚本：

- 当前 MOD：\`${modId}\`（${modName}）${itemName ? `\n- 当前条目：${itemName}` : ''}${scriptPath ? `\n- 目标脚本路径：\`${scriptPath}\`` : ''}

## 执行要求
1. 先调用 \`get_studio_context(includeScriptContent=true)\` 读取该脚本最新完整内容。
2. 如涉及 ocgcore Lua 写法不确定，先调用 \`get_vanilla_card_script\` 参考原版脚本或调用 \`get_mod_guide\`。
3. 优先使用 \`patch_mod_file\` 做精准最小改动（或必要时用 \`write_mod_file\`），保持代码风格整洁、无语法错误。
4. 改完后调用 \`validate_mod(modId="${modId}")\` 并调用 \`trigger_hot_reload\`，创作台编辑器与游戏预览会自动实时刷新！${customBlock}`
      : `Use the connected **ygo-rogue-mod** MCP server (\`${mcpUrl}\`) to edit the exact script currently open in MOD Studio:

- Active MOD: \`${modId}\` (${modName})${itemName ? `\n- Selected Item: ${itemName}` : ''}${scriptPath ? `\n- Script Path: \`${scriptPath}\`` : ''}

## Steps
1. Call \`get_studio_context(includeScriptContent=true)\` to read the latest script content.
2. Call \`get_vanilla_card_script\` or \`get_mod_guide\` if you need official ocgcore/API references.
3. Apply clean targeted edits with \`patch_mod_file\` (or \`write_mod_file\`).
4. Run \`validate_mod(modId="${modId}")\` and \`trigger_hot_reload\` so Studio & Live Preview refresh instantly.${customBlock}`;
  }

  // Default: all_in_one
  return isZh
    ? `你现在是 **Yu-Gi-Oh! Rogue Duelist** 的专业 MOD 开发助手。桌面端 MOD 工坊正在本机运行，并同时提供 **内置实时 MCP 服务器** 与 **本地 HTTP REST API**。

## 1. 连接与环境上下文
- **MCP (Streamable HTTP 推荐)**：\`${mcpUrl}\`
- **MCP (旧版 SSE)**：\`${sseUrl}\`
- **本地 REST API**：\`${apiBase}\`（完整端点列表：\`GET ${apiBase}/api/docs?lang=zh\`）
- **当前工作区**：\`${ctx.workspaceRoot || '(默认工作区)'}\`
- **当前选中 MOD**：\`${modId}\`（${modName}）${scriptPath ? ` · 聚焦脚本：\`${scriptPath}\`` : ''}
- **原版游戏版本**：\`${ctx.gameVersion || 'auto'}\`

## 2. 内置 MCP 全部工具（${ctx.tools?.length || 23} 个）
\`${toolNames}\`

## 3. 如何查阅规范指南（按需调用 \`get_mod_guide\`）
可选 \`topic\`：\`overview\` | \`settings\` (F9 游戏内设置) | \`card\` | \`lua\` | \`bitmasks\` | \`character\` | \`relic\` | \`pack\` | \`deck\` | \`trainer\` | \`js\` | \`dsl\` (Xmake 双域 mod.lua/mod.js)

## 4. 本工坊核心功能与规范要点
- **零侵入虚拟版本包注入**：永不修改原版 EXE/DLL 或原版 \`web-*.zip\`，支持多 MOD 叠加与无痕还原。
- **所有 Entries 自动导入**（\`autoDiscoverEntries: true\` 默认开启）：放入 \`js/**/*.js|mjs\`、\`styles/**/*.css\`、\`css/**/*.css\`、\`lua/lib/**/*.lua\` 的所有文件自动加载；如需排除单个文件在对应数组加 \`"!路径"\`。
- **分类与封面**：支持 \`category\`（9 大分类）与 \`cover\`（\`.png/.webp/.jpg/.svg\`），可通过 \`update_mod_metadata\` 更新。
- **游戏内 MOD 设置（F9 → MOD 设置）**：在 \`mod-manifest.json -> settingsSchema\` 或 \`settings.json\` 声明控件（\`boolean/range/number/select/text/color\`），JS 中用 \`ctx.settings.get / onChange / open\` 实时控制难度、倍速、倍率或界面主题。
- **游戏内调试与修改器**：内置 F8 实时修改器、F9 MOD 列表与设置、F10 开发者设置与调试模式。
- **YGOPro / MDPro3 \`.ypk\` 导入**：支持 \`import_ypk\` 自动解析 \`.cdb\` 卡表、Lua 脚本、卡图与 \`.ydk\` 卡组。

## 5. 推荐标准工作流
1. 先调用 \`get_studio_context\` 或 \`get_manager_status\` 了解当前状态。
2. 调用 \`get_mod_guide\` 和 \`get_vanilla_card_script\` / \`search_game_data\` 获取真实原版参考。
3. 通过 MCP 工具创建/修改 MOD（若无 MCP 工具权限，则改用 \`${apiBase}/api/...\` 本地 HTTP API）。
4. 修改后调用 \`validate_mod\` 校验，再调用 \`trigger_hot_reload\` 让桌面预览即时生效！${customBlock}`
    : `You are an expert **Yu-Gi-Oh! Rogue Duelist** MOD development assistant. Desktop MOD Studio is running locally with both a **Built-in Live MCP Server** and a **Local HTTP REST API**.

## 1. Connection & Live Context
- **MCP (Streamable HTTP, Recommended)**: \`${mcpUrl}\`
- **MCP (Legacy SSE)**: \`${sseUrl}\`
- **Local REST API**: \`${apiBase}\` (full schema: \`GET ${apiBase}/api/docs?lang=en\`)
- **Workspace**: \`${ctx.workspaceRoot || '(default)'}\`
- **Active MOD**: \`${modId}\` (${modName})${scriptPath ? ` · Focused Script: \`${scriptPath}\`` : ''}
- **Base Game Version**: \`${ctx.gameVersion || 'auto'}\`

## 2. All Built-in MCP Tools (${ctx.tools?.length || 23})
\`${toolNames}\`

## 3. Official Spec Guides (\`get_mod_guide\`)
Topics: \`overview\` | \`settings\` (In-game F9 settings) | \`card\` | \`lua\` | \`bitmasks\` | \`character\` | \`relic\` | \`pack\` | \`deck\` | \`trainer\` | \`js\` | \`dsl\` (Xmake mod.lua/mod.js)

## 4. Platform Capabilities & Rules
- **Zero-Touch Virtual Bundle**: Never modifies vanilla EXE/DLL or vanilla \`web-*.zip\`.
- **All-Entries Auto-Import** (\`autoDiscoverEntries: true\` default): auto-imports all \`js/**/*.js|mjs\`, \`styles/**/*.css\`, \`css/**/*.css\`, \`lua/lib/**/*.lua\` (use \`"!path"\` to exclude).
- **Categories & Covers**: \`category\` + \`cover\` (.png/.webp/.jpg/.svg) via \`update_mod_metadata\`.
- **In-Game MOD Settings (F9)**: \`settingsSchema\` / \`settings.json\` + \`ctx.settings.get/set/onChange/open\` for live difficulty, speed, multipliers & toggles.
- **In-Game Tools**: F8 Live Trainer, F9 MODs & Settings, F10 Dev/Debug.
- **YGOPro / MDPro3 \`.ypk\` Import**: \`import_ypk\` parses SQLite \`.cdb\`, Lua scripts, art & \`.ydk\` decks natively.

## 5. Recommended Workflow
1. Call \`get_studio_context\` or \`get_manager_status\`.
2. Consult \`get_mod_guide\` and \`get_vanilla_card_script\` / \`search_game_data\`.
3. Author/patch files via MCP (or fallback to \`${apiBase}/api/...\` REST endpoints).
4. Call \`validate_mod\` and \`trigger_hot_reload\` to test immediately!${customBlock}`;
}
