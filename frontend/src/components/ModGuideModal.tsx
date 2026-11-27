import { useState } from 'react';
import { Button, Chip, Modal } from '@heroui/react';
import {
  BookOpen,
  Bot,
  Check,
  Code2,
  Copy,
  FolderTree,
  Layers,
  Sliders,
  Sparkles,
  Wand2,
} from 'lucide-react';
import type { I18nDict } from '../i18n';
import type { Lang } from '../types';

interface ModGuideModalProps {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
  t: I18nDict;
  lang: Lang;
  onCreateExample?: () => void;
  onCopyAiPrompt?: () => void;
}

type GuideTabKey = 'layout' | 'entries' | 'settings' | 'cards' | 'content' | 'js_mcp';

interface GuideSection {
  key: GuideTabKey;
  labelZh: string;
  labelEn: string;
  icon: React.ReactNode;
  summaryZh: string;
  summaryEn: string;
  tipsZh: string[];
  tipsEn: string[];
  codeTitle: string;
  code: string;
}

const GUIDE_SECTIONS: GuideSection[] = [
  {
    key: 'layout',
    labelZh: '目录结构 · 分类 · 封面',
    labelEn: 'Layout · Category · Cover',
    icon: <FolderTree className="size-3.5" />,
    summaryZh:
      '每个 MOD 都是 mods/<mod-id>/ 下的独立目录。采用零侵入虚拟版本包注入，原版游戏文件永不被破坏，可无限叠加多个 MOD 并随时无痕还原。',
    summaryEn:
      'Every MOD lives in mods/<mod-id>/. Zero-Touch Virtual Bundle injection never mutates vanilla game files and stacks unlimited MODs cleanly.',
    tipsZh: [
      '分类 (category)：支持 cards / characters / relics / packs_decks / ui_theme / script_plugin / trainer / overhaul / other；留空则自动识别，也可直接拖拽 MOD 行到分类标签归类。',
      '封面 (cover)：支持 .png / .webp / .jpg / .svg。在根目录放入 cover.png/svg 即可自动识别，或把图片直接拖到右侧封面预览区更换！',
      '加载顺序：列表从上到下叠加，排在下面的 MOD 优先级更高（拖拽左侧抓手可自由调整顺序）。',
      '支持直接导入 YGOPro / MDPro3 `.ypk` 扩展卡包：点击「导入 ZIP / YPK」或直接拖入 .ypk 文件，内置纯 C# SQLite 解析器自动提取 .cdb 卡表、script/c*.lua、pics 卡图与 .ydk 卡组！',
    ],
    tipsEn: [
      'Category: cards | characters | relics | packs_decks | ui_theme | script_plugin | trainer | overhaul | other (leave empty for auto-detection, or drag rows onto category chips).',
      'Cover: .png / .webp / .jpg / .svg. Auto-detects cover.* / icon.* / first art/* image, or drop an image onto the Cover banner.',
      'Load Order: applied top-to-bottom; lower MODs override upper ones. Drag rows directly to reorder.',
      'YGOPro / MDPro3 .ypk Import: click Import ZIP/YPK or drag-drop a .ypk file — built-in zero-dependency SQLite .cdb reader extracts cards, Lua scripts, card art, and .ydk decks automatically!',
    ],
    codeTitle: 'mods/my-mod/mod-manifest.json',
    code: `{
  "schemaVersion": 1,
  "id": "my-awesome-mod",
  "name": "我的自定义扩展 MOD",
  "version": "1.0.0",
  "author": "YourName",
  "category": "overhaul",
  "cover": "cover.png",
  "autoDiscoverEntries": true,
  "minGameVersion": "0.2.46",
  "tags": ["cards", "settings", "custom"]
}`,
  },
  {
    key: 'entries',
    labelZh: '全部 Entries 自动导入',
    labelEn: 'Auto-Import All Entries',
    icon: <Layers className="size-3.5" />,
    summaryZh:
      '默认开启 autoDiscoverEntries: true，放入对应目录的所有 JS、CSS 与 Lua 公共库会自动发现并加载，再也不用每次手改 mod-manifest.json！',
    summaryEn:
      'With autoDiscoverEntries: true (default), all JS modules, CSS stylesheets, and Lua helper libs in standard folders are auto-imported without manual manifest edits.',
    tipsZh: [
      'JS 插件：自动扫描 js/**/*.js 与 js/**/*.mjs（优先加载 js/main.js，其余按字母序）。',
      'CSS 样式：自动同时扫描 styles/**/*.css 与 css/**/*.css（优先加载 styles/mod.css）。',
      'Lua 公共库：自动扫描 lua/lib/**/*.lua，安全追加到游戏全局 utility.lua。',
      '局部排除：若某个 js/utils.js 仅被 main.js import 不想作为独立入口执行，只需在 jsEntries 中写 ["!js/utils.js"] 即可单独排除！',
    ],
    tipsEn: [
      'JS plugins: auto-scans js/**/*.js and js/**/*.mjs (js/main.js loads first).',
      'CSS styles: auto-scans BOTH styles/**/*.css and css/**/*.css (styles/mod.css loads first).',
      'Lua libraries: auto-scans lua/lib/**/*.lua and appends idempotently to utility.lua.',
      'Exclusions: prefix any entry with "!" (e.g. "jsEntries": ["!js/internal-helper.js"]) to exclude it while keeping auto-import on.',
    ],
    codeTitle: '自动导入 + 局部排除示例',
    code: `mods/my-mod/
├─ cover.svg                  # 自动识别封面
├─ mod-manifest.json          # autoDiscoverEntries 默认为 true
├─ settings.json              # 可选独立游戏内设置文件 (F9)
├─ js/
│  ├─ main.js                 # 自动导入 #1
│  ├─ hud-badge.js            # 自动导入 #2
│  └─ shared-utils.js         # 若写 "!js/shared-utils.js" 则仅作 import 依赖
├─ styles/theme.css           # 自动导入 CSS #1
├─ css/accent.css             # 自动导入 CSS #2 (同时支持 styles/ 和 css/)
└─ lua/
   ├─ lib/my_helpers.lua      # 自动并入全局 utility.lua
   └─ cards/c190088001.lua    # 按卡号自动匹配卡牌效果`,
  },
  {
    key: 'settings',
    labelZh: '游戏内 MOD 设置 (F9)',
    labelEn: 'In-Game MOD Settings (F9)',
    icon: <Sliders className="size-3.5" />,
    summaryZh:
      'MOD 可通过声明式 settingsSchema（写在 mod-manifest.json 或独立 settings.json）或 JS 动态注册，自动在游戏内 F9 →「MOD 设置」渲染原生交互控件（难度、倍速、倍率、颜色、开关），修改后即时生效并自动持久化！',
    summaryEn:
      'Declare settingsSchema in mod-manifest.json (or settings.json) or call ctx.settings.define() in JS. Controls auto-render in-game under F9 -> MOD Settings with live onChange & localStorage persistence.',
    tipsZh: [
      '支持 6 种控件类型：boolean (开关)、range (带单位滑动条)、number (数字输入)、select (下拉选项)、text (文本框)、color (取色器)。',
      '支持中英双语字段：label / zhLabel、description / zhDescription、options[].zhLabel。',
      'JS 中用 ctx.settings.get(key, fallback) 读取、ctx.settings.onChange((k, v, all) => ...) 实时响应（无需刷新页面！），ctx.settings.open() 可一键呼出设置面板。',
    ],
    tipsEn: [
      '6 built-in control types: boolean, range (with min/max/step/unit), number, select, text, color.',
      'Bilingual support via label/zhLabel and description/zhDescription.',
      'JS API: ctx.settings.get(key, def), .set(key, val), .getAll(), .reset(), .onChange(fn), .define(schema), .open().',
    ],
    codeTitle: 'settings.json + js/main.js 实时联动示例',
    code: `// 1) settings.json (或 mod-manifest.json -> "settingsSchema")
[
  {
    "key": "difficulty", "type": "select",
    "label": "Difficulty", "zhLabel": "游戏难度", "default": "standard",
    "options": [
      { "value": "casual", "label": "Casual", "zhLabel": "休闲 (0.75x)" },
      { "value": "standard", "label": "Standard", "zhLabel": "标准 (1.0x)" },
      { "value": "hard", "label": "Hard", "zhLabel": "困难 (1.5x)" }
    ]
  },
  {
    "key": "speed", "type": "range",
    "label": "Animation Speed", "zhLabel": "动画倍速",
    "default": 1.5, "min": 0.5, "max": 3.0, "step": 0.25, "unit": "x"
  }
]

// 2) js/main.js
export async function init(ctx) {
  const apply = () => {
    const speed = ctx.settings.get('speed', 1.5);
    ctx.addStyle(\`#app * { transition-duration: calc(0.18s / \${speed}) !important; }\`, 'spd');
  };
  apply();
  ctx.settings.onChange(() => apply());
}`,
  },
  {
    key: 'cards',
    labelZh: '卡牌与 ocgcore Lua',
    labelEn: 'Cards & ocgcore Lua',
    icon: <Sparkles className="size-3.5" />,
    summaryZh:
      '自定义卡牌推荐使用 ID 段 190010000 ~ 199999999，自动同步至 content.json、engine-data.json、era-cards.json 与中文图鉴 i18n/zh.json。',
    summaryEn:
      'Use custom card IDs 190010000–199999999. Cards auto-sync to content.json, engine-data.json, era-cards.json, and i18n/zh.json.',
    tipsZh: [
      '常用 data.type 位掩码：通常怪兽=17、效果怪兽=33、融合效果=97、通常魔法=2、速攻魔法=65538、永续魔法=131074、场地魔法=524290、通常陷阱=4、反击陷阱=1048580。',
      '常用属性 data.attribute：地=1、水=2、炎=4、风=8、光=16、暗=32、神=64；魔法/陷阱填 0。',
      '脚本文件放 lua/cards/c<ID>.lua，支持标准 EDOPro 3 返回值 local s, id, o = GetID()。',
    ],
    tipsEn: [
      'Common data.type masks: Normal Monster=17, Effect Monster=33, Fusion Effect=97, Normal Spell=2, Quick-Play=65538, Field=524290, Normal Trap=4, Counter Trap=1048580.',
      'Attributes: EARTH=1, WATER=2, FIRE=4, WIND=8, LIGHT=16, DARK=32, DIVINE=64 (0 for Spell/Trap).',
      'Card scripts live in lua/cards/c<id>.lua and support 3-return local s, id, o = GetID().',
    ],
    codeTitle: 'lua/cards/c190088001.lua',
    code: `local s, id, o = GetID()
function s.initial_effect(c)
    local e1 = Effect.CreateEffect(c)
    e1:SetCategory(CATEGORY_DRAW + CATEGORY_RECOVER)
    e1:SetType(EFFECT_TYPE_ACTIVATE)
    e1:SetCode(EVENT_FREE_CHAIN)
    e1:SetCountLimit(1, id, EFFECT_COUNT_CODE_OATH)
    e1:SetTarget(s.target)
    e1:SetOperation(s.activate)
    c:RegisterEffect(e1)
end
function s.target(e, tp, eg, ep, ev, re, r, rp, chk)
    if chk == 0 then return Duel.IsPlayerCanDraw(tp, 2) end
    Duel.SetOperationInfo(0, CATEGORY_DRAW, nil, 0, tp, 2)
end
function s.activate(e, tp, eg, ep, ev, re, r, rp)
    Duel.Draw(tp, 2, REASON_EFFECT)
    Duel.Recover(tp, 600, REASON_EFFECT)
end`,
  },
  {
    key: 'content',
    labelZh: '角色 · 遗物 · 卡包 · 修改器',
    labelEn: 'Characters · Relics · Packs',
    icon: <Wand2 className="size-3.5" />,
    summaryZh:
      '角色、遗物、卡包、预组卡组和修改器均可直接在「可视化 MOD 创作工作台」表单编辑，或用 JSON 文件声明式编写。',
    summaryEn:
      'Characters, relics, booster packs, preconstructed decks, and trainers can be authored visually in MOD Studio or via declarative JSON files.',
    tipsZh: [
      'characters/*.json：省略 index 可新增可玩角色，填写 index (如 0=暗游戏) 则覆盖原版角色；cards 数组填英文卡名！',
      'relics/*.json：支持自定义 art/ 本地图片（自动内联 data URI）与 luaEffect 决斗开始注入效果。',
      'decks/*.ydk：直接放入标准 YGOPro .ydk 文件即可自动转为游戏内预组卡组。',
      'trainer.json：声明式开启 F8 游戏内修改器与开局金币/LP/免费商店。',
    ],
    tipsEn: [
      'characters/*.json: omit index to add a new duelist or pass index to edit an existing one; cards[] uses exact English card names.',
      'relics/*.json: supports stock or local art/ images (auto-inlined) and luaEffect duel hooks.',
      'decks/*.ydk: drop standard YGOPro .ydk files to auto-register preconstructed decks.',
      'trainer.json: enables F8 live in-game trainer & campaign balance overrides.',
    ],
    codeTitle: 'relics/my-relic.json',
    code: `[
  {
    "key": "chrono_hourglass",
    "name": "Chrono Hourglass",
    "zhName": "时空沙漏",
    "cost": 85,
    "tag": "offense",
    "description": "Spellcaster monsters you control gain 300 ATK.",
    "zhDescription": "自己场上的魔法师族怪兽攻击力上升300。",
    "art": "Merchant Urn.png",
    "luaEffect": "do local e=Effect.GlobalEffect() e:SetType(EFFECT_TYPE_FIELD) e:SetCode(EFFECT_UPDATE_ATTACK) e:SetTargetRange(LOCATION_MZONE,0) e:SetTarget(function(e,c) return c:IsRace(RACE_SPELLCASTER) end) e:SetValue(300) Duel.RegisterEffect(e,0) end"
  }
]`,
  },
  {
    key: 'js_mcp',
    labelZh: 'JS API · Xmake DSL · 内置 MCP',
    labelEn: 'JS API · Xmake DSL · MCP',
    icon: <Code2 className="size-3.5" />,
    summaryZh:
      '支持在游戏内按 F8（修改器）/ F9（MOD 列表与设置）/ F10（开发者与调试），也支持 Xmake 风格单文件 mod.lua/mod.js，并内置 22 个 AI MCP 工具实时协同改脚本！',
    summaryEn:
      'In-game hotkeys: F8 Trainer, F9 Loaded MODs & Settings, F10 Dev/Debug. Also supports Xmake-style mod.lua/mod.js DSL and 22 built-in MCP tools for live AI scripting.',
    tipsZh: [
      'ctx.onFetch(regex, fn)：在游戏启动前挂载，可实时拦截并改写 content.json / i18n/zh.json 等任何请求。',
      'Xmake DSL：在 mod.lua 中支持 set_category / set_cover / set_auto_entries / card(...) / relic(...) / on_duel_script。',
      '内置 MCP：点击顶部或左侧「内置 MCP 与 AI 脚本」一键写入 .mcp.json，AI 可直接查阅原版卡脚本并自动热加载。',
    ],
    tipsEn: [
      'ctx.onFetch(regex, fn): runs before web-boot.js so you can transform content.json or i18n JSON live.',
      'Xmake DSL: mod.lua/mod.js supports set_category, set_cover, set_auto_entries, inline card/relic blocks & hooks.',
      'Built-in MCP: connect Cursor/Claude to /mcp — AI reads your focused script, studies vanilla Lua & triggers live Hot-Reload.',
    ],
    codeTitle: 'mod.lua (Xmake 双域 DSL 示例)',
    code: `set_mod("my-dsl-mod")
set_name("我的双域 DSL MOD")
set_category("script_plugin")
set_cover("cover.svg")
set_auto_entries(true)
set_min_gamever("0.2.46")

-- 也可继续混合目录资产
add_cards("cards/*.json")
add_js_files("js/*.js")
add_css_files("styles/*.css")`,
  },
];

export function ModGuideModal({
  isOpen,
  onOpenChange,
  t,
  lang,
  onCreateExample,
  onCopyAiPrompt,
}: ModGuideModalProps) {
  const [activeKey, setActiveKey] = useState<GuideTabKey>('layout');
  const [copied, setCopied] = useState(false);

  const isZh = lang === 'zh';
  const current = GUIDE_SECTIONS.find((s) => s.key === activeKey) ?? GUIDE_SECTIONS[0];

  const handleCopy = () => {
    navigator.clipboard.writeText(current.code).catch(() => {});
    setCopied(true);
    setTimeout(() => setCopied(false), 1400);
  };

  return (
    <Modal.Backdrop isOpen={isOpen} onOpenChange={onOpenChange}>
      <Modal.Container placement="center" size="lg">
        <Modal.Dialog className="max-h-[86vh] sm:max-w-[760px]">
          <Modal.CloseTrigger />
          <Modal.Header>
            <Modal.Icon className="bg-accent-soft text-accent-soft-foreground">
              <BookOpen className="size-5" />
            </Modal.Icon>
            <div className="min-w-0">
              <Modal.Heading className="text-base">{t.modGuideTitle}</Modal.Heading>
              <p className="mt-0.5 text-[11px] text-muted">{t.modGuideSubtitle}</p>
            </div>
          </Modal.Header>

          <Modal.Body className="space-y-3 overflow-y-auto">
            {/* Section Switcher Pills */}
            <div className="flex flex-wrap gap-1.5 border-b border-border/80 pb-2.5">
              {GUIDE_SECTIONS.map((sec) => {
                const active = sec.key === current.key;
                return (
                  <button
                    key={sec.key}
                    type="button"
                    onClick={() => setActiveKey(sec.key)}
                    className={`inline-flex items-center gap-1.5 rounded-lg border px-2.5 py-1 text-xs font-medium transition-colors ${
                      active
                        ? 'border-accent/70 bg-accent/15 text-accent'
                        : 'border-border/70 bg-surface-secondary/60 text-muted hover:border-border hover:text-foreground'
                    }`}
                  >
                    {sec.icon}
                    <span>{isZh ? sec.labelZh : sec.labelEn}</span>
                  </button>
                );
              })}
            </div>

            {/* Active Section Content */}
            <div className="space-y-3">
              <p className="text-xs leading-relaxed text-foreground/90">
                {isZh ? current.summaryZh : current.summaryEn}
              </p>

              <ul className="space-y-1.5 rounded-xl border border-border/75 bg-surface-secondary/50 p-3 text-xs text-foreground/85">
                {(isZh ? current.tipsZh : current.tipsEn).map((tip, i) => (
                  <li key={i} className="flex items-start gap-2 leading-relaxed">
                    <span className="mt-1.5 size-1.5 shrink-0 rounded-full bg-accent" />
                    <span>{tip}</span>
                  </li>
                ))}
              </ul>

              <div className="overflow-hidden rounded-xl border border-border/80 bg-[#0b0d12]">
                <div className="flex items-center justify-between border-b border-white/10 px-3 py-1.5">
                  <div className="flex items-center gap-2">
                    <Chip size="sm" variant="soft" color="accent" className="h-4">
                      <Chip.Label className="font-mono text-[10px]">{current.codeTitle}</Chip.Label>
                    </Chip>
                  </div>
                  <Button
                    size="sm"
                    variant="ghost"
                    onPress={handleCopy}
                    className="h-6 px-2 text-[11px] text-zinc-300"
                  >
                    {copied ? <Check className="size-3 text-success" /> : <Copy className="size-3" />}
                    {copied ? t.copiedSnippet : t.copySnippet}
                  </Button>
                </div>
                <pre className="max-h-60 overflow-auto p-3 font-mono text-[11px] leading-5 text-zinc-200">
                  <code>{current.code}</code>
                </pre>
              </div>
            </div>
          </Modal.Body>

          <Modal.Footer className="flex items-center justify-between gap-2">
            <div className="flex flex-wrap items-center gap-1.5">
              {onCreateExample && (
                <Button
                  size="sm"
                  variant="secondary"
                  onPress={() => {
                    onOpenChange(false);
                    onCreateExample();
                  }}
                >
                  <Sparkles className="size-3.5 text-warning" />
                  {t.createExampleMod}
                </Button>
              )}
              {onCopyAiPrompt && (
                <Button size="sm" variant="secondary" onPress={onCopyAiPrompt}>
                  <Bot className="size-3.5 text-accent" />
                  {t.copyAiSuperPromptBtn}
                </Button>
              )}
            </div>
            <Button variant="primary" slot="close">
              {t.cancel}
            </Button>
          </Modal.Footer>
        </Modal.Dialog>
      </Modal.Container>
    </Modal.Backdrop>
  );
}
