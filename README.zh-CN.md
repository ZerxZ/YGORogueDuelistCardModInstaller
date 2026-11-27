# Yu-Gi-Oh! Rogue Duelist MOD 管理器 & Modding SDK

**中文** | [English](README.md)

面向 [**Yu-Gi-Oh! Rogue Duelist**](https://github.com/Thronior/ygo-rogue-duelist) 的**零侵入多 MOD 管理器**、**仿 Xmake 双域 Lua/JS DSL 开发套件**、**可视化 WebView2 创作工坊 (HeroUI v3 + React + CodeMirror 6 + 开放本地 REST/SSE API)**、**命令行工具 (CommandLineParser)** 与 **AI MCP 服务器** —— 全套原生 **Native AOT** 单文件发布。

---

## 核心架构：零侵入虚拟版本包注入

*Yu-Gi-Oh! Rogue Duelist* 官方桌面端启动器（`Yu-Gi-Oh! Rogue Duelist.dll`）会校验内置 `web-<ver>.zip` 的 `StartupHashes.json`，同时会在 `%LOCALAPPDATA%\Yu-Gi-Oh! Rogue Duelist\Game\` 中自动选择通过 SHA-256 完整性校验且版本号最高的 `<ver>` 目录启动。

本管理器完全复用官方内置升级校验机制，将全部已启用的 MOD 合成到隔离虚拟版本目录 `Game\900.x.y`：

- **零破坏**：完全不修改游戏 `EXE / DLL` 与原版 `web-*.zip`
- **多 MOD 自由叠加**：可视化控制启用开关、加载顺序、资源冲突检测与最低版本校验
- **无惧游戏更新**：官方更新原版游戏后，所有 MOD 列表完整保留，点击「应用注入」即可一键重新合成到新版本
- **一键无痕还原**：直接清理 `Game\900+.*` 与缓存，瞬间回到 100% 纯净原版
- **完整脚本与资产扩展**：支持新增任意文件、Lua 卡牌脚本/共享库、ES Module JS 插件、CSS 样式表、卡图、角色、遗物、卡包、预组卡组与修改器

---

## 功能特性

| 模块 | 说明 |
| :--- | :--- |
| **零侵入多 MOD 管理** | 支持导入 `.zip`、目录、工作区整合包及旧版 `data/mod.json`；自定义加载优先级；自动资源冲突分析；一键还原纯净版与检查官方 GitHub 更新。 |
| **仿 Xmake 双域 DSL** | 支持用 **`mod.lua`** 和/或 **`mod.js`** 声明 MOD：**描述域**负责元数据、`set_min_gamever` 最低版本限制、通配符文件匹配及 `card`/`relic`/`character`/`pack`/`preconstructed`/`trainer` 目标块；**脚本域**负责内联 Lua 决斗脚本/遗物效果，以及自动编译为 ES Module 的 JS 钩子（`on_load`、`on_fetch`、`on_ui_change`、`on_hotkey`）。 |
| **全要素内容制作** | 一站式支持新增 **卡牌** (`cards/*.json`)、**Lua 脚本/共享库** (`lua/cards/`, `lua/lib/`)、**可选角色** (`characters/*.json`)、**遗物** (`relics/*.json`)、**自定义卡包** (`packs/*.json`)、**预组构筑卡组 & YGOPro `.ydk`** (`preconstructed/*.json`, `decks/*.ydk`)、**JS/CSS 插件** (`js/`, `styles/`) 与 **修改器** (`trainer.json` + 游戏内 `F8` 浮窗)。 |
| **开发者模式 (`F10`)** | 开启后自动注入游戏内 **`F10` 开发者调试面板**、挂载 `window.YgoModDev` 控制台工具、添加悬浮 DEV 快捷栏，并自动解锁沙盒自由决斗与角色形态。 |
| **可视化图鉴创作工坊 & 原版图鉴复制** | 采用原生 **Native AOT + Photino WebView2 + HeroUI v3 (OKLCH 明暗双主题) + Vite/React/TypeScript** 架构，与游戏本体技术栈一致。内置**无限滚动（IntersectionObserver）**的 MOD 可视化画廊与离线原版图鉴（2143 卡牌、116 角色、324 遗物、86 卡包、22 预组，100% 实图显示与一键克隆），集成 **CodeMirror 6 IDE**（支持 Xmake DSL / ocgcore Lua / `window.YgoModAPI` 智能补全与 JSON 校验），以及全局多工作区管理。 |
| **实时热加载 & 内嵌游戏试玩** | 内置本地 HTTP/SSE 热加载服务器 + 文件监听（`http://127.0.0.1:17845/`）：CSS 无刷新热更新、卡牌/Lua/JS/角色/遗物/卡组内存秒级重合成，并支持在工坊「内嵌热加载试玩」标签页直接试玩或注入桌面游戏桥。 |
| **开放本地 REST & SSE API** | 内置 `http://127.0.0.1:17850/api/*` 环回 API（含 `/api/docs` 文档与 `/api/events` SSE 推送），支持 `--api-only` 无头模式，方便脚本、浏览器与外部工具自动化集成。 |
| **最低版本与工作区打包** | 支持 `minGameVersion` / `maxGameVersion` / `minManagerVersion` 版本守卫；支持一键打包单个 MOD（`.zip`）或整个 **MOD 工作区整合包**（保留加载顺序、启用状态与自动计算的最低版本），分享给他人后一键导入。 |
| **三端 Native AOT 程序** | **WebView2 桌面工坊（中/英双语切换）**、**CommandLineParser CLI**、以及 **MCP 服务器**（让 AI 助手直接检索游戏原版数据、创建/验证/打包并注入 MOD）。 |

---

## 快速上手

### 1. 桌面创作工坊（Native AOT WebView2 · 中英双语）

直接运行 `YGORogueDuelistCardModInstaller.Desktop.exe`：
- 点击右上角 **`EN` / `中文`** 按钮可随时切换双语界面。
- 在 **原版图鉴参考** 中无限滚动浏览原版卡牌/角色/遗物/卡包/预组实图与 Lua 脚本，一键复制或克隆到你的 MOD。
- 在 **可视化 MOD 创作工作台** 中使用画廊网格 + 可视化表单 + **CodeMirror 6**（`Ctrl+Space` 智能提示、`Ctrl+S` 保存并热重载）高效开发。
- 在 **内嵌热加载试玩** 中即时测试效果，或点击 **`应用注入`** / **`注入并启动游戏`** 畅玩。

### 2. 命令行工具（CLI）

```powershell
# 查看 MOD 列表、版本告警与冲突检测
ygo-mod.exe list

# 创建 MOD 模板并指定最低游戏版本要求
ygo-mod.exe create my-pack --name "我的扩展包" --author "Me" --min-game-ver 0.2.46

# 导入单个 MOD（.zip 或目录）或工作区整合包（自动识别）
ygo-mod.exe import .\examples\sample-xmake-dsl-mod

# 启用 / 禁用 / 调整加载顺序
ygo-mod.exe enable my-pack
ygo-mod.exe disable my-pack
ygo-mod.exe order my-pack 1

# 开启/关闭开发者模式
ygo-mod.exe dev on

# 执行零侵入虚拟包注入
ygo-mod.exe apply --mode virtual --dev --yes --no-pause

# 打包单个 MOD 或整个工作区整合包
ygo-mod.exe pack-mod my-pack
ygo-mod.exe pack-workspace --name "我的整合包" --version "1.0.0" --min-game-ver "0.2.46"

# 一键导入工作区整合包
ygo-mod.exe import-workspace .\dist\My-Modpack-v1.0.0.zip

# 一键还原纯净原版
ygo-mod.exe restore --yes --no-pause
```

### 3. 接入 AI MCP 服务器（让 AI 帮你做 MOD）

将编译出的 `YGORogueDuelistCardModInstaller.Mcp.exe` 配置到支持 MCP 的客户端（Claude Desktop、Cursor、Windsurf、VS Code 等）：

```json
{
  "mcpServers": {
    "ygo-rogue-mod-manager": {
      "command": "C:/path/to/YGORogueDuelistCardModInstaller.Mcp.exe",
      "args": []
    }
  }
}
```

内置 **15 个工具**（查询开发指南、读取管理器状态、检索原版游戏数据、创建 MOD、添加卡牌/角色/遗物/卡包/预组卡组、配置修改器、读写文件、校验合法性、打包单 MOD 或工作区、应用/还原注入）以及文档资源与提示词模板。

---

## Native AOT 原生编译发布

需要 **.NET 10 SDK**、**Node.js 22+ (pnpm)** 与 Windows **Visual Studio C++ x64 生成工具**：

```powershell
# 1. 构建前端资源并嵌入 Desktop/wwwroot/
cd frontend
pnpm install
pnpm build
cd ..

# 2. 编译完整解决方案
dotnet build YGORogueDuelistCardModInstaller.slnx -c Release

# 3. 发布三个 Native AOT 独立可执行程序 (win-x64)
dotnet publish YGORogueDuelistCardModInstaller -c Release -r win-x64 -o dist\aot\cli
dotnet publish YGORogueDuelistCardModInstaller.Desktop -c Release -r win-x64 -o dist\aot\gui
dotnet publish YGORogueDuelistCardModInstaller.Mcp -c Release -r win-x64 -o dist\aot\mcp
```

完整 MOD 规范与双域 DSL 语法参考请查阅 [docs/MOD_DEVELOPMENT_GUIDE.md](docs/MOD_DEVELOPMENT_GUIDE.md) 与 [examples/sample-xmake-dsl-mod](examples/sample-xmake-dsl-mod)。
