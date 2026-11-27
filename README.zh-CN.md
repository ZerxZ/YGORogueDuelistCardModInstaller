# Yu-Gi-Oh! Rogue Duelist MOD 创作工坊 & Modding SDK

**简体中文** | [English](README.md)

> [!IMPORTANT]
> **AI 编写与维护声明**
> - **全程由 Gemini AI 编写**：本项目从底层 C# Native AOT 引擎、React 19 + HeroUI v3 可视化桌面工坊、内置 MCP 服务器、官方 `.delta.gz` 增量补丁解析器、Lua/JS 双域 DSL 到全套技术文档，**全程均由 Gemini AI 编写完成**。
> - **仅作技术参考 · 不承诺长期维护**：本项目**不会进行长期维护**，旨在为社区提供一套完整可用的开源技术参考与工程原型。非常欢迎各位开发者 **Fork、二次开发、按需精简或继续完善**！

面向 [**Yu-Gi-Oh! Rogue Duelist**](https://github.com/Thronior/ygo-rogue-duelist) 的**零侵入多 MOD 管理器**、**免原版 EXE 独立 `web-*.zip` 游玩/增量更新器**、**可视化创作工坊（独立弹窗编辑 + 战役与原版删减可视化 + CodeMirror 6）**、**仿 Xmake Lua/JS DSL 开发套件**、**跨平台打包方案（Windows / Linux / Android）**、**命令行工具**与 **23 工具 AI MCP 服务器**。

---

## 核心特性总览

### 1. 零侵入虚拟版本包 + 官方识别标记 (`-mod`) 与服务器隔离
- 复用官方启动器自带的 SHA-256 完整性校验机制，将全部已启用的 MOD 合成到隔离虚拟版本目录 `Game\900.x.y` —— **永不改动原版 `EXE / DLL` 与原版 `web-*.zip`**，支持一键无痕还原纯净版。
- **防止混淆原版与 MOD 版**：合成时自动将版本号标记为 `v<ver>-mod`（标题画面显示 `v0.2.47-mod · MOD (N)`，并在 `build.json` 中写入 `modded: true`），同时自动隔离官方 Cloudflare Workers（`global-stats` 统计、`replays` 回放、`shadow-run-rooms` 联机房），除非 MOD 显式配置自己的 `multiplayer-config.json`。

### 2. 免装原版 EXE：直接下载 `web-*.zip` 试玩、游玩与极速增量更新
- **无需安装 ~270 MB 原版 `.exe` 安装包**：在新手引导、顶部提示条、内嵌试玩页或更新中心点击「**免装 EXE · 下载 `web-.zip` 立即试玩**」，即可直接拉取数据包并在工坊内嵌窗口或默认浏览器（`http://127.0.0.1:17845/`）即刻开玩（含全部已启用 MOD 热加载）！
- **官方 `.delta.gz` 增量补丁 (`YGODLT01`)**：纯 C# Native AOT 实现的官方增量补丁合并引擎（`DeltaPatchService.cs`），更新版本时自动优先下载 **~5.5 MB** 增量补丁（节省 97% 流量！），并支持 HTTP Range 断点续传与 GitHub 代理加速（`ghfast.top` 等）。

### 3. 全方位可视化创作工坊与深度 MOD 扩展能力
| 功能模块 | 详细说明 |
| :--- | :--- |
| **弹窗式可视化创作 & 全套封面上传** | 卡牌、角色、遗物、卡包、预组卡组采用全宽 7 列图鉴画廊，点击条目弹出宽敞独立编辑窗口；全部支持本地浏览、文件上传与拖拽更换封面图，自动生成 `assets/cards/*.jpg`、`assets/packs/*.jpg`、`assets/decks/*.jpg`（永不裂图）。 |
| **战役规则与原版删减可视化 (`campaign-config.json`)** | 无需手改 Python：可视化**删减原版卡牌 ID、隐藏原版角色、移除原版卡包/预组/遗物**，并支持勾选关闭全部 23 种原版 Boss 诅咒，或一键彻底关闭 **Boss/路线诅咒系统**、**暗影诅咒遗物系统**、**遗物封印系统 (`relic_seal`)**、**商店遗物放逐**、**全部 46 条遗物隐藏融合配方**，以及覆盖起始金币/LP、Boss 间隔、商店卡包数与刷新价格。 |
| **现代时代召唤机制解锁 (`MR2–MR5`)** | 深度适配原版内置的 EDOPro `ocgcore-wasm 0.1.2`（已含 `proc_synchro/xyz/pendulum/link.lua`）：只要在 `mod-manifest.json` 或战役配置选择 `mr2..mr5`，或添加任意同调/超量/灵摆/连接卡，自动解锁对应大师规则、EMZ 额外怪兽区与 3 返回值 `GetID()`！ |
| **纯原生 `.ypk` 扩展卡包导入** | 支持在 MOD 列表或创作工作台直接拖拽/选择导入 YGOPro / EDOPro / MDPro3 `.ypk` 扩展包，原生解析 `.cdb` SQLite 卡表、Lua 脚本、卡图与 `.ydk` 卡组并自动生成卡包。 |
| **所有 Entries 自动导入 & 游戏内 `F9` 设置** | 默认自动扫描加载 `js/**`、`styles/**`、`css/**`、`lua/lib/**`；在 `mod-manifest.json` 或 `settings.json` 声明 `settingsSchema` 后，玩家可在游戏内按 **`F9 -> MOD 设置`** 实时调整难度、倍速、倍率、主题色等参数。 |
| **仿 Xmake 双域 DSL (`mod.lua` / `mod.js`)** | 支持在单一脚本文件中声明卡牌/遗物/角色/卡包/预组/修改器并内联编写 Lua 决斗效果与 JS 运行时钩子（`on_load`/`on_fetch`/`on_ui_change`/`on_hotkey`）。 |
| **实时热加载、`F8` 修改器与 `F10` 开发者模式** | 内置本机 + 局域网 HTTP/SSE 热加载服务器（`http://127.0.0.1:17845/`）、`F8` 游戏内实时修改器（自动维护 SHA-256 存档校验）与 `F10` 开发者调试面板。 |
| **内置 23 工具 AI MCP + 深度学习技能** | 同时提供 Streamable HTTP (`/mcp`)、传统 SSE (`/mcp/sse`) 与独立 Stdio MCP，内置 7 套「AI 提示词一键复制预设」与完整的 `ygo-rogue-modding` 技能文档。 |
| **跨平台打包方案（Windows / Linux / Android APK） & `.yrdpk` 格式** | GitHub Actions 自动构建 **Windows (`win-x64`) + Linux (`linux-x64`) + 轻量 Android 只导入 MOD 加载器 APK (`android-loader/`，纯 Java < 100 KB，零拷贝叠加层运行)**；支持 **`.yrdpk`** 专属 MOD 打包/导入格式（本质为 ZIP，类似 `.ypk`，桌面端/CLI/安卓端全兼容），以及 Linux 一键包与 Android 覆盖包导出。 |

---

## 文档导航

- **[MOD 完整开发指南 (Mod Spec v1 & DSL)](docs/MOD_DEVELOPMENT_GUIDE.md)** — 目录规范、卡牌/角色/遗物/卡包/预组、F9 设置、现代时代 MR2–MR5、`campaign-config.json` 原版删减与 JS/Lua API。
- **[原版遗物系统深度技术调查](docs/RELIC-SYSTEM-DEEP-DIVE.md)** — 基于 `v0.2.47` 源码完整剖析 12 个遗物 Python 模块、6 大遗物层级、46 条隐藏融合配方、`passives.py` -> `shadow-passives.lua` 编译管线、Hint 协议、封印系统与暗影遗物。
- **[Windows / Linux / Android 跨平台打包方案](docs/CROSS-PLATFORM-PACKAGING.md)** — 涵盖「导出给 Linux/Android 游玩的 MOD 包」与「将本 MOD 管理器本身打包到 Linux / Android (Termux / WebView 封装)」完整方案。
- **[AI 辅助制作技能 (`ygo-rogue-modding`)](.agents/skills/ygo-rogue-modding/SKILL.md)** — 面向 Cursor / Claude / Codex / Gemini 的 `runtime/script` 与遗物系统参考技能。

---

## 快速上手

### 1. 桌面创作工坊（Windows `win-x64` / Linux `linux-x64`）
直接运行 `YGORogueDuelistCardModInstaller.Desktop`：
- **首次启动引导**：选择 **简体中文 / English**、检测游戏目录，或点击「**免装 EXE · 下载 `web-.zip` 试玩**」无需原版安装包即可开玩！
- **无头 Web 服务模式**：运行 `YGORogueDuelistCardModInstaller.Desktop --serve`（适合 Linux 服务器或 Android Termux），然后在任意浏览器打开 `http://127.0.0.1:17850/` 即可使用完整可视化界面。

### 2. 命令行工具（CLI）
```powershell
# 查看 MOD 列表、版本兼容与冲突检测
ygo-mod.exe list

# 新建 MOD 或导入 .zip / .ypk / 目录 / 工作区整合包
ygo-mod.exe create my-pack --name "我的扩展包" --author "Me" --min-game-ver 0.2.47
ygo-mod.exe import .\examples\sample-xmake-dsl-mod

# 执行零侵入虚拟包注入 / 还原纯净原版
ygo-mod.exe apply --mode virtual --dev --yes --no-pause
ygo-mod.exe restore --yes --no-pause

# 打包单个 MOD 或整个工作区整合包
ygo-mod.exe pack-mod my-pack
ygo-mod.exe pack-workspace --name "我的整合包" --version "1.0.0"
```

### 3. 接入内置 AI MCP 服务器（23 个工具）
启动桌面工坊后，在 Cursor / Claude Desktop / Codex / VS Code 中配置 HTTP MCP 地址即可直连（也可在工坊内一键写入 `.mcp.json`）：
```json
{
  "mcpServers": {
    "ygo-rogue-mod": {
      "url": "http://127.0.0.1:17850/mcp"
    }
  }
}
```

---

## 从源码编译（Native AOT · Windows & Linux）

需要 **.NET 10 SDK**、**Node.js 22+ (`pnpm`)** 与 Native AOT C++ 工具链（Windows 需 Visual Studio C++ x64 生成工具；Linux 需 `clang` + `zlib1g-dev`）：

```bash
# 1. 构建前端（自动输出并嵌入 Desktop/wwwroot/）
cd frontend && pnpm install --frozen-lockfile && pnpm build && cd ..

# 2. 编译完整解决方案
dotnet build YGORogueDuelistCardModInstaller.slnx -c Release

# 3. 发布三个 Native AOT 独立可执行程序（Linux 下将 win-x64 改为 linux-x64）
dotnet publish YGORogueDuelistCardModInstaller         -c Release -r win-x64 -o dist/aot/cli
dotnet publish YGORogueDuelistCardModInstaller.Desktop -c Release -r win-x64 -o dist/aot/gui
dotnet publish YGORogueDuelistCardModInstaller.Mcp     -c Release -r win-x64 -o dist/aot/mcp
```

---

## 致谢与协议
- 专为 Thronior 制作的 [**Yu-Gi-Oh! Rogue Duelist**](https://github.com/Thronior/ygo-rogue-duelist) 构建。
- 遵循 [LICENSE](LICENSE) 开源协议，欢迎自由二次开发与完善。
