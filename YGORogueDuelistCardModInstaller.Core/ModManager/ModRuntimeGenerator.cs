using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

public sealed class ModRuntimeDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("jsEntries")]
    public List<string> JsEntries { get; set; } = [];

    [JsonPropertyName("cssEntries")]
    public List<string> CssEntries { get; set; } = [];

    [JsonPropertyName("config")]
    public JsonObject? Config { get; set; }

    [JsonPropertyName("settingsSchema")]
    public JsonArray? SettingsSchema { get; set; }
}

/// <summary>
/// Generates the CSP-safe in-game ES Module runtime (mod-runtime.js), mods-registry.json,
/// built-in F8 Trainer overlay (mod-trainer-overlay.js) + its native Python mobile_backend.py hook,
/// built-in F10 Developer Settings &amp; Debug Mode workbench (mod-dev-tools.js), and patches index.html.
/// 100% Native AOT compatible (source-generated JSON only).
/// </summary>
public static class ModRuntimeGenerator
{
    public const string RuntimeScriptPath = "mod-runtime.js";
    public const string RegistryJsonPath = "mods-registry.json";
    public const string TrainerOverlayScriptPath = "mod-trainer-overlay.js";
    public const string DevToolsScriptPath = "mod-dev-tools.js";
    public const string RuntimeVersion = "2.2.0";

    public static string BuildRegistryJson(string? baseGameVersion, List<ModRuntimeDescriptor> descriptors)
    {
        var payload = new ModRuntimeRegistryPayload
        {
            RuntimeVersion = RuntimeVersion,
            BaseGameVersion = baseGameVersion ?? "unknown",
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Mods = descriptors
        };
        return JsonSerializer.Serialize(payload, CoreJsonContext.PrettyUtf8.ModRuntimeRegistryPayload);
    }

    public static string PatchIndexHtml(string indexHtml)
    {
        const string runtimeTag = "<script type=\"module\" src=\"mod-runtime.js\"></script>";
        if (indexHtml.Contains("src=\"mod-runtime.js\"", StringComparison.OrdinalIgnoreCase))
        {
            return indexHtml;
        }

        const string bootAnchor = "<script type=\"module\" src=\"web-boot.js\"></script>";
        if (indexHtml.Contains(bootAnchor, StringComparison.OrdinalIgnoreCase))
        {
            return indexHtml.Replace(bootAnchor, runtimeTag + bootAnchor, StringComparison.OrdinalIgnoreCase);
        }

        const string bodyEnd = "</body>";
        if (indexHtml.Contains(bodyEnd, StringComparison.OrdinalIgnoreCase))
        {
            return indexHtml.Replace(bodyEnd, runtimeTag + bodyEnd, StringComparison.OrdinalIgnoreCase);
        }

        return indexHtml + runtimeTag;
    }

    /// <summary>
    /// Appended to campaign-files.json["mobile_backend.py"] when Developer Mode or Built-in Trainer is active.
    /// Adds the live 'mod-trainer' action to mobile_backend.dispatch(raw) so every Trainer action executes
    /// directly inside the game's Python engine, signs saves with genuine SHA-256 envelopes via storage.py,
    /// and returns full live state() for instant zero-reload UI updates on both Web (IndexedDB) and Desktop!
    /// </summary>
    public static string BuildTrainerBackendPythonHook() => """
        # mod-trainer-backend:v2
        _mod_trainer_orig_dispatch = dispatch
        def _mod_trainer_handle(val):
         global run
         op = str((val or {}).get('op') or '')
         res = {'op': op}
         if op == 'unlock-everything':
          import relic_fusions
          p = storage.profile()
          p.update(
           unlock_all=True,
           unlocked=list(content.PLAYABLE_IDS),
           variants_unlocked=True,
           relicless_unlocked=True,
           gallery_unlocked=True,
           cpu_viewer_unlocked=True,
           sleeves_all=True,
           testing_all_clears=True,
           wins=max(int(p.get('wins') or 0), 1),
           character_levels={str(i): 6 for i in content.PLAYABLE_IDS},
           fused_relics=sorted(set(p.get('fused_relics', [])) | set(relic_fusions.KEYS))
          )
          storage.write(storage.ROOT/'profile.json', p)
          all_ids = sorted(c['id'] for c in g.CARDS if isinstance(c.get('id'), int) and 0 < c['id'] < 191000000)
          storage.write(storage.ROOT/'registered-cards.json', {'cards': all_ids})
          res['unlocked'] = len(p['unlocked'])
          res['cards'] = len(all_ids)
          return res
         if op == 'unlock-flag':
          flag = str(val.get('flag') or '')
          p = storage.profile()
          if flag == 'all_chars':
           p['unlock_all'] = True
           p['unlocked'] = list(content.PLAYABLE_IDS)
          elif flag == 'all_variants':
           p['variants_unlocked'] = True
          elif flag == 'all_levels':
           p['testing_all_clears'] = True
           p['character_levels'] = {str(i): 6 for i in content.PLAYABLE_IDS}
          elif flag == 'all_sleeves':
           p['sleeves_all'] = True
          elif flag == 'all_fused':
           import relic_fusions
           p['fused_relics'] = sorted(set(p.get('fused_relics', [])) | set(relic_fusions.KEYS))
          elif flag == 'relicless':
           p['relicless_unlocked'] = True
          elif flag == 'gallery_cpu_sandbox':
           p['gallery_unlocked'] = True
           p['cpu_viewer_unlocked'] = True
           p['wins'] = max(int(p.get('wins') or 0), 1)
          elif flag == 'all_collection':
           all_ids = sorted(c['id'] for c in g.CARDS if isinstance(c.get('id'), int) and 0 < c['id'] < 191000000)
           storage.write(storage.ROOT/'registered-cards.json', {'cards': all_ids})
           res['cards'] = len(all_ids)
           return res
          else:
           raise ValueError('Unknown unlock flag: ' + flag)
          storage.write(storage.ROOT/'profile.json', p)
          return res
         if op == 'set-char-level':
          import challenge_levels
          cid = int(val.get('character', 0))
          lvl = max(-1, min(6, int(val.get('level', 6))))
          p = storage.profile()
          p.setdefault('character_levels', {})[str(cid)] = lvl
          if cid in content.PLAYABLE_IDS:
           p['unlocked'] = sorted(set(p.get('unlocked', [0, 1])) | {cid})
          challenge_levels.sync_engine_unlock(p)
          storage.write(storage.ROOT/'profile.json', p)
          return res
         if not run:
          raise ValueError('No active run. Start a run first.')
         if op == 'set-gold':
          run['gold'] = max(0, int(val.get('amount', 0)))
         elif op == 'add-gold':
          run['gold'] = max(0, int(run.get('gold', 0)) + int(val.get('delta', 0)))
         elif op == 'set-lp':
          run['lp'] = max(1, int(val.get('amount', 8000)))
         elif op == 'add-card':
          cid = int(val.get('id', 0))
          if cid not in g.BY_ID:
           raise ValueError(f'Unknown card ID: {cid}')
          count = max(1, min(9, int(val.get('count', 1))))
          to_deck = bool(val.get('toDeck', False))
          pool = run.setdefault('pool', [])
          sel = run.setdefault('selected', [])
          for _ in range(count):
           idx = len(pool)
           pool.append(cid)
           if to_deck and idx not in sel:
            sel.append(idx)
          storage.register_collection(run)
          res['id'] = cid
          res['count'] = count
         elif op == 'add-relic':
          import debug_codes
          res.update(debug_codes.give_relic(run, str(val.get('key') or '')))
         elif op == 'remove-relic':
          key = str(val.get('key') or '')
          if key in run.get('artifacts', []):
           run['artifacts'] = [k for k in run['artifacts'] if k != key]
          if key in run.get('cursed_artifacts', []):
           run['cursed_artifacts'] = [k for k in run['cursed_artifacts'] if k != key]
          res['key'] = key
         elif op == 'add-pack':
          pid = str(val.get('id') or '').strip()
          count = max(1, min(20, int(val.get('count', 1))))
          if not pid:
           raise ValueError('Pack ID required.')
          packs_opened = []
          for _ in range(count):
           cards = g.open_pack(pid)
           packs_opened.append(dict(id=pid, cards=cards))
           run.setdefault('pool', []).extend(cards)
          run.setdefault('free_packs', []).extend(packs_opened)
          storage.register_collection(run)
          res['id'] = pid
          res['count'] = count
         elif op == 'free-shop':
          if bool(val.get('restock', False)):
           g.restock(run, general_stock=True)
          cnt = 0
          for it in run.get('shop', []):
           if isinstance(it, dict) and 'price' in it:
            it['price'] = 0
            if bool(val.get('unsold', False)):
             it['sold'] = False
            cnt += 1
          res['count'] = cnt
         elif op == 'restock-shop':
          g.restock(run, general_stock=True)
          res['count'] = len(run.get('shop', []))
         elif op == 'clear-curses':
          run['curses'] = []
          run['boss_curse'] = None
          run['cursed_artifacts'] = []
         elif op == 'set-progress':
          if 'round' in val and val['round'] is not None:
           run['round'] = max(0, min(8, int(val['round'])))
          if 'loop' in val and val['loop'] is not None:
           run['loop'] = max(0, int(val['loop']))
         else:
          raise ValueError('Unknown mod-trainer op: ' + op)
         g.save(run)
         return res

        def dispatch(raw):
         try:
          req = json.loads(raw)
         except Exception:
          return _mod_trainer_orig_dispatch(raw)
         if isinstance(req, dict) and req.get('action') == 'mod-trainer':
          res = _mod_trainer_handle(req.get('value') or {})
          return json.dumps(dict(**state(), result=res), ensure_ascii=False)
         return _mod_trainer_orig_dispatch(raw)
        """;

    public static string BuildDevToolsScript() => """
        // Built-in Developer Settings & Debug Mode Workbench (mod-dev-tools.js)
        // Exposes window.YgoModDev, F10 Developer Settings & Debug Console, FPS HUD, and floating quick bar.
        export function init(ctx) {
          window.__YGO_MOD_DEV__ = true;
          const SETTINGS_KEY = 'ygomod:dev-settings';
          const DEFAULT_SETTINGS = {
            debugMode: false,
            traceFetch: true,
            traceConsole: true,
            showQuickBar: true,
            showStartupToast: true,
            fpsOverlay: false
          };

          function loadSettings() {
            try {
              const raw = localStorage.getItem(SETTINGS_KEY);
              if (!raw) return { ...DEFAULT_SETTINGS };
              const parsed = JSON.parse(raw);
              return { ...DEFAULT_SETTINGS, ...(parsed && typeof parsed === 'object' ? parsed : {}) };
            } catch {
              return { ...DEFAULT_SETTINGS };
            }
          }

          let settings = loadSettings();
          function persistSettings() {
            try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings)); } catch {}
          }

          let lastContent = null;
          let lastZhMap = null;
          const logs = [];
          const fetches = [];
          const evalHistory = [];
          let evalHistIdx = -1;
          let errorCount = 0;
          let activeTab = 'overview';
          let logFilter = 'all';
          let logSearch = '';
          let stateSubTab = 'live';
          let contentSearch = '';
          let consoleHooked = false;
          let origConsole = null;
          let fpsEl = null;
          let fpsRaf = 0;

          const MAX_LOGS = 300;
          const MAX_FETCHES = 200;
          const t = (key, params) => ctx.api.i18n ? ctx.api.i18n.t(key, params) : key;
          const esc = (v) => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
          const nowStr = () => {
            const d = new Date();
            return d.toTimeString().slice(0, 8) + '.' + String(d.getMilliseconds()).padStart(3, '0');
          };

          function safeFormatArg(arg) {
            if (typeof arg === 'string') return arg;
            if (arg instanceof Error) return arg.stack || arg.message || String(arg);
            try {
              const s = JSON.stringify(arg);
              return s && s.length > 600 ? s.slice(0, 600) + '…' : (s ?? String(arg));
            } catch {
              return String(arg);
            }
          }

          function pushLog(level, source, text) {
            logs.push({ time: nowStr(), level, source, text: String(text) });
            if (logs.length > MAX_LOGS) logs.shift();
            if (level === 'error') {
              errorCount++;
              refreshQuickBar();
            }
            refreshInspectorIfOpen('debug');
          }

          function ensureConsoleHooks() {
            if (consoleHooked) return;
            consoleHooked = true;
            origConsole = {
              log: console.log.bind(console),
              info: console.info.bind(console),
              warn: console.warn.bind(console),
              error: console.error.bind(console)
            };
            for (const level of ['log', 'info', 'warn', 'error']) {
              console[level] = (...args) => {
                origConsole[level](...args);
                if (settings.debugMode && settings.traceConsole) {
                  pushLog(level, 'console', args.map(safeFormatArg).join(' '));
                }
              };
            }
            window.addEventListener('error', (ev) => {
              if (!settings.debugMode) return;
              const loc = ev.filename ? ` (${ev.filename}:${ev.lineno}:${ev.colno})` : '';
              pushLog('error', 'window.error', `${ev.message || 'Script error'}${loc}`);
            });
            window.addEventListener('unhandledrejection', (ev) => {
              if (!settings.debugMode) return;
              pushLog('error', 'promise', safeFormatArg(ev.reason));
            });
          }

          ensureConsoleHooks();

          ctx.onFetch(/(?:^|\/)content\.json(?:\?|$)/, async (resp) => {
            try { lastContent = await resp.clone().json(); } catch {}
            return resp;
          });
          ctx.onFetch(/(?:^|\/)i18n\/zh\.json(?:\?|$)/, async (resp) => {
            try { lastZhMap = await resp.clone().json(); } catch {}
            return resp;
          });

          ctx.api._telemetry?.onFetchTrace((entry) => {
            if (!settings.debugMode || !settings.traceFetch) return;
            fetches.push(entry);
            if (fetches.length > MAX_FETCHES) fetches.shift();
            pushLog(entry.ok ? 'fetch' : 'error', 'fetch', `${entry.method} ${entry.url} -> ${entry.status} (${entry.ms}ms)`);
          });

          ctx.api._telemetry?.onEventTrace((entry) => {
            if (!settings.debugMode) return;
            pushLog('event', 'event', `${entry.event} ${safeFormatArg(entry.detail)}`);
          });

          function applyDebugAttr() {
            try {
              document.documentElement.dataset.ygoDebug = settings.debugMode ? 'on' : 'off';
            } catch {}
          }

          function syncFpsHud() {
            if (settings.fpsOverlay) {
              if (fpsEl) return;
              const mount = () => {
                if (fpsEl || !settings.fpsOverlay) return;
                fpsEl = document.createElement('div');
                fpsEl.id = 'ygo-dev-fps-hud';
                const topOffset = document.getElementById('ygo-hot-reload-badge') ? '46px' : '10px';
                fpsEl.style.cssText = [
                  'position:fixed', `top:${topOffset}`, 'right:10px', 'z-index:2147483636',
                  'padding:5px 10px', 'border-radius:8px',
                  'background:rgba(12,14,19,0.88)', 'color:#e5e7eb',
                  'border:1px solid rgba(56,189,248,0.45)',
                  'font:600 11px/1.35 ui-monospace,SFMono-Regular,Consolas,monospace',
                  'pointer-events:none', 'backdrop-filter:blur(8px)',
                  'box-shadow:0 6px 20px rgba(0,0,0,0.45)'
                ].join(';');
                document.body.appendChild(fpsEl);
                let frames = 0;
                let last = performance.now();
                const tick = (now) => {
                  if (!fpsEl) return;
                  frames++;
                  if (now - last >= 500) {
                    const fps = Math.round((frames * 1000) / (now - last));
                    frames = 0;
                    last = now;
                    const mem = performance.memory
                      ? ` · ${Math.round(performance.memory.usedJSHeapSize / 1048576)}MB`
                      : '';
                    const domN = document.getElementsByTagName('*').length;
                    const dbg = settings.debugMode ? ' · DBG' : '';
                    fpsEl.textContent = `${fps} FPS${mem} · DOM ${domN}${dbg}`;
                    fpsEl.style.borderColor = fps >= 50 ? 'rgba(34,197,94,0.5)' : (fps >= 30 ? 'rgba(250,204,21,0.55)' : 'rgba(248,113,113,0.65)');
                  }
                  fpsRaf = requestAnimationFrame(tick);
                };
                fpsRaf = requestAnimationFrame(tick);
              };
              ctx.onDomReady(mount);
            } else {
              if (fpsRaf) { cancelAnimationFrame(fpsRaf); fpsRaf = 0; }
              if (fpsEl) { fpsEl.remove(); fpsEl = null; }
            }
          }

          function setSetting(key, value) {
            if (!(key in DEFAULT_SETTINGS)) return;
            settings[key] = Boolean(value);
            persistSettings();
            applyDebugAttr();
            syncFpsHud();
            refreshQuickBar();
            if (key === 'debugMode') {
              ctx.showToast(settings.debugMode ? t('debugEnabledToast') : t('debugDisabledToast'), 2200);
              if (settings.debugMode) {
                pushLog('info', 'dev', t('debugStartedLog'));
              }
            }
            ctx.events.emit('dev:settings', { ...settings });
            refreshInspectorIfOpen();
          }

          async function clearCacheAndReload() {
            try {
              if ('caches' in window) {
                const keys = await caches.keys();
                await Promise.all(keys.map(k => caches.delete(k)));
              }
            } catch {}
            location.reload();
          }

          const devApi = Object.freeze({
            getMods: () => ctx.api.mods,
            getStatuses: () => ctx.api.statuses,
            getContent: () => lastContent,
            getZhMap: () => lastZhMap,
            getLiveState: () => window.shadowTest?.state?.() || null,
            getSettings: () => ({ ...settings }),
            setSetting,
            setDebugMode: (on) => setSetting('debugMode', !!on),
            getLogs: () => logs.slice(),
            getFetches: () => fetches.slice(),
            clearLogs: () => { logs.length = 0; fetches.length = 0; errorCount = 0; refreshQuickBar(); refreshInspectorIfOpen('debug'); },
            openInspector: () => openInspector(),
            openTrainer: () => ctx.api.openTrainer?.(),
            openModsOverlay: () => ctx.api.openOverlay(),
            reload: clearCacheAndReload,
            summary() {
              const live = window.shadowTest?.state?.();
              return {
                runtimeVersion: ctx.api.runtimeVersion,
                baseGameVersion: ctx.api.baseGameVersion,
                debugMode: settings.debugMode,
                language: ctx.api.i18n?.getLang?.() || 'zh',
                screen: window.shadowTest?.screen?.() || 'booting',
                storageBackend: window.ShadowNative ? 'ShadowNative (Desktop)' : 'IndexedDB (ygo-rogue-saves)',
                loadedMods: ctx.api.mods.length,
                failedMods: ctx.api.statuses.filter(s => !s.ok).length,
                cards: lastContent?.cards?.length ?? 0,
                characters: lastContent?.characters?.length ?? 0,
                playable: lastContent?.playable?.length ?? 0,
                packs: lastContent?.packs?.length ?? 0,
                preconstructedDecks: lastContent?.preconstructedDecks?.length ?? 0,
                relics: lastContent?.artifacts ? Object.keys(lastContent.artifacts).length : 0,
                unlockedChars: live?.profile?.unlocked?.length ?? 0,
                activeRunGold: live?.run?.gold ?? null,
                capturedLogs: logs.length,
                capturedFetches: fetches.length,
                capturedErrors: errorCount
              };
            }
          });
          window.YgoModDev = devApi;
          ctx.api.openDevTools = openInspector;

          let inspectorRoot = null;
          let inspectorHost = null;

          function refreshInspectorIfOpen(onlyTab) {
            if (!inspectorHost || !inspectorHost.isConnected) return;
            if (onlyTab && activeTab !== onlyTab) return;
            renderInspectorBody();
          }

          function renderInspectorBody() {
            if (!inspectorRoot) return;
            const s = devApi.summary();
            const lang = ctx.api.i18n?.getLang?.() || 'zh';

            const tabBtn = (id, label) =>
              `<button class="tab ${activeTab === id ? 'active' : ''}" data-tab="${id}">${esc(label)}</button>`;

            let bodyHtml = '';
            if (activeTab === 'overview') {
              const statCard = (label, val, cls = '') =>
                `<div class="stat"><div class="stat-k">${esc(label)}</div><div class="stat-v ${cls}">${esc(val)}</div></div>`;
              bodyHtml = `
                <div class="grid4">
                  ${statCard(t('devStatRuntime'), 'v' + s.runtimeVersion, 'accent')}
                  ${statCard(t('devStatGameVer'), s.baseGameVersion)}
                  ${statCard(t('devStatMods'), `${s.loadedMods}${s.failedMods ? ` (${s.failedMods}!)` : ''}`, s.failedMods ? 'err' : 'ok')}
                  ${statCard(t('devStatDebug'), settings.debugMode ? t('statusOn') : t('statusOff'), settings.debugMode ? 'ok' : 'muted')}
                  ${statCard(t('devStatCards'), s.cards)}
                  ${statCard(t('devStatChars'), `${s.playable} / ${s.characters}`)}
                  ${statCard(t('devStatRelics'), s.relics)}
                  ${statCard(t('devStatPacksDecks'), `${s.packs} / ${s.preconstructedDecks}`)}
                </div>
                <div class="sec-title">${esc(t('devQuickActions'))}</div>
                <div class="actions-wrap">
                  <button class="btn primary" id="actToggleDebug">${esc(settings.debugMode ? t('devBtnDisableDebug') : t('devBtnEnableDebug'))}</button>
                  ${ctx.api.openTrainer ? `<button class="btn warn" id="actTrainer">${esc(t('devBtnOpenTrainer'))}</button>` : ''}
                  <button class="btn" id="actMods">${esc(t('devBtnOpenMods'))}</button>
                  <button class="btn" id="actCopyDiag">${esc(t('devBtnCopyDiag'))}</button>
                  <button class="btn ok" id="actReload">${esc(t('devBtnClearReload'))}</button>
                </div>
                <div class="sec-title">${esc(t('devRuntimeJson'))}</div>
                <pre class="code">${esc(JSON.stringify(s, null, 2))}</pre>`;
            } else if (activeTab === 'settings') {
              const toggleRow = (key, title, desc) => `
                <div class="set-row">
                  <div>
                    <div class="set-title">${esc(title)}</div>
                    <div class="set-desc">${esc(desc)}</div>
                  </div>
                  <label class="switch">
                    <input type="checkbox" data-setting="${key}" ${settings[key] ? 'checked' : ''}>
                    <span class="slider"></span>
                  </label>
                </div>`;
              bodyHtml = `
                <div class="set-list">
                  ${toggleRow('debugMode', t('setDebugMode'), t('setDebugModeDesc'))}
                  ${toggleRow('traceConsole', t('setTraceConsole'), t('setTraceConsoleDesc'))}
                  ${toggleRow('traceFetch', t('setTraceFetch'), t('setTraceFetchDesc'))}
                  ${toggleRow('fpsOverlay', t('setFpsOverlay'), t('setFpsOverlayDesc'))}
                  ${toggleRow('showQuickBar', t('setQuickBar'), t('setQuickBarDesc'))}
                  ${toggleRow('showStartupToast', t('setStartupToast'), t('setStartupToastDesc'))}
                  <div class="set-row">
                    <div>
                      <div class="set-title">${esc(t('setLangTitle'))}</div>
                      <div class="set-desc">${esc(t('setLangDesc'))}</div>
                    </div>
                    <div class="row-gap">
                      ${(ctx.api.i18n?.listLocales?.() || [{ id: 'zh', name: '中文' }, { id: 'en', name: 'EN' }]).map(loc =>
                        `<button class="btn sm ${lang === loc.id ? 'primary' : ''}" data-set-lang="${esc(loc.id)}">${esc(loc.name)}</button>`
                      ).join('')}
                    </div>
                  </div>
                </div>
                <div class="foot-actions">
                  <button class="btn danger sm" id="actResetSettings">${esc(t('devBtnResetSettings'))}</button>
                </div>`;
            } else if (activeTab === 'debug') {
              const q = logSearch.trim().toLowerCase();
              const filtered = logs.filter(l => {
                if (logFilter !== 'all' && l.level !== logFilter) return false;
                if (q && !l.text.toLowerCase().includes(q) && !l.source.toLowerCase().includes(q)) return false;
                return true;
              });
              const chip = (id, label) =>
                `<button class="chip ${logFilter === id ? 'active' : ''}" data-lfilter="${id}">${esc(label)}</button>`;
              const rowsHtml = filtered.length
                ? filtered.slice(-180).map(l =>
                    `<div class="log-line lv-${esc(l.level)}"><span class="lt">${esc(l.time)}</span><span class="ls">[${esc(l.source)}]</span><span class="lm">${esc(l.text)}</span></div>`
                  ).join('')
                : `<div class="empty">${esc(settings.debugMode ? t('debugNoLogs') : t('debugOffHint'))}</div>`;

              bodyHtml = `
                ${!settings.debugMode ? `
                  <div class="banner-warn">
                    <span>${esc(t('debugOffBanner'))}</span>
                    <button class="btn sm primary" id="actQuickEnableDebug">${esc(t('devBtnEnableDebug'))}</button>
                  </div>` : ''}
                <div class="toolbar">
                  <div class="row-gap">
                    ${chip('all', `${t('filterAll')} (${logs.length})`)}
                    ${chip('error', `${t('filterError')} (${logs.filter(x => x.level === 'error').length})`)}
                    ${chip('warn', t('filterWarn'))}
                    ${chip('fetch', `${t('filterFetch')} (${fetches.length})`)}
                    ${chip('event', t('filterEvent'))}
                  </div>
                  <div class="row-gap">
                    <input class="inp sm" id="logSearchInp" value="${esc(logSearch)}" placeholder="${esc(t('debugSearchPh'))}">
                    <button class="btn sm" id="actCopyLogs">${esc(t('btnCopy'))}</button>
                    <button class="btn sm danger" id="actClearLogs">${esc(t('btnClear'))}</button>
                  </div>
                </div>
                <div class="log-box" id="devLogBox">${rowsHtml}</div>
                <div class="eval-row">
                  <span class="prompt">&gt;</span>
                  <input class="inp" id="evalInp" placeholder="${esc(t('debugEvalPh'))}">
                  <button class="btn primary sm" id="actEval">${esc(t('btnEval'))}</button>
                </div>`;
            } else {
              const subBtn = (id, label) =>
                `<button class="chip ${stateSubTab === id ? 'active' : ''}" data-sub="${id}">${esc(label)}</button>`;
              bodyHtml = `
                <div class="toolbar">
                  <div class="row-gap">
                    ${subBtn('live', t('stateSubLive'))}
                    ${subBtn('save', t('stateSubSave'))}
                    ${subBtn('content', t('stateSubContent'))}
                    ${subBtn('mods', t('stateSubMods'))}
                    ${subBtn('storage', 'localStorage')}
                  </div>
                  <div class="row-gap">
                    ${stateSubTab === 'content' ? `<input class="inp sm" id="contentSearchInp" value="${esc(contentSearch)}" placeholder="${esc(t('stateSearchContentPh'))}">` : ''}
                    <button class="btn sm" id="actRefreshState">${esc(t('btnRefresh'))}</button>
                    <button class="btn sm" id="actCopyState">${esc(t('btnCopy'))}</button>
                  </div>
                </div>
                <pre class="code tall" id="stateDumpBox">${esc(t('loading'))}</pre>`;
            }

            inspectorRoot.innerHTML = `<style>
              *{box-sizing:border-box}
              .bg{position:fixed;inset:0;z-index:2147483644;background:rgba(5,7,11,0.78);backdrop-filter:blur(4px);display:flex;align-items:center;justify-content:center;font:13px/1.45 "Microsoft YaHei UI","Segoe UI",system-ui,sans-serif;color:#e5e7eb}
              .panel{width:min(820px,95vw);height:min(620px,88vh);display:flex;flex-direction:column;background:#0c0e13;border:1px solid #262b36;border-radius:14px;box-shadow:0 24px 64px rgba(0,0,0,0.7);overflow:hidden}
              .head{display:flex;align-items:center;justify-content:space-between;gap:10px;padding:12px 16px;background:#11141c;border-bottom:1px solid #232834}
              .title-wrap{display:flex;align-items:center;gap:8px;min-width:0}
              h3{margin:0;font-size:14px;font-weight:700;color:#f3f4f6;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
              .pill{font-size:11px;font-weight:700;padding:2px 8px;border-radius:999px;border:1px solid transparent;cursor:pointer}
              .pill.on{background:rgba(34,197,94,0.16);color:#4ade80;border-color:rgba(34,197,94,0.4)}
              .pill.off{background:rgba(148,163,184,0.12);color:#94a3b8;border-color:#334155}
              .tabs{display:flex;gap:4px;padding:8px 16px 0;background:#11141c;border-bottom:1px solid #232834}
              .tab{background:transparent;border:none;border-bottom:2px solid transparent;color:#9ca3af;padding:7px 12px;font-size:12.5px;font-weight:600;cursor:pointer;border-radius:6px 6px 0 0}
              .tab:hover{color:#e5e7eb;background:rgba(255,255,255,0.04)}
              .tab.active{color:#60a5fa;border-bottom-color:#60a5fa;background:rgba(96,165,250,0.08)}
              .body{flex:1;min-height:0;overflow:auto;padding:14px 16px;display:flex;flex-direction:column;gap:10px}
              .grid4{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:8px}
              @media(max-width:640px){.grid4{grid-template-columns:repeat(2,minmax(0,1fr))}}
              .stat{background:#131722;border:1px solid #222733;border-radius:10px;padding:9px 11px}
              .stat-k{font-size:11px;color:#9ca3af;margin-bottom:3px}
              .stat-v{font-size:15px;font-weight:700;font-family:ui-monospace,Consolas,monospace;color:#f3f4f6}
              .stat-v.accent{color:#60a5fa}.stat-v.ok{color:#4ade80}.stat-v.err{color:#f87171}.stat-v.muted{color:#9ca3af}
              .sec-title{font-size:11.5px;font-weight:700;color:#9ca3af;text-transform:uppercase;letter-spacing:0.04em;margin-top:4px}
              .actions-wrap{display:flex;flex-wrap:wrap;gap:8px}
              .row-gap{display:flex;align-items:center;gap:6px;flex-wrap:wrap}
              .btn{background:#1a1f2c;border:1px solid #2d3446;color:#e5e7eb;border-radius:8px;padding:6px 11px;font-size:12px;font-weight:600;cursor:pointer}
              .btn:hover{background:#232a3b;border-color:#3e475e}
              .btn.sm{padding:4px 9px;font-size:11.5px;border-radius:6px}
              .btn.primary{background:#2563eb;border-color:#3b82f6;color:#fff}
              .btn.primary:hover{background:#1d4ed8}
              .btn.ok{background:#15803d;border-color:#22c55e;color:#fff}
              .btn.warn{background:rgba(217,119,6,0.2);border-color:rgba(245,158,11,0.5);color:#fcd34d}
              .btn.danger{background:rgba(220,38,38,0.18);border-color:rgba(248,113,113,0.45);color:#fca5a5}
              .code{margin:0;background:#080a0e;border:1px solid #1e2330;border-radius:10px;padding:10px 12px;font:12px/1.45 ui-monospace,Consolas,monospace;color:#93c5fd;overflow:auto;max-height:220px;white-space:pre-wrap;word-break:break-word}
              .code.tall{flex:1;max-height:none}
              .set-list{display:flex;flex-direction:column;gap:8px}
              .set-row{display:flex;align-items:center;justify-content:space-between;gap:14px;padding:10px 12px;background:#131722;border:1px solid #222733;border-radius:10px}
              .set-title{font-weight:600;color:#f3f4f6;font-size:13px}
              .set-desc{color:#9ca3af;font-size:11.5px;margin-top:2px}
              .switch{position:relative;display:inline-block;width:40px;height:22px;flex-shrink:0}
              .switch input{opacity:0;width:0;height:0}
              .slider{position:absolute;cursor:pointer;inset:0;background:#2b3140;border-radius:999px;transition:0.18s}
              .slider:before{position:absolute;content:"";height:16px;width:16px;left:3px;bottom:3px;background:#e5e7eb;border-radius:50%;transition:0.18s}
              input:checked+.slider{background:#2563eb}
              input:checked+.slider:before{transform:translateX(18px)}
              .toolbar{display:flex;align-items:center;justify-content:space-between;gap:8px;flex-wrap:wrap}
              .chip{background:#141824;border:1px solid #272d3d;color:#9ca3af;border-radius:999px;padding:3px 9px;font-size:11px;font-weight:600;cursor:pointer}
              .chip.active{background:rgba(59,130,246,0.2);border-color:#3b82f6;color:#93c5fd}
              .inp{background:#090b10;border:1px solid #2a3040;color:#f3f4f6;border-radius:7px;padding:6px 9px;font:12px ui-monospace,Consolas,monospace;flex:1;min-width:120px}
              .inp.sm{padding:4px 8px;font-size:11.5px;flex:0 1 165px}
              .log-box{flex:1;min-height:180px;background:#080a0e;border:1px solid #1e2330;border-radius:10px;padding:8px 10px;overflow:auto;font:11.5px/1.45 ui-monospace,Consolas,monospace}
              .log-line{padding:2px 0;border-bottom:1px solid rgba(255,255,255,0.03);word-break:break-all}
              .lt{color:#64748b;margin-right:6px}.ls{color:#38bdf8;margin-right:6px}
              .lv-error .lm{color:#f87171}.lv-warn .lm{color:#fbbf24}.lv-fetch .lm{color:#a78bfa}.lv-event .lm{color:#34d399}.lv-info .lm{color:#60a5fa}
              .empty{color:#64748b;padding:24px 10px;text-align:center}
              .banner-warn{display:flex;align-items:center;justify-content:space-between;gap:10px;padding:8px 12px;background:rgba(245,158,11,0.12);border:1px solid rgba(245,158,11,0.4);border-radius:9px;color:#fcd34d;font-size:12px}
              .eval-row{display:flex;align-items:center;gap:6px}
              .prompt{font:700 13px ui-monospace,Consolas,monospace;color:#60a5fa}
              .foot-actions{display:flex;justify-content:flex-end;margin-top:4px}
            </style>
            <div class="bg">
              <div class="panel">
                <div class="head">
                  <div class="title-wrap">
                    <h3>${esc(t('devTitle'))}</h3>
                    <button class="pill ${settings.debugMode ? 'on' : 'off'}" id="hdrDebugPill">
                      ${esc(settings.debugMode ? t('debugBadgeOn') : t('debugBadgeOff'))}
                    </button>
                  </div>
                  <div class="row-gap">
                    <button class="btn sm" id="hdrLang">${lang === 'zh' ? 'EN' : '中文'}</button>
                    ${ctx.api.openTrainer ? `<button class="btn sm warn" id="hdrTrainer">${esc(t('shortTrainer'))}</button>` : ''}
                    <button class="btn sm" id="hdrMods">${esc(t('shortMods'))}</button>
                    <button class="btn sm" id="hdrClose">${esc(t('btnClose'))} (F10)</button>
                  </div>
                </div>
                <div class="tabs">
                  ${tabBtn('overview', t('devTabOverview'))}
                  ${tabBtn('settings', t('devTabSettings'))}
                  ${tabBtn('debug', `${t('devTabDebug')}${errorCount ? ` (${errorCount})` : ''}`)}
                  ${tabBtn('state', t('devTabState'))}
                </div>
                <div class="body">${bodyHtml}</div>
              </div>
            </div>`;

            inspectorRoot.querySelector('.bg').onclick = (e) => { if (e.target === e.currentTarget) closeInspector(); };
            inspectorRoot.querySelector('#hdrClose').onclick = closeInspector;
            inspectorRoot.querySelector('#hdrDebugPill').onclick = () => setSetting('debugMode', !settings.debugMode);
            inspectorRoot.querySelector('#hdrLang').onclick = () => ctx.api.i18n?.toggleLang?.();
            inspectorRoot.querySelector('#hdrMods').onclick = () => { closeInspector(); ctx.api.openOverlay(); };
            const hdrTr = inspectorRoot.querySelector('#hdrTrainer');
            if (hdrTr) hdrTr.onclick = () => { closeInspector(); ctx.api.openTrainer?.(); };

            inspectorRoot.querySelectorAll('[data-tab]').forEach(btn => {
              btn.onclick = () => { activeTab = btn.dataset.tab; renderInspectorBody(); };
            });

            const actToggleDebug = inspectorRoot.querySelector('#actToggleDebug');
            if (actToggleDebug) actToggleDebug.onclick = () => setSetting('debugMode', !settings.debugMode);
            const actTrainer = inspectorRoot.querySelector('#actTrainer');
            if (actTrainer) actTrainer.onclick = () => { closeInspector(); ctx.api.openTrainer?.(); };
            const actMods = inspectorRoot.querySelector('#actMods');
            if (actMods) actMods.onclick = () => { closeInspector(); ctx.api.openOverlay(); };
            const actReload = inspectorRoot.querySelector('#actReload');
            if (actReload) actReload.onclick = clearCacheAndReload;
            const actCopyDiag = inspectorRoot.querySelector('#actCopyDiag');
            if (actCopyDiag) actCopyDiag.onclick = async () => {
              const payload = JSON.stringify({ summary: devApi.summary(), settings, mods: ctx.api.statuses, recentErrors: logs.filter(x => x.level === 'error').slice(-20) }, null, 2);
              try { await navigator.clipboard.writeText(payload); ctx.showToast(t('copiedToast'), 1800); } catch {}
            };

            inspectorRoot.querySelectorAll('[data-setting]').forEach(inp => {
              inp.onchange = () => setSetting(inp.dataset.setting, inp.checked);
            });
            inspectorRoot.querySelectorAll('[data-set-lang]').forEach(btn => {
              btn.onclick = () => ctx.api.i18n?.setLang?.(btn.dataset.setLang);
            });
            const actReset = inspectorRoot.querySelector('#actResetSettings');
            if (actReset) actReset.onclick = () => {
              settings = { ...DEFAULT_SETTINGS };
              persistSettings();
              applyDebugAttr();
              syncFpsHud();
              refreshQuickBar();
              ctx.showToast(t('devResetToast'), 1800);
              renderInspectorBody();
            };

            const actQuickEnable = inspectorRoot.querySelector('#actQuickEnableDebug');
            if (actQuickEnable) actQuickEnable.onclick = () => setSetting('debugMode', true);
            inspectorRoot.querySelectorAll('[data-lfilter]').forEach(btn => {
              btn.onclick = () => { logFilter = btn.dataset.lfilter; renderInspectorBody(); };
            });
            const logSearchInp = inspectorRoot.querySelector('#logSearchInp');
            if (logSearchInp) logSearchInp.oninput = (e) => { logSearch = e.target.value; renderInspectorBody(); };
            const actClearLogs = inspectorRoot.querySelector('#actClearLogs');
            if (actClearLogs) actClearLogs.onclick = () => devApi.clearLogs();
            const actCopyLogs = inspectorRoot.querySelector('#actCopyLogs');
            if (actCopyLogs) actCopyLogs.onclick = async () => {
              const txt = logs.map(l => `[${l.time}][${l.level}][${l.source}] ${l.text}`).join('\n');
              try { await navigator.clipboard.writeText(txt); ctx.showToast(t('copiedToast'), 1800); } catch {}
            };
            const logBox = inspectorRoot.querySelector('#devLogBox');
            if (logBox) logBox.scrollTop = logBox.scrollHeight;

            const cspSafeEval = (expr) => {
              const trimmed = expr.trim();
              if (trimmed === 'summary' || trimmed === 'YgoModDev.summary()') return devApi.summary();
              if (trimmed === 'mods' || trimmed === 'YgoModAPI.mods') return ctx.api.mods;
              if (trimmed === 'settings' || trimmed === 'YgoModDev.getSettings()') return devApi.getSettings();
              if (trimmed === 'state' || trimmed === 'shadowTest.state()') return window.shadowTest?.state?.();
              if (trimmed === 'clear') { devApi.clearLogs(); return 'cleared'; }
              if (trimmed === 'reload') { clearCacheAndReload(); return 'reloading...'; }
              if (trimmed === 'trainer') { closeInspector(); ctx.api.openTrainer?.(); return 'opened trainer'; }
              if (/^[A-Za-z_$][\w$]*(\.[A-Za-z_$][\w$]*|\[\d+\])*$/.test(trimmed)) {
                const roots = { window, globalThis, document, location, localStorage, shadowTest: window.shadowTest, YgoModAPI: ctx.api, YgoModDev: devApi, YgoModTrainer: window.YgoModTrainer };
                const parts = trimmed.replace(/\[(\d+)\]/g, '.$1').split('.');
                let cur = parts[0] in roots ? roots[parts[0]] : window[parts[0]];
                for (let i = 1; i < parts.length && cur != null; i++) cur = cur[parts[i]];
                return cur;
              }
              throw new Error('CSP blocks arbitrary eval; try dot-paths (e.g. YgoModAPI.mods) or summary / state / mods / settings / clear / reload / trainer');
            };

            const runEval = () => {
              const inp = inspectorRoot.querySelector('#evalInp');
              const expr = inp?.value?.trim();
              if (!expr) return;
              evalHistory.push(expr);
              evalHistIdx = evalHistory.length;
              pushLog('info', 'eval>', expr);
              try {
                let res;
                try {
                  const fn = new Function('YgoModAPI', 'YgoModDev', 'YgoModTrainer', 'shadowTest', `return (${expr});`);
                  res = fn(ctx.api, devApi, window.YgoModTrainer, window.shadowTest);
                } catch (e) {
                  if (String(e?.message || e).includes('Content Security Policy') || e instanceof EvalError) {
                    res = cspSafeEval(expr);
                  } else {
                    throw e;
                  }
                }
                pushLog('log', 'eval=', safeFormatArg(res));
              } catch (err) {
                pushLog('error', 'eval!', err?.message || String(err));
              }
              if (inp) inp.value = '';
            };
            const actEval = inspectorRoot.querySelector('#actEval');
            if (actEval) actEval.onclick = runEval;
            const evalInp = inspectorRoot.querySelector('#evalInp');
            if (evalInp) {
              evalInp.onkeydown = (e) => {
                if (e.key === 'Enter') { e.preventDefault(); runEval(); }
                else if (e.key === 'ArrowUp' && evalHistory.length) {
                  e.preventDefault();
                  evalHistIdx = Math.max(0, evalHistIdx - 1);
                  evalInp.value = evalHistory[evalHistIdx] || '';
                } else if (e.key === 'ArrowDown' && evalHistory.length) {
                  e.preventDefault();
                  evalHistIdx = Math.min(evalHistory.length, evalHistIdx + 1);
                  evalInp.value = evalHistory[evalHistIdx] || '';
                }
              };
            }

            inspectorRoot.querySelectorAll('[data-sub]').forEach(btn => {
              btn.onclick = () => { stateSubTab = btn.dataset.sub; renderInspectorBody(); };
            });
            const cSearch = inspectorRoot.querySelector('#contentSearchInp');
            if (cSearch) cSearch.oninput = (e) => { contentSearch = e.target.value; populateStateDump(); };
            const actRefreshState = inspectorRoot.querySelector('#actRefreshState');
            if (actRefreshState) actRefreshState.onclick = populateStateDump;
            const actCopyState = inspectorRoot.querySelector('#actCopyState');
            if (actCopyState) actCopyState.onclick = async () => {
              const box = inspectorRoot.querySelector('#stateDumpBox');
              if (box) {
                try { await navigator.clipboard.writeText(box.textContent || ''); ctx.showToast(t('copiedToast'), 1800); } catch {}
              }
            };
            if (activeTab === 'state') populateStateDump();
          }

          async function populateStateDump() {
            const box = inspectorRoot?.querySelector('#stateDumpBox');
            if (!box) return;
            if (stateSubTab === 'live') {
              const st = window.shadowTest?.state?.();
              box.textContent = st
                ? JSON.stringify({ screen: window.shadowTest?.screen?.(), activeSlot: st.activeSlot, run: st.run, profile: st.profile, settings: st.settings }, null, 2)
                : t('trEngineBooting');
            } else if (stateSubTab === 'save') {
              const parsed = await ctx.api.saves.loadParsed().catch(e => ({ error: e?.message || String(e) }));
              box.textContent = JSON.stringify(parsed, null, 2);
            } else if (stateSubTab === 'mods') {
              box.textContent = JSON.stringify(ctx.api.statuses, null, 2);
            } else if (stateSubTab === 'storage') {
              const out = {};
              for (let i = 0; i < localStorage.length; i++) {
                const k = localStorage.key(i);
                const v = localStorage.getItem(k);
                out[k] = v && v.length > 300 ? v.slice(0, 300) + `… (${v.length} chars)` : v;
              }
              box.textContent = JSON.stringify(out, null, 2);
            } else {
              if (!lastContent) {
                box.textContent = t('stateNoContent');
                return;
              }
              const q = contentSearch.trim().toLowerCase();
              const zhCards = lastZhMap?.cards || {};
              const zhUi = lastZhMap?.ui || {};
              const cards = (lastContent.cards || [])
                .filter(c => {
                  if (!q) return true;
                  const zhArr = zhCards[String(c.id)];
                  const zhName = Array.isArray(zhArr) ? String(zhArr[0] || '') : '';
                  return String(c.id).includes(q) || String(c.name || '').toLowerCase().includes(q) || zhName.toLowerCase().includes(q);
                })
                .slice(0, 40)
                .map(c => {
                  const zhArr = zhCards[String(c.id)];
                  return { id: c.id, name: c.name, zh: Array.isArray(zhArr) ? zhArr[0] : undefined, atk: c.atk, def: c.defense ?? c.def, level: c.level };
                });
              const relics = Object.entries(lastContent.artifacts || {})
                .map(([key, v]) => {
                  const en = Array.isArray(v) ? String(v[0] || key) : String(v?.name || key);
                  const zh = zhUi[en] || '';
                  return { key, name: en, zh: zh || undefined, price: Array.isArray(v) ? v[1] : undefined };
                })
                .filter(r => !q || r.key.toLowerCase().includes(q) || r.name.toLowerCase().includes(q) || (r.zh && r.zh.toLowerCase().includes(q)))
                .slice(0, 30);
              box.textContent = JSON.stringify({ cardsMatched: cards.length, cards, relicsMatched: relics.length, relics }, null, 2);
            }
          }

          function closeInspector() {
            if (inspectorHost) { inspectorHost.remove(); inspectorHost = null; inspectorRoot = null; }
          }

          function openInspector(tab) {
            if (tab) activeTab = tab;
            if (inspectorHost && inspectorHost.isConnected && !tab) {
              closeInspector();
              return;
            }
            if (!inspectorHost || !inspectorHost.isConnected) {
              inspectorHost = document.createElement('div');
              inspectorHost.id = 'ygo-mod-dev-inspector';
              inspectorRoot = inspectorHost.attachShadow({ mode: 'closed' });
              document.body.appendChild(inspectorHost);
            }
            renderInspectorBody();
          }

          function refreshQuickBar() {
            const existing = document.getElementById('ygo-dev-pill');
            if (!settings.showQuickBar) {
              if (existing) existing.remove();
              return;
            }
            if (!document.body) return;
            const pill = existing || document.createElement('div');
            pill.id = 'ygo-dev-pill';
            pill.style.cssText = [
              'position:fixed', 'left:10px', 'bottom:10px', 'z-index:2147483635',
              'display:flex', 'align-items:center', 'gap:5px', 'padding:4px 6px',
              'background:rgba(12,14,19,0.86)', 'border:1px solid #262b36',
              'border-radius:9px', 'backdrop-filter:blur(8px)',
              'box-shadow:0 8px 24px rgba(0,0,0,0.45)',
              'font:600 11px/1 "Microsoft YaHei UI","Segoe UI",monospace'
            ].join(';');
            pill.innerHTML = '';

            const mkBtn = (label, fn, color, border) => {
              const b = document.createElement('button');
              b.textContent = label;
              b.style.cssText = `background:rgba(24,29,40,0.92);color:${color};border:1px solid ${border};border-radius:6px;padding:4px 8px;cursor:pointer;font:inherit`;
              b.onclick = fn;
              return b;
            };

            pill.appendChild(mkBtn(t('barDev'), () => openInspector(), '#60a5fa', 'rgba(96,165,250,0.45)'));
            const dbgLabel = settings.debugMode
              ? `${t('barDebugOn')}${errorCount ? ` (${errorCount})` : ''}`
              : t('barDebugOff');
            pill.appendChild(mkBtn(
              dbgLabel,
              () => setSetting('debugMode', !settings.debugMode),
              settings.debugMode ? (errorCount ? '#f87171' : '#4ade80') : '#94a3b8',
              settings.debugMode ? (errorCount ? 'rgba(248,113,113,0.5)' : 'rgba(74,222,128,0.45)') : '#334155'
            ));
            if (ctx.api.openTrainer) {
              pill.appendChild(mkBtn(t('barTrainer'), () => ctx.api.openTrainer(), '#fcd34d', 'rgba(250,204,21,0.45)'));
            }
            if (ctx.api.hasModSettings?.()) {
              pill.appendChild(mkBtn(t('barModSettings'), () => ctx.api.openModSettings(), '#c084fc', 'rgba(192,132,252,0.45)'));
            }
            pill.appendChild(mkBtn(t('barMods'), () => ctx.api.openOverlay(), '#cbd5e1', '#334155'));
            pill.appendChild(mkBtn(t('barReload'), clearCacheAndReload, '#cbd5e1', '#334155'));

            if (!existing) document.body.appendChild(pill);
          }

          window.addEventListener('keydown', (e) => {
            if (e.key === 'F10' && !e.repeat) {
              e.preventDefault();
              openInspector();
            } else if (e.key === 'Escape' && inspectorHost && inspectorHost.isConnected) {
              closeInspector();
            }
          }, true);

          ctx.events.on('i18n:change', () => {
            refreshQuickBar();
            refreshInspectorIfOpen();
          });

          ctx.events.on('runtime:ready', () => {
            refreshQuickBar();
          });

          applyDebugAttr();
          syncFpsHud();

          ctx.onDomReady(() => {
            refreshQuickBar();
            if (settings.showStartupToast) {
              ctx.showToast(t('devReadyToast'), 3200);
            }
            origConsole?.info('[YgoModDev] Developer Settings & Debug Mode ready (F10). Trainer (F8), MODs (F9).');
          });
        }
        """;

    public static string BuildTrainerOverlayScript() => """
        // Built-in F8 Real-Time In-Game Trainer (mod-trainer-overlay.js)
        // Connects directly to window.shadowTest.command('mod-trainer', ...) -> Pyodide mobile_backend.py -> storage.py
        // Works live with ZERO reload & genuine SHA-256 signed saves on BOTH Web (IndexedDB) and Desktop (ShadowNative)!
        export function init(ctx) {
          let cachedContent = null;
          let cachedZh = null;

          ctx.onFetch(/(?:^|\/)content\.json(?:\?|$)/, async (resp) => {
            try { cachedContent = await resp.clone().json(); } catch {}
            return resp;
          });
          ctx.onFetch(/(?:^|\/)i18n\/zh\.json(?:\?|$)/, async (resp) => {
            try { cachedZh = await resp.clone().json(); } catch {}
            return resp;
          });

          // Real live Web Animation playbackRate controller (1x .. 4x)
          const SPEED_KEY = 'ygomod:anim-speed';
          let animSpeed = Math.max(1, Math.min(4, Number(localStorage.getItem(SPEED_KEY) || '1') || 1));
          const speedSeen = new WeakSet();
          const origAnimate = Element.prototype.animate;
          function applyAnimSpeed(anim) {
            if (!anim || speedSeen.has(anim)) return anim;
            speedSeen.add(anim);
            if (animSpeed !== 1) {
              try { anim.playbackRate = anim.playbackRate * animSpeed; } catch {}
            }
            return anim;
          }
          Element.prototype.animate = function(...args) {
            return applyAnimSpeed(origAnimate.apply(this, args));
          };
          function setAnimSpeed(mult) {
            const prev = animSpeed || 1;
            animSpeed = Math.max(1, Math.min(4, Number(mult) || 1));
            try { localStorage.setItem(SPEED_KEY, String(animSpeed)); } catch {}
            try {
              const ratio = animSpeed / prev;
              document.getAnimations().forEach(a => { try { a.playbackRate *= ratio; } catch {} });
            } catch {}
          }

          const t = (key, params) => ctx.api.i18n ? ctx.api.i18n.t(key, params) : key;
          const esc = (v) => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

          function getBridge() {
            return window.shadowTest && typeof window.shadowTest.command === 'function' ? window.shadowTest : null;
          }

          function refreshGameUi() {
            const br = getBridge();
            if (!br) return;
            try {
              const scr = br.screen?.();
              if (scr && scr !== 'duel') {
                br.go(scr);
              }
            } catch {}
          }

          async function runTrainerOp(op, payload = {}) {
            const br = getBridge();
            if (!br) throw new Error(t('trEngineBooting'));
            const result = await br.command('mod-trainer', { op, ...payload });
            refreshGameUi();
            return result;
          }

          async function runCheatCode(code) {
            const br = getBridge();
            if (!br) throw new Error(t('trEngineBooting'));
            const result = await br.command('cheat', String(code || '').trim());
            refreshGameUi();
            return result;
          }

          async function instantWinDuel() {
            const br = getBridge();
            if (!br) throw new Error(t('trEngineBooting'));
            if (br.screen?.() !== 'duel') throw new Error(t('trNotInDuel'));
            const st = br.state?.();
            const eng = br.engine?.();
            const lp = Number(st?.run?.lp || 8000);
            const events = typeof eng?.summary === 'function' ? eng.summary() : [];
            await br.command('finish', { winner: 0, lp, reason: 0, events });
            try { eng?.destroy?.(); } catch {}
            const nextState = br.state?.();
            const nextStage = nextState?.run?.stage === 'shop' ? 'shop' : (nextState?.run?.stage === 'routes' ? 'routes' : 'ending');
            br.go(nextStage);
          }

          function cardZhName(id) {
            const arr = cachedZh?.cards?.[String(id)];
            return Array.isArray(arr) && arr[0] ? String(arr[0]) : '';
          }

          function relicInfo(key) {
            const raw = cachedContent?.artifacts?.[key];
            const en = Array.isArray(raw) ? String(raw[0] || key) : String(raw?.name || key);
            const price = Array.isArray(raw) ? raw[1] : null;
            const zh = cachedZh?.ui?.[en] ? String(cachedZh.ui[en]) : '';
            const label = ctx.api.i18n?.getLang?.() === 'zh' && zh ? `${zh} (${en})` : en;
            return { key, en, zh, price, label };
          }

          function charDisplayName(idx) {
            const c = cachedContent?.characters?.[idx];
            if (!c) return `#${idx}`;
            const en = String(c.name || `#${idx}`);
            const zh = cachedZh?.ui?.[en];
            return ctx.api.i18n?.getLang?.() === 'zh' && zh ? zh : en;
          }

          function searchCards(query) {
            if (!cachedContent?.cards || !query) return [];
            const q = query.trim().toLowerCase();
            if (!q) return [];
            const out = [];
            for (const c of cachedContent.cards) {
              const idStr = String(c.id ?? '');
              const en = String(c.name || '');
              const zh = cardZhName(c.id);
              if (idStr.includes(q) || en.toLowerCase().includes(q) || zh.toLowerCase().includes(q)) {
                out.push({ id: c.id, en, zh, atk: c.atk, def: c.defense ?? c.def, level: c.level });
                if (out.length >= 10) break;
              }
            }
            return out;
          }

          function searchRelics(query) {
            if (!cachedContent?.artifacts || !query) return [];
            const q = query.trim().toLowerCase();
            if (!q) return [];
            const retired = new Set(cachedContent.retiredArtifacts || []);
            const out = [];
            for (const key of Object.keys(cachedContent.artifacts)) {
              if (retired.has(key)) continue;
              const info = relicInfo(key);
              if (key.toLowerCase().includes(q) || info.en.toLowerCase().includes(q) || info.zh.toLowerCase().includes(q)) {
                out.push(info);
                if (out.length >= 10) break;
              }
            }
            return out;
          }

          function searchPacks(query) {
            if (!Array.isArray(cachedContent?.packs) || !query) return [];
            const q = query.trim().toLowerCase();
            if (!q) return [];
            const out = [];
            for (const p of cachedContent.packs) {
              const id = String(p.id || '');
              const name = String(p.name || '');
              const zh = cachedZh?.ui?.[name] || '';
              if (id.toLowerCase().includes(q) || name.toLowerCase().includes(q) || zh.toLowerCase().includes(q)) {
                out.push({ id, name: ctx.api.i18n?.getLang?.() === 'zh' && zh ? `${zh} (${id})` : `${name} (${id})` });
                if (out.length >= 10) break;
              }
            }
            return out;
          }

          let host = null;
          let root = null;
          let activeTab = 'run';
          let addToDeckToo = true;
          let lastStatus = { text: '', err: false };
          let busy = false;

          function closeTrainer() {
            if (host) { host.remove(); host = null; root = null; }
          }

          function setMsg(text, err = false) {
            lastStatus = { text, err };
            const el = root?.querySelector('#msg');
            if (el) {
              el.style.color = err ? '#f87171' : '#4ade80';
              el.textContent = text;
            }
          }

          async function execAction(fn, okText) {
            if (busy) return;
            busy = true;
            try {
              const r = await fn();
              const msg = typeof okText === 'function' ? okText(r) : okText;
              setMsg(msg || t('trAppliedLive'), false);
              if (msg) ctx.showToast(msg, 2000);
              renderTrainer();
            } catch (e) {
              const errMsg = t('trErrorPrefix') + (e?.message || e);
              setMsg(errMsg, true);
              ctx.showToast(errMsg, 2600);
            } finally {
              busy = false;
            }
          }

          function renderTrainer() {
            if (!root) return;
            const br = getBridge();
            const st = br?.state?.() || null;
            const run = st?.run || null;
            const prof = st?.profile || null;
            const gset = st?.settings || null;
            const scr = br?.screen?.() || 'booting';
            const lang = ctx.api.i18n?.getLang?.() || 'zh';
            const totalPlayable = cachedContent?.playable?.length || 35;

            const tabBtn = (id, label) =>
              `<button class="tab ${activeTab === id ? 'active' : ''}" data-tab="${id}">${esc(label)}</button>`;

            let bodyHtml = '';
            if (!br) {
              bodyHtml = `<div class="warn-box">${esc(t('trEngineBooting'))}</div>`;
            } else if (activeTab === 'run') {
              const runBanner = run
                ? `<div class="stats">
                    <div class="st"><span>${esc(t('trChar'))}</span><b class="accent">${esc(charDisplayName(run.character))}</b></div>
                    <div class="st"><span>${esc(t('trStage'))}</span><b>L${esc(run.loop || 0)} · R${esc((run.round || 0) + 1)} (${esc(scr)})</b></div>
                    <div class="st"><span>${esc(t('trGold'))}</span><b class="gold-txt">${esc(run.gold ?? 0)}</b></div>
                    <div class="st"><span>${esc(t('trLp'))}</span><b class="ok-txt">${esc(run.lp ?? 0)}</b></div>
                    <div class="st"><span>${esc(t('trPool'))}</span><b>${Array.isArray(run.pool) ? run.pool.length : 0}</b></div>
                    <div class="st"><span>${esc(t('trRelics'))}</span><b>${Array.isArray(run.artifacts) ? run.artifacts.length : 0}</b></div>
                    <div class="st"><span>${esc(t('trCurses'))}</span><b class="${(run.curses?.length || 0) ? 'err-txt' : ''}">${Array.isArray(run.curses) ? run.curses.length : 0}</b></div>
                    <div class="st"><span>${esc(t('trShopItems'))}</span><b>${Array.isArray(run.shop) ? run.shop.filter(x => !x?.sold).length : 0}</b></div>
                  </div>`
                : `<div class="warn-box">${esc(t('trNoActiveRun'))}</div>`;

              bodyHtml = `
                ${runBanner}
                <div class="sec">${esc(t('trQuickGoldLp'))}</div>
                <div class="row">
                  <button class="gold sm" id="qG500">+500 ${esc(t('trGold'))}</button>
                  <button class="gold sm" id="qG2k">+2000 ${esc(t('trGold'))}</button>
                  <button class="gold sm" id="qG9k">+9999 ${esc(t('trGold'))}</button>
                  <button class="gold sm" id="qGMax">99999 ${esc(t('trGold'))}</button>
                  <button class="sec sm" id="qLp8k">8000 LP</button>
                  <button class="sec sm" id="qLp16k">16000 LP</button>
                  <button class="sec sm" id="qLp99k">99999 LP</button>
                </div>

                <div class="sec">${esc(t('trShopDuelCtrl'))}</div>
                <div class="row">
                  <button class="ok sm" id="qFreeShop">${esc(t('trFreeShopNow'))}</button>
                  <button class="sec sm" id="qRestock">${esc(t('trFreeRestock'))}</button>
                  <button class="gold sm" id="qRestockFree">${esc(t('trRestockAndFree'))}</button>
                  <button class="sec sm" id="qClearCurses">${esc(t('trClearCurses'))}</button>
                  ${scr === 'duel' ? `<button class="danger sm" id="qWinDuel">${esc(t('trInstantWin'))}</button>` : ''}
                </div>

                <div class="sec">${esc(t('trExactEdit'))}</div>
                <div class="row">
                  <label>${esc(t('trGold'))}</label>
                  <input id="inpGold" type="number" value="${run ? esc(run.gold ?? '') : ''}" placeholder="9999">
                  <button id="btnSetGold">${esc(t('btnApply'))}</button>
                </div>
                <div class="row">
                  <label>${esc(t('trLp'))}</label>
                  <input id="inpLp" type="number" value="${run ? esc(run.lp ?? '') : ''}" placeholder="16000">
                  <button id="btnSetLp">${esc(t('btnApply'))}</button>
                </div>
                <div class="row">
                  <label>${esc(t('trRoundLoop'))}</label>
                  <input id="inpRound" type="number" min="0" max="8" value="${run ? esc(run.round ?? 0) : 0}" placeholder="Round 0-8" style="max-width:110px">
                  <input id="inpLoop" type="number" min="0" value="${run ? esc(run.loop ?? 0) : 0}" placeholder="Loop 0+" style="max-width:110px">
                  <button class="sec" id="btnSetProg">${esc(t('btnApply'))}</button>
                </div>`;
            } else if (activeTab === 'items') {
              const ownedRelics = Array.isArray(run?.artifacts) ? run.artifacts : [];
              bodyHtml = `
                ${!run ? `<div class="warn-box">${esc(t('trNoActiveRun'))}</div>` : ''}
                <div class="sec">${esc(t('trAddCardSec'))}</div>
                <div class="row">
                  <input id="cardInput" type="text" placeholder="${esc(t('trCardPh'))}">
                  <label class="chk"><input type="checkbox" id="chkToDeck" ${addToDeckToo ? 'checked' : ''}><span>${esc(t('trAlsoToDeck'))}</span></label>
                  <button id="btnAddCard">${esc(t('btnAdd'))}</button>
                </div>
                <div class="suggest" id="cardSuggest"></div>

                <div class="sec">${esc(t('trAddRelicSec'))}</div>
                <div class="row">
                  <input id="relicInput" type="text" placeholder="${esc(t('trRelicPh'))}">
                  <button id="btnAddRelic">${esc(t('btnAdd'))}</button>
                </div>
                <div class="suggest" id="relicSuggest"></div>

                <div class="sec">${esc(t('trAddPackSec'))}</div>
                <div class="row">
                  <input id="packInput" type="text" placeholder="${esc(t('trPackPh'))}">
                  <button id="btnAddPack">${esc(t('btnAdd'))}</button>
                </div>
                <div class="suggest" id="packSuggest"></div>

                ${ownedRelics.length ? `
                  <div class="sec">${esc(t('trOwnedRelics', { n: ownedRelics.length }))}</div>
                  <div class="chips-wrap">
                    ${ownedRelics.map(k => {
                      const info = relicInfo(k);
                      return `<span class="owned-chip"><span>${esc(info.label)}</span><button class="x-btn" data-rm-relic="${esc(k)}" title="${esc(t('btnRemove'))}">×</button></span>`;
                    }).join('')}
                  </div>` : ''}`;
            } else if (activeTab === 'unlocks') {
              const unlockedCnt = prof?.unlocked?.length ?? 0;
              const isChars = Boolean(prof?.unlock_all || unlockedCnt >= totalPlayable);
              const isVars = Boolean(prof?.variants_unlocked);
              const isLvls = Boolean(prof?.testing_all_clears);
              const isSleeves = Boolean(prof?.sleeves_all);
              const isRelicless = Boolean(prof?.relicless_unlocked);
              const isGallery = Boolean(prof?.gallery_unlocked && prof?.cpu_viewer_unlocked);
              const regCards = st?.registeredCards?.length ?? 0;

              const uCard = (id, title, done, sub) => `
                <button class="u-card ${done ? 'done' : ''}" data-uflag="${id}">
                  <div class="u-top"><span>${esc(title)}</span><span class="u-st">${done ? '✓' : '+'}</span></div>
                  <div class="u-sub">${esc(sub)}</div>
                </button>`;

              bodyHtml = `
                <div class="hero-unlock">
                  <div>
                    <div class="hu-title">${esc(t('trUnlockMasterTitle'))}</div>
                    <div class="hu-desc">${esc(t('trUnlockMasterDesc'))}</div>
                  </div>
                  <button class="gold" id="btnUnlockEverything">${esc(t('trUnlockMasterBtn'))}</button>
                </div>

                <div class="u-grid">
                  ${uCard('all_chars', t('trUChars'), isChars, `${unlockedCnt} / ${totalPlayable}`)}
                  ${uCard('all_variants', t('trUVars'), isVars, 'alteregos')}
                  ${uCard('all_levels', t('trULevels'), isLvls, 'LVL -1 ~ 6 (touchgrass)')}
                  ${uCard('all_sleeves', t('trUSleeves'), isSleeves, 'sleevesforall')}
                  ${uCard('all_fused', t('trUFused'), (prof?.fused_relics?.length || 0) > 10, 'polymerization')}
                  ${uCard('relicless', t('trURelicless'), isRelicless, 'reliclessforall')}
                  ${uCard('gallery_cpu_sandbox', t('trUGallery'), isGallery, t('trUGallerySub'))}
                  ${uCard('all_collection', t('trUCollection'), regCards > 1500, `${regCards} ${t('trCardsUnit')}`)}
                </div>

                <div class="sec">${esc(t('trOfficialCodeSec'))}</div>
                <div class="row">
                  <input id="inpCheatCode" type="text" placeholder="${esc(t('trOfficialCodePh'))}">
                  <button class="sec" id="btnRunCheat">${esc(t('btnApply'))}</button>
                </div>`;
            } else {
              const activeSlot = st?.activeSlot || 1;
              const speedBtn = (v) =>
                `<button class="sm ${animSpeed === v ? 'primary' : 'sec'}" data-speed="${v}">${v}x</button>`;
              const qolRow = (key, title, desc, checked) => `
                <div class="set-row">
                  <div><div class="set-title">${esc(title)}</div><div class="set-desc">${esc(desc)}</div></div>
                  <label class="switch"><input type="checkbox" data-gset="${key}" ${checked ? 'checked' : ''}><span class="slider"></span></label>
                </div>`;
              bodyHtml = `
                <div class="sec">${esc(t('trQolSpeedSec'))}</div>
                <div class="set-list">
                  <div class="set-row">
                    <div>
                      <div class="set-title">${esc(t('trAnimMultTitle'))}</div>
                      <div class="set-desc">${esc(t('trAnimMultDesc'))}</div>
                    </div>
                    <div class="row">${[1, 1.5, 2, 3, 4].map(speedBtn).join('')}</div>
                  </div>
                  ${gset ? qolRow('fasterAnimations', t('trFasterAnim'), t('trFasterAnimDesc'), !!gset.fasterAnimations) : ''}
                  ${gset ? qolRow('skipPackAnimations', t('trSkipPacks'), t('trSkipPacksDesc'), !!gset.skipPackAnimations) : ''}
                  ${gset ? qolRow('reduceMotion', t('trReduceMotion'), t('trReduceMotionDesc'), !!gset.reduceMotion) : ''}
                </div>

                <div class="sec">${esc(t('trSlotSaveSec'))}</div>
                <div class="row">
                  <span style="color:#9ca3af;font-size:12px">${esc(t('trActiveSlot'))}:</span>
                  ${[1, 2, 3].map(n => `<button class="sm ${activeSlot === n ? 'primary' : 'sec'}" data-slot="${n}">${esc(t('trSlotN', { n }))}</button>`).join('')}
                </div>
                <div class="row" style="margin-top:4px">
                  <button class="ok" id="btnExportSave">${esc(t('trExportSave'))}</button>
                  <button class="sec" id="btnImportSave">${esc(t('trImportSave'))}</button>
                  <input type="file" id="inpImportFile" accept=".json,application/json" style="display:none">
                  <button class="sec" id="btnReloadGame">${esc(t('trReloadGame'))}</button>
                </div>`;
            }

            root.innerHTML = `<style>
              *{box-sizing:border-box}
              .bg{position:fixed;inset:0;z-index:2147483645;background:rgba(5,7,11,0.78);backdrop-filter:blur(4px);display:flex;align-items:center;justify-content:center;font:13px/1.45 "Microsoft YaHei UI","Segoe UI",system-ui,sans-serif;color:#e5e7eb}
              .box{width:min(720px,95vw);max-height:88vh;display:flex;flex-direction:column;background:#0c0e13;border:1px solid #353024;border-radius:14px;box-shadow:0 24px 64px rgba(0,0,0,0.72);overflow:hidden}
              .top{display:flex;justify-content:space-between;align-items:center;gap:8px;padding:11px 16px;background:#12151e;border-bottom:1px solid #242936}
              h3{margin:0;color:#fcd34d;font-size:14.5px;font-weight:700;display:flex;align-items:center;gap:8px}
              .live-dot{display:inline-block;width:7px;height:7px;border-radius:50%;background:${br ? '#4ade80' : '#f59e0b'}}
              .tabs{display:flex;gap:4px;padding:7px 16px 0;background:#11141c;border-bottom:1px solid #232834;overflow-x:auto}
              .tab{background:transparent;border:none;border-bottom:2px solid transparent;color:#9ca3af;padding:6px 12px;font-size:12.5px;font-weight:600;cursor:pointer;border-radius:6px 6px 0 0;white-space:nowrap}
              .tab:hover{color:#e5e7eb;background:rgba(255,255,255,0.04)}
              .tab.active{color:#fcd34d;border-bottom-color:#f59e0b;background:rgba(245,158,11,0.08)}
              .body{padding:14px 16px;overflow:auto;display:flex;flex-direction:column;gap:10px;min-height:360px}
              .stats{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:7px}
              @media(max-width:600px){.stats{grid-template-columns:repeat(2,minmax(0,1fr))}}
              .st{background:#131722;border:1px solid #232836;border-radius:9px;padding:7px 10px;display:flex;flex-direction:column;gap:2px}
              .st span{font-size:11px;color:#9ca3af}
              .st b{font-size:13.5px;font-family:ui-monospace,Consolas,monospace;color:#f8fafc;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
              .st b.accent{color:#60a5fa}.st b.gold-txt{color:#fcd34d}.st b.ok-txt{color:#4ade80}.st b.err-txt{color:#f87171}
              .warn-box{padding:9px 12px;background:rgba(245,158,11,0.12);border:1px solid rgba(245,158,11,0.35);border-radius:9px;color:#fcd34d;font-size:12px}
              .sec{font-size:11px;font-weight:700;color:#9ca3af;text-transform:uppercase;letter-spacing:0.04em;margin-top:2px}
              .row{display:flex;gap:7px;align-items:center;flex-wrap:wrap}
              label{width:88px;color:#9ca3af;font-size:12px;flex-shrink:0}
              label.chk{width:auto;display:inline-flex;align-items:center;gap:5px;color:#cbd5e1;cursor:pointer;font-size:12px}
              label.chk input{flex:none;min-width:0;width:14px;height:14px;accent-color:#f59e0b}
              input[type="text"],input[type="number"]{flex:1;min-width:120px;background:#080a0f;color:#f8fafc;border:1px solid #2a3040;border-radius:7px;padding:6px 9px;font:12.5px ui-monospace,Consolas,monospace}
              button{background:#2563eb;color:#fff;border:1px solid #3b82f6;border-radius:7px;padding:6px 11px;font-size:12px;font-weight:600;cursor:pointer}
              button:hover{filter:brightness(1.08)}
              button.sec{background:#1a1f2c;border:1px solid #2d3446;color:#e5e7eb}
              button.primary{background:#2563eb;border-color:#3b82f6;color:#fff}
              button.ok{background:#15803d;border-color:#22c55e;color:#fff}
              button.gold{background:rgba(217,119,6,0.24);border-color:rgba(245,158,11,0.55);color:#fcd34d}
              button.danger{background:rgba(220,38,38,0.22);border-color:rgba(248,113,113,0.5);color:#fca5a5}
              button.sm{padding:4px 9px;font-size:11.5px;border-radius:6px}
              .suggest{display:flex;flex-direction:column;gap:4px}
              .sg-item{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:5px 9px;background:#131722;border:1px solid #232938;border-radius:7px;font-size:12px}
              .sg-name{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
              .sg-id{color:#64748b;font-family:ui-monospace,Consolas,monospace;font-size:11px;margin-right:6px}
              .sg-stat{color:#94a3b8;font-family:ui-monospace,Consolas,monospace;font-size:11px;margin-left:6px}
              .chips-wrap{display:flex;flex-wrap:wrap;gap:6px}
              .owned-chip{display:inline-flex;align-items:center;gap:6px;padding:3px 8px;background:#141925;border:1px solid #283044;border-radius:999px;font-size:11.5px;color:#e2e8f0}
              .x-btn{background:transparent;border:none;color:#f87171;padding:0 2px;font-size:14px;line-height:1;cursor:pointer}
              .hero-unlock{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:11px 14px;background:linear-gradient(135deg,rgba(245,158,11,0.14),rgba(37,99,235,0.10));border:1px solid rgba(245,158,11,0.4);border-radius:11px}
              .hu-title{font-weight:700;color:#fde68a;font-size:13.5px}
              .hu-desc{font-size:11.5px;color:#cbd5e1;margin-top:2px}
              .u-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}
              @media(max-width:520px){.u-grid{grid-template-columns:1fr}}
              .u-card{text-align:left;background:#131722;border:1px solid #252b3a;border-radius:10px;padding:9px 11px;color:#e5e7eb;display:flex;flex-direction:column;gap:3px}
              .u-card:hover{border-color:#60a5fa;background:#171d2c}
              .u-card.done{border-color:rgba(34,197,94,0.45);background:rgba(21,128,61,0.12)}
              .u-top{display:flex;justify-content:space-between;align-items:center;font-weight:600;font-size:12.5px}
              .u-st{font-family:ui-monospace,Consolas,monospace;color:#4ade80}
              .u-card:not(.done) .u-st{color:#60a5fa}
              .u-sub{font-size:11px;color:#9ca3af;font-family:ui-monospace,Consolas,monospace}
              .set-list{display:flex;flex-direction:column;gap:8px}
              .set-row{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:9px 12px;background:#131722;border:1px solid #222733;border-radius:10px}
              .set-title{font-weight:600;color:#f3f4f6;font-size:12.5px}
              .set-desc{color:#9ca3af;font-size:11.5px;margin-top:2px}
              .switch{position:relative;display:inline-block;width:38px;height:21px;flex-shrink:0}
              .switch input{opacity:0;width:0;height:0}
              .slider{position:absolute;cursor:pointer;inset:0;background:#2b3140;border-radius:999px;transition:0.18s}
              .slider:before{position:absolute;content:"";height:15px;width:15px;left:3px;bottom:3px;background:#e5e7eb;border-radius:50%;transition:0.18s}
              input:checked+.slider{background:#f59e0b}
              input:checked+.slider:before{transform:translateX(17px)}
              .foot{display:flex;align-items:center;justify-content:space-between;gap:10px;padding:9px 16px;background:#10131a;border-top:1px solid #202532;font-size:12px}
              .msg{color:${lastStatus.err ? '#f87171' : '#4ade80'};flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
              .hint{color:#64748b;font-size:11px;white-space:nowrap}
            </style>
            <div class="bg">
              <div class="box">
                <div class="top">
                  <h3><span class="live-dot"></span>${esc(t('trTitle'))}</h3>
                  <div class="row">
                    <button class="sec sm" id="btnLang">${lang === 'zh' ? 'EN' : '中文'}</button>
                    ${ctx.api.openDevTools ? `<button class="sec sm" id="btnDev">${esc(t('shortDev'))}</button>` : ''}
                    <button class="sec sm" id="close">${esc(t('btnClose'))} (F8)</button>
                  </div>
                </div>
                <div class="tabs">
                  ${tabBtn('run', t('trTabRun'))}
                  ${tabBtn('items', t('trTabItems'))}
                  ${tabBtn('unlocks', t('trTabUnlocks'))}
                  ${tabBtn('qol', t('trTabQol'))}
                </div>
                <div class="body">${bodyHtml}</div>
                <div class="foot">
                  <div class="msg" id="msg">${esc(lastStatus.text || t('trLiveReadyHint'))}</div>
                  <div class="hint">F8 · ${br ? 'LIVE' : 'BOOTING'}</div>
                </div>
              </div>
            </div>`;

            // Top & tabs
            root.querySelector('#close').onclick = closeTrainer;
            root.querySelector('.bg').onclick = (e) => { if (e.target === e.currentTarget) closeTrainer(); };
            root.querySelector('#btnLang').onclick = () => ctx.api.i18n?.toggleLang?.();
            const btnDev = root.querySelector('#btnDev');
            if (btnDev) btnDev.onclick = () => { closeTrainer(); ctx.api.openDevTools?.(); };
            root.querySelectorAll('[data-tab]').forEach(b => {
              b.onclick = () => { activeTab = b.dataset.tab; renderTrainer(); };
            });

            if (!br) return;

            // Tab 1: Run Cheats
            if (activeTab === 'run') {
              root.querySelector('#qG500').onclick = () => execAction(() => runTrainerOp('add-gold', { delta: 500 }), t('trAppliedLive'));
              root.querySelector('#qG2k').onclick = () => execAction(() => runTrainerOp('add-gold', { delta: 2000 }), t('trAppliedLive'));
              root.querySelector('#qG9k').onclick = () => execAction(() => runTrainerOp('add-gold', { delta: 9999 }), t('trAppliedLive'));
              root.querySelector('#qGMax').onclick = () => execAction(() => runTrainerOp('set-gold', { amount: 99999 }), t('trAppliedLive'));
              root.querySelector('#qLp8k').onclick = () => execAction(() => runTrainerOp('set-lp', { amount: 8000 }), t('trAppliedLive'));
              root.querySelector('#qLp16k').onclick = () => execAction(() => runTrainerOp('set-lp', { amount: 16000 }), t('trAppliedLive'));
              root.querySelector('#qLp99k').onclick = () => execAction(() => runTrainerOp('set-lp', { amount: 99999 }), t('trAppliedLive'));

              root.querySelector('#qFreeShop').onclick = () => execAction(() => runTrainerOp('free-shop', { restock: false }), r => t('trFreeShopDone', { n: r?.count ?? 0 }));
              root.querySelector('#qRestock').onclick = () => execAction(() => runTrainerOp('restock-shop'), t('trRestocked'));
              root.querySelector('#qRestockFree').onclick = () => execAction(() => runTrainerOp('free-shop', { restock: true }), r => t('trFreeShopDone', { n: r?.count ?? 0 }));
              root.querySelector('#qClearCurses').onclick = () => execAction(() => runTrainerOp('clear-curses'), t('trCursesCleared'));

              const qWin = root.querySelector('#qWinDuel');
              if (qWin) qWin.onclick = () => execAction(async () => { await instantWinDuel(); closeTrainer(); }, t('trDuelWon'));

              root.querySelector('#btnSetGold').onclick = () => {
                const v = Number(root.querySelector('#inpGold').value);
                if (!Number.isFinite(v) || v < 0) return setMsg(t('trInvalidNumber'), true);
                execAction(() => runTrainerOp('set-gold', { amount: Math.floor(v) }), t('trAppliedLive'));
              };
              root.querySelector('#btnSetLp').onclick = () => {
                const v = Number(root.querySelector('#inpLp').value);
                if (!Number.isFinite(v) || v <= 0) return setMsg(t('trInvalidNumber'), true);
                execAction(() => runTrainerOp('set-lp', { amount: Math.floor(v) }), t('trAppliedLive'));
              };
              root.querySelector('#btnSetProg').onclick = () => {
                const rVal = Number(root.querySelector('#inpRound').value);
                const lVal = Number(root.querySelector('#inpLoop').value);
                if (!Number.isFinite(rVal) || !Number.isFinite(lVal)) return setMsg(t('trInvalidNumber'), true);
                execAction(() => runTrainerOp('set-progress', { round: Math.floor(rVal), loop: Math.floor(lVal) }), t('trAppliedLive'));
              };
            }

            // Tab 2: Cards, Relics & Packs
            if (activeTab === 'items') {
              const chkToDeck = root.querySelector('#chkToDeck');
              chkToDeck.onchange = () => { addToDeckToo = chkToDeck.checked; };

              const cardInp = root.querySelector('#cardInput');
              const cardSug = root.querySelector('#cardSuggest');
              const doAddCard = (id, count) => execAction(() => runTrainerOp('add-card', { id, count, toDeck: addToDeckToo }), t('trCardAdded', { id, n: count }));
              cardInp.oninput = () => {
                const list = searchCards(cardInp.value);
                cardSug.innerHTML = list.map(c => {
                  const title = lang === 'zh' && c.zh ? `${c.zh} (${c.en})` : c.en;
                  const stat = c.atk != null ? `ATK ${c.atk} / DEF ${c.def ?? '?'}` : '';
                  return `<div class="sg-item">
                    <div class="sg-name"><span class="sg-id">#${esc(c.id)}</span>${esc(title)}<span class="sg-stat">${esc(stat)}</span></div>
                    <div class="row">
                      <button class="sm" data-cid="${esc(c.id)}" data-n="1">+1</button>
                      <button class="sm sec" data-cid="${esc(c.id)}" data-n="3">+3</button>
                    </div>
                  </div>`;
                }).join('');
                cardSug.querySelectorAll('[data-cid]').forEach(b => {
                  b.onclick = () => doAddCard(Number(b.dataset.cid), Number(b.dataset.n || 1));
                });
              };
              root.querySelector('#btnAddCard').onclick = () => {
                const raw = cardInp.value.trim();
                const num = Number(raw);
                if (Number.isFinite(num) && num > 0) return doAddCard(Math.floor(num), 1);
                const first = searchCards(raw)[0];
                if (first) return doAddCard(Number(first.id), 1);
                setMsg(t('trInvalidCard'), true);
              };

              const relicInp = root.querySelector('#relicInput');
              const relicSug = root.querySelector('#relicSuggest');
              const doAddRelic = (key) => execAction(() => runTrainerOp('add-relic', { key }), t('trRelicAdded', { key }));
              relicInp.oninput = () => {
                const list = searchRelics(relicInp.value);
                relicSug.innerHTML = list.map(r =>
                  `<div class="sg-item">
                    <div class="sg-name"><span class="sg-id">${esc(r.key)}</span>${esc(r.label)}</div>
                    <button class="sm" data-rkey="${esc(r.key)}">+ ${esc(t('btnAdd'))}</button>
                  </div>`
                ).join('');
                relicSug.querySelectorAll('[data-rkey]').forEach(b => {
                  b.onclick = () => doAddRelic(b.dataset.rkey);
                });
              };
              root.querySelector('#btnAddRelic').onclick = () => {
                const raw = relicInp.value.trim();
                if (!raw) return setMsg(t('trInvalidRelic'), true);
                const key = cachedContent?.artifacts?.[raw] ? raw : (searchRelics(raw)[0]?.key || raw);
                doAddRelic(key);
              };

              const packInp = root.querySelector('#packInput');
              const packSug = root.querySelector('#packSuggest');
              const doAddPack = (id, count) => execAction(() => runTrainerOp('add-pack', { id, count }), t('trPackAdded', { id, n: count }));
              packInp.oninput = () => {
                const list = searchPacks(packInp.value);
                packSug.innerHTML = list.map(p =>
                  `<div class="sg-item">
                    <div class="sg-name">${esc(p.name)}</div>
                    <div class="row">
                      <button class="sm" data-pid="${esc(p.id)}" data-n="1">+1</button>
                      <button class="sm sec" data-pid="${esc(p.id)}" data-n="3">+3</button>
                    </div>
                  </div>`
                ).join('');
                packSug.querySelectorAll('[data-pid]').forEach(b => {
                  b.onclick = () => doAddPack(b.dataset.pid, Number(b.dataset.n || 1));
                });
              };
              root.querySelector('#btnAddPack').onclick = () => {
                const raw = packInp.value.trim();
                if (!raw) return setMsg(t('trInvalidPack'), true);
                const first = searchPacks(raw)[0];
                doAddPack(first ? first.id : raw, 1);
              };

              root.querySelectorAll('[data-rm-relic]').forEach(b => {
                b.onclick = () => execAction(() => runTrainerOp('remove-relic', { key: b.dataset.rmRelic }), t('trRelicRemoved', { key: b.dataset.rmRelic }));
              });
            }

            // Tab 3: Unlocks & Official Codes
            if (activeTab === 'unlocks') {
              root.querySelector('#btnUnlockEverything').onclick = () =>
                execAction(() => runTrainerOp('unlock-everything'), t('trUnlockedMasterDone'));
              root.querySelectorAll('[data-uflag]').forEach(b => {
                b.onclick = () => execAction(() => runTrainerOp('unlock-flag', { flag: b.dataset.uflag }), t('trUnlockedDone'));
              });
              root.querySelector('#btnRunCheat').onclick = () => {
                const code = root.querySelector('#inpCheatCode').value.trim();
                if (!code) return setMsg(t('trInvalidCode'), true);
                execAction(() => runCheatCode(code), t('trCodeApplied', { code }));
              };
            }

            // Tab 4: QoL & Saves
            if (activeTab === 'qol') {
              root.querySelectorAll('[data-speed]').forEach(b => {
                b.onclick = () => {
                  const v = Number(b.dataset.speed);
                  setAnimSpeed(v);
                  setMsg(t('trSpeedChanged', { n: v }), false);
                  renderTrainer();
                };
              });
              root.querySelectorAll('[data-gset]').forEach(inp => {
                inp.onchange = () => {
                  const next = { ...(br.state?.()?.settings || {}), [inp.dataset.gset]: inp.checked };
                  execAction(() => br.command('settings', next), t('trAppliedLive'));
                };
              });
              root.querySelectorAll('[data-slot]').forEach(b => {
                b.onclick = () => {
                  const slot = Number(b.dataset.slot);
                  execAction(async () => { await br.command('select-slot', slot); refreshGameUi(); }, t('trSlotSwitched', { n: slot }));
                };
              });
              root.querySelector('#btnExportSave').onclick = async () => {
                try {
                  const raw = await ctx.api.saves.loadRaw();
                  if (!raw || raw === '{}') return setMsg(t('trNoSaveData'), true);
                  const blob = new Blob([raw], { type: 'application/json' });
                  const a = document.createElement('a');
                  a.href = URL.createObjectURL(blob);
                  a.download = `YGO-Rogue-save-backup-${new Date().toISOString().slice(0, 10)}.json`;
                  a.click();
                  setTimeout(() => URL.revokeObjectURL(a.href), 5000);
                  setMsg(t('trExported'), false);
                } catch (e) {
                  setMsg(t('trErrorPrefix') + (e?.message || e), true);
                }
              };
              const inpImport = root.querySelector('#inpImportFile');
              root.querySelector('#btnImportSave').onclick = () => inpImport.click();
              inpImport.onchange = async () => {
                const file = inpImport.files?.[0];
                if (!file) return;
                try {
                  const text = await file.text();
                  const parsed = JSON.parse(text);
                  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('Invalid save JSON');
                  await ctx.api.saves.saveRaw(text);
                  setMsg(t('trImportedReloading'), false);
                  setTimeout(() => location.reload(), 400);
                } catch (e) {
                  setMsg(t('trErrorPrefix') + (e?.message || e), true);
                } finally {
                  inpImport.value = '';
                }
              };
              root.querySelector('#btnReloadGame').onclick = () => location.reload();
            }
          }

          function openTrainer(tab) {
            if (tab) activeTab = tab;
            if (host && host.isConnected && !tab) {
              closeTrainer();
              return;
            }
            if (!host || !host.isConnected) {
              host = document.createElement('div');
              host.id = 'ygo-mod-trainer';
              root = host.attachShadow({ mode: 'closed' });
              document.body.appendChild(host);
            }
            renderTrainer();
          }

          ctx.api.openTrainer = openTrainer;
          window.YgoModTrainer = Object.freeze({
            open: openTrainer,
            runOp: runTrainerOp,
            runCheat: runCheatCode,
            instantWinDuel,
            setAnimSpeed,
            getAnimSpeed: () => animSpeed
          });

          window.addEventListener('keydown', (e) => {
            if (e.key === 'F8' && !e.repeat) {
              e.preventDefault();
              openTrainer();
            } else if (e.key === 'Escape' && host && host.isConnected) {
              closeTrainer();
            }
          }, true);

          ctx.events.on('i18n:change', () => {
            if (host && host.isConnected) renderTrainer();
          });

          document.addEventListener('game-startup-ready', () => {
            if (host && host.isConnected) renderTrainer();
          });

          ctx.onDomReady(() => {
            if (ctx.config?.showTrainerHint !== false && !window.__YGO_MOD_DEV__) {
              ctx.showToast(t('trReadyToast'), 2800);
            }
          });
        }
        """;

    public static string BuildRuntimeScript() => """
        // Yu-Gi-Oh! Rogue Duelist - In-Game JS Mod Runtime (v2.1.0)
        // Automatically injected by YGORogueDuelistCardModInstaller when JS/CSS/Trainer/Developer mods are enabled.
        // Exposes window.YgoModAPI (extensible zh/en i18n, real IndexedDB/ShadowNative saves helper & debug telemetry).

        const RUNTIME_VERSION = '2.2.0';
        const REGISTRY_URL = './mods-registry.json';
        const LANG_STORAGE_KEY = 'ygomod:lang';
        const IDB_NAME = 'ygo-rogue-saves';
        const IDB_STORE = 'saves';
        const IDB_KEY = 'shadow-run-mobile';

        const I18N_LOCALES = {
          zh: {
            __name: '中文',
            modsTitle: '已加载 MOD ({n})',
            modsTabList: '已加载 MOD ({n})',
            modsTabSettings: 'MOD 设置 ({n})',
            modsSettingsBtn: '⚙ 设置',
            modsSettingsEmpty: '当前已启用的 MOD 尚未声明游戏内设置项。MOD 作者可在 mod-manifest.json 写入 settingsSchema 或调用 ctx.settings.define([...])，即可在此自动生成可视化开关、滑块与下拉菜单！',
            modsSettingsReset: '恢复默认',
            modsSettingsResetToast: '已恢复「{name}」默认设置',
            barModSettings: 'MOD设置',
            modsEmpty: '尚未注册 JS/CSS 前端插件。',
            modsLoaded: '已加载',
            modsError: '异常',
            modsUnknownAuthor: '未知作者',
            modsFoot: 'YgoModAPI v{v} · 按 F9 开关 · F8 修改器 · F10 开发者调试',
            btnClose: '关闭',
            btnApply: '应用',
            btnAdd: '添加',
            btnRemove: '移除',
            btnCopy: '复制',
            btnClear: '清空',
            btnRefresh: '刷新',
            btnEval: '执行',
            loading: '加载中...',
            copiedToast: '已复制到剪贴板',
            statusOn: '已开启',
            statusOff: '已关闭',
            shortDev: '开发者 F10',
            shortTrainer: '修改器 F8',
            shortMods: 'MOD F9',
            barDev: '开发者 F10',
            barDebugOn: '调试:开',
            barDebugOff: '调试:关',
            barTrainer: '修改器 F8',
            barMods: 'MOD F9',
            barReload: '重载',
            devTitle: '内置开发者设置 & 调试模式 (F10)',
            debugBadgeOn: '调试模式 ON',
            debugBadgeOff: '调试模式 OFF',
            devTabOverview: '概览',
            devTabSettings: '开发者设置',
            devTabDebug: '调试终端',
            devTabState: '状态与存档',
            devStatRuntime: '运行时版本',
            devStatGameVer: '原版游戏版本',
            devStatMods: '已加载 MOD',
            devStatDebug: '调试模式',
            devStatCards: '卡牌总数',
            devStatChars: '可用 / 总角色',
            devStatRelics: '遗物总数',
            devStatPacksDecks: '卡包 / 预组',
            devQuickActions: '快捷操作',
            devBtnEnableDebug: '开启调试模式',
            devBtnDisableDebug: '关闭调试模式',
            devBtnOpenTrainer: '打开内置修改器 (F8)',
            devBtnOpenMods: 'MOD 列表 (F9)',
            devBtnCopyDiag: '复制诊断 JSON',
            devBtnClearReload: '清缓存并重载',
            devRuntimeJson: '运行时诊断摘要',
            setDebugMode: '调试模式 (Debug Mode)',
            setDebugModeDesc: '开启后实时捕获 console 日志、未捕获异常、fetch 网络耗时与 MOD 事件。',
            setTraceConsole: '捕获控制台输出',
            setTraceConsoleDesc: '在调试模式下将 console.log/warn/error 镜像记录到 F10 调试终端。',
            setTraceFetch: '网络请求追踪 (Fetch Trace)',
            setTraceFetchDesc: '记录游戏内所有 fetch 请求的状态码与响应耗时 (ms)。',
            setFpsOverlay: '实时性能监视浮标 (FPS / DOM)',
            setFpsOverlayDesc: '在屏幕右上角显示实时帧率、JS 内存占用与 DOM 节点数。',
            setQuickBar: '显示左下角快捷开发条',
            setQuickBarDesc: '提供 开发者 F10 / 调试开关 / 修改器 F8 / MOD F9 / 重载 快捷入口。',
            setStartupToast: '启动时显示开发者就绪提示',
            setStartupToastDesc: '游戏载入完成时在底部弹出一次快捷键提示。',
            setLangTitle: '游戏内浮窗语言',
            setLangDesc: '默认优先支持简体中文与英语，可通过 YgoModAPI.i18n.registerLocale 扩展其他语言。',
            devBtnResetSettings: '恢复默认开发者设置',
            devResetToast: '开发者设置已恢复默认',
            devReadyToast: '开发者模式已就绪 · F10 开发者设置/调试 · F8 修改器 · F9 MOD 列表',
            debugEnabledToast: '调试模式已开启 — 正在记录日志、异常与网络请求',
            debugDisabledToast: '调试模式已关闭',
            debugStartedLog: '调试模式已激活，正在实时捕获日志、错误与网络请求。',
            debugOffBanner: '调试模式当前处于关闭状态，开启后即可实时捕获控制台日志、异常与网络请求。',
            debugOffHint: '请先开启「调试模式」以开始记录实时日志与网络请求。',
            debugNoLogs: '暂无符合条件的调试日志。',
            debugSearchPh: '过滤日志关键词...',
            debugEvalPh: '输入 JS 表达式调试，例如 YgoModDev.summary() 或 shadowTest.state()',
            filterAll: '全部',
            filterError: '错误',
            filterWarn: '警告',
            filterFetch: '网络',
            filterEvent: '事件',
            stateSubLive: '实时游戏状态',
            stateSubSave: '磁盘/IDB 存档',
            stateSubContent: '内容检索',
            stateSubMods: 'MOD 状态',
            stateSearchContentPh: '搜索卡牌/遗物中英文名或 ID...',
            stateNoContent: '尚未拦截到 content.json（请在游戏加载完成后点击刷新）。',
            trTitle: '内置实时修改器 (F8)',
            trReadyToast: '内置实时修改器已就绪 · 按 F8 打开',
            trEngineBooting: '游戏内核 (Pyodide) 正在初始化中，请等待 1-2 秒游戏主界面加载完成后再操作。',
            trLiveReadyHint: '直连游戏内核 · 所有修改即时生效并自动 SHA-256 持久化保存',
            trTabRun: '局内修改',
            trTabItems: '卡牌 / 遗物 / 卡包',
            trTabUnlocks: '全局解锁 & 调试码',
            trTabQol: '加速 / 槽位 / 备份',
            trChar: '当前角色',
            trStage: '进度 / 阶段',
            trGold: '金币',
            trLp: '生命值 LP',
            trPool: '卡池张数',
            trRelics: '遗物数量',
            trCurses: '诅咒数量',
            trShopItems: '商店在售',
            trNoActiveRun: '当前尚未开始一局冒险（全局解锁功能请切换至「全局解锁 & 调试码」标签页，点击即生效）。',
            trQuickGoldLp: '金币与生命值快捷修改',
            trShopDuelCtrl: '商店 / 诅咒 / 决斗控制',
            trExactEdit: '精确数值与轮次调整',
            trRoundLoop: '轮次 / 循环',
            trFreeShopNow: '当前商店 0 金币',
            trFreeRestock: '免费刷新商店',
            trRestockAndFree: '免费刷新 + 全 0 金币',
            trClearCurses: '清除全部诅咒',
            trInstantWin: '⚡ 直接赢得当前决斗',
            trNotInDuel: '当前不在决斗画面中',
            trDuelWon: '已直接结算本场决斗胜利！',
            trFreeShopDone: '已将商店 {n} 件商品设为 0 金币（即时生效）！',
            trRestocked: '已免费刷新商店货架！',
            trCursesCleared: '已清除本局所有诅咒！',
            trAppliedLive: '修改已实时生效并保存！',
            trAddCardSec: '检索并注入卡牌（支持原版 + MOD 卡）',
            trCardPh: '输入卡牌 ID 或中/英文名，如 黑魔术师 / Blue-Eyes / 46986414',
            trAlsoToDeck: '同时加入当前卡组',
            trAddRelicSec: '检索并注入遗物（自动触发融合遗物）',
            trRelicPh: '输入遗物 Key 或中/英文名，如 ankh / 钻石安卡 / urn',
            trAddPackSec: '发放免费卡包（立即加入待拆卡包）',
            trPackPh: '输入卡包代码或名称，如 EN-LOB / JP-DL1',
            trOwnedRelics: '当前已拥有遗物 ({n}) — 点击 × 移除',
            trCardAdded: '已向卡池注入卡牌 #{id} (x{n})！',
            trRelicAdded: '已注入遗物 {key}！',
            trRelicRemoved: '已移除遗物 {key}！',
            trPackAdded: '已发放免费卡包 {id} (x{n})！',
            trUnlockMasterTitle: '一键终极全解锁（立即生效，无需重启）',
            trUnlockMasterDesc: '一次性解锁全部角色、第二形态变体、全 LVL -1~6 挑战等级、全卡背、全融合遗物、无遗物模式、画廊/观战/沙盒与全图鉴卡牌。',
            trUnlockMasterBtn: '⚡ 一键全解锁',
            trUnlockedMasterDone: '全部角色、变体、等级、卡背与图鉴已实时解锁！',
            trUChars: '解锁全部角色',
            trUVars: '解锁全部第二形态变体',
            trULevels: '全角色通关 LVL 6',
            trUSleeves: '解锁全部卡背',
            trUFused: '解锁全部融合遗物图鉴',
            trURelicless: '解锁无遗物模式',
            trUGallery: '画廊 / CPU观战 / 沙盒',
            trUGallerySub: 'gallery + cpu + wins>=1',
            trUCollection: '全图鉴卡牌收录',
            trCardsUnit: '张已收录',
            trUnlockedDone: '已实时解锁并持久化写入存档！',
            trOfficialCodeSec: '执行原版官方调试码',
            trOfficialCodePh: '如 heartofthecards / alteregos / touchgrass / 521joeylvl6 / relic:ankh',
            trCodeApplied: '调试码 "{code}" 执行成功！',
            trQolSpeedSec: '真实动画加速与体验开关',
            trAnimMultTitle: '动画播放倍率 (Web Animations)',
            trAnimMultDesc: '实时加快界面与战斗动画播放速度（1x 为原速）。',
            trFasterAnim: '游戏内置极速动画 (Faster Animations)',
            trFasterAnimDesc: '直接修改游戏原生 settings.json 极速动画开关。',
            trSkipPacks: '跳过开包动画 (Skip Pack Animations)',
            trSkipPacksDesc: '开启后直接展示卡包结果，无需逐张翻牌。',
            trReduceMotion: '减弱画面震动与特效 (Reduce Motion)',
            trReduceMotionDesc: '关闭剧烈晃动与闪烁，提高低配设备流畅度。',
            trSpeedChanged: '动画倍率已实时调整为 {n}x',
            trSlotSaveSec: '多槽位切换与真实存档备份',
            trActiveSlot: '当前存档槽位',
            trSlotN: '槽位 {n}',
            trSlotSwitched: '已切换至存档槽位 {n}',
            trExportSave: '导出完整存档备份 JSON',
            trImportSave: '导入覆盖存档备份',
            trReloadGame: '刷新重载游戏',
            trExported: '已下载与游戏原生完全兼容的存档备份 JSON',
            trImportedReloading: '存档已写入，正在重载游戏...',
            trNoSaveData: '当前暂无可导出的存档数据',
            trInvalidNumber: '请输入有效数字',
            trInvalidCard: '请输入有效卡牌 ID 或选择联想结果',
            trInvalidRelic: '请输入遗物 Key 或名称',
            trInvalidPack: '请输入卡包 ID 或名称',
            trInvalidCode: '请输入调试码',
            trErrorPrefix: '操作失败：'
          },
          en: {
            __name: 'EN',
            modsTitle: 'Loaded MODs ({n})',
            modsTabList: 'Loaded MODs ({n})',
            modsTabSettings: 'MOD Settings ({n})',
            modsSettingsBtn: '⚙ Settings',
            modsSettingsEmpty: 'No loaded MOD has declared in-game settings yet. Authors can add settingsSchema in mod-manifest.json or call ctx.settings.define([...]) to auto-render switches, sliders & selects right here!',
            modsSettingsReset: 'Reset Defaults',
            modsSettingsResetToast: 'Reset defaults for "{name}"',
            barModSettings: 'MOD CFG',
            modsEmpty: 'No JS/CSS frontend plugins registered.',
            modsLoaded: 'Loaded',
            modsError: 'Error',
            modsUnknownAuthor: 'Unknown',
            modsFoot: 'YgoModAPI v{v} · F9 MODs · F8 Trainer · F10 Dev & Debug',
            btnClose: 'Close',
            btnApply: 'Apply',
            btnAdd: 'Add',
            btnRemove: 'Remove',
            btnCopy: 'Copy',
            btnClear: 'Clear',
            btnRefresh: 'Refresh',
            btnEval: 'Eval',
            loading: 'Loading...',
            copiedToast: 'Copied to clipboard',
            statusOn: 'ON',
            statusOff: 'OFF',
            shortDev: 'Dev F10',
            shortTrainer: 'Trainer F8',
            shortMods: 'MODs F9',
            barDev: 'DEV F10',
            barDebugOn: 'DBG:ON',
            barDebugOff: 'DBG:OFF',
            barTrainer: 'TRAINER F8',
            barMods: 'MODS F9',
            barReload: 'RELOAD',
            devTitle: 'Developer Settings & Debug Mode (F10)',
            debugBadgeOn: 'DEBUG ON',
            debugBadgeOff: 'DEBUG OFF',
            devTabOverview: 'Overview',
            devTabSettings: 'Dev Settings',
            devTabDebug: 'Debug Console',
            devTabState: 'State & Save',
            devStatRuntime: 'Runtime',
            devStatGameVer: 'Base Game',
            devStatMods: 'Loaded MODs',
            devStatDebug: 'Debug Mode',
            devStatCards: 'Cards',
            devStatChars: 'Playable / Chars',
            devStatRelics: 'Relics',
            devStatPacksDecks: 'Packs / Decks',
            devQuickActions: 'Quick Actions',
            devBtnEnableDebug: 'Enable Debug Mode',
            devBtnDisableDebug: 'Disable Debug Mode',
            devBtnOpenTrainer: 'Open Trainer (F8)',
            devBtnOpenMods: 'MOD List (F9)',
            devBtnCopyDiag: 'Copy Diagnostics JSON',
            devBtnClearReload: 'Clear Cache & Reload',
            devRuntimeJson: 'Runtime Diagnostics Summary',
            setDebugMode: 'Debug Mode',
            setDebugModeDesc: 'Capture console output, uncaught errors, fetch timing & YgoModAPI events in real time.',
            setTraceConsole: 'Capture Console Logs',
            setTraceConsoleDesc: 'Mirror console.log/warn/error into the F10 Debug Console when Debug Mode is ON.',
            setTraceFetch: 'Fetch Network Trace',
            setTraceFetchDesc: 'Record HTTP status codes and latency (ms) for all in-game fetch() requests.',
            setFpsOverlay: 'Live FPS / DOM HUD',
            setFpsOverlayDesc: 'Show live FPS, JS heap memory and DOM node count in the top-right corner.',
            setQuickBar: 'Show Bottom-Left Quick Bar',
            setQuickBarDesc: 'Show quick buttons for Dev (F10), Debug toggle, Trainer (F8), MODs (F9) & Reload.',
            setStartupToast: 'Startup Ready Toast',
            setStartupToastDesc: 'Display a shortcut reminder toast when the game finishes loading.',
            setLangTitle: 'In-Game Overlay Language',
            setLangDesc: 'Built-in Simplified Chinese & English. Community MODs can add locales via YgoModAPI.i18n.registerLocale.',
            devBtnResetSettings: 'Reset Developer Settings',
            devResetToast: 'Developer settings reset to defaults',
            devReadyToast: 'Dev Mode Ready · F10 Dev/Debug · F8 Trainer · F9 MODs',
            debugEnabledToast: 'Debug Mode ON — capturing logs, errors & network requests',
            debugDisabledToast: 'Debug Mode OFF',
            debugStartedLog: 'Debug Mode enabled. Capturing logs, uncaught errors & fetch requests.',
            debugOffBanner: 'Debug Mode is currently OFF. Turn it on to capture console logs, errors & network requests.',
            debugOffHint: 'Enable Debug Mode above to start recording live console and network logs.',
            debugNoLogs: 'No matching debug logs yet.',
            debugSearchPh: 'Filter logs...',
            debugEvalPh: 'Evaluate JS expression, e.g. YgoModDev.summary() or shadowTest.state()',
            filterAll: 'All',
            filterError: 'Errors',
            filterWarn: 'Warn',
            filterFetch: 'Fetch',
            filterEvent: 'Events',
            stateSubLive: 'Live Game State',
            stateSubSave: 'Disk / IDB Save',
            stateSubContent: 'Content Search',
            stateSubMods: 'MOD Status',
            stateSearchContentPh: 'Search card/relic name or ID...',
            stateNoContent: 'content.json not captured yet (click Refresh after the game loads).',
            trTitle: 'Built-in Live Trainer (F8)',
            trReadyToast: 'Live Trainer Ready · Press F8',
            trEngineBooting: 'Game engine (Pyodide) is still booting. Wait 1-2s until the title screen loads.',
            trLiveReadyHint: 'Live kernel bridge · All edits apply immediately with SHA-256 save persistence',
            trTabRun: 'Run Cheats',
            trTabItems: 'Cards / Relics / Packs',
            trTabUnlocks: 'Unlocks & Codes',
            trTabQol: 'Speed / Slots / Backup',
            trChar: 'Duelist',
            trStage: 'Progress / Screen',
            trGold: 'Gold',
            trLp: 'Life Points',
            trPool: 'Pool Cards',
            trRelics: 'Relics',
            trCurses: 'Curses',
            trShopItems: 'Shop Stock',
            trNoActiveRun: 'No active run yet. Start a run first (or switch to "Unlocks & Codes" for global unlocks right now).',
            trQuickGoldLp: 'Quick Gold & LP',
            trShopDuelCtrl: 'Shop / Curses / Duel',
            trExactEdit: 'Exact Values & Progress',
            trRoundLoop: 'Round / Loop',
            trFreeShopNow: 'Free Shop (0G)',
            trFreeRestock: 'Free Restock',
            trRestockAndFree: 'Restock + 0G All',
            trClearCurses: 'Clear All Curses',
            trInstantWin: '⚡ Instant Win Duel',
            trNotInDuel: 'Not currently inside a duel',
            trDuelWon: 'Duel finished as Victory!',
            trFreeShopDone: 'Set {n} shop item(s) to 0 Gold (live)!',
            trRestocked: 'Shop restocked for free!',
            trCursesCleared: 'Cleared all active curses!',
            trAppliedLive: 'Applied live and persisted!',
            trAddCardSec: 'Search & Inject Cards (Vanilla + MOD)',
            trCardPh: 'Card ID or EN/ZH name, e.g. Dark Magician / 46986414',
            trAlsoToDeck: 'Also add to active Deck',
            trAddRelicSec: 'Search & Inject Relics (auto-fuses)',
            trRelicPh: 'Relic key or name, e.g. ankh / urn / radiance_of_ra',
            trAddPackSec: 'Grant Free Booster Packs',
            trPackPh: 'Pack ID or name, e.g. EN-LOB / JP-DL1',
            trOwnedRelics: 'Owned Relics ({n}) — click × to remove',
            trCardAdded: 'Injected card #{id} (x{n})!',
            trRelicAdded: 'Added relic "{key}"!',
            trRelicRemoved: 'Removed relic "{key}"!',
            trPackAdded: 'Granted free pack {id} (x{n})!',
            trUnlockMasterTitle: 'One-Click Unlock Everything (Instant, Zero Reload)',
            trUnlockMasterDesc: 'Unlocks all duelists, second variants, LVL -1..6 clears, all sleeves, fused relics, relicless mode, gallery/CPU viewer/sandbox & full card collection.',
            trUnlockMasterBtn: '⚡ Unlock Everything',
            trUnlockedMasterDone: 'Everything unlocked and persisted live!',
            trUChars: 'Unlock All Duelists',
            trUVars: 'Unlock All Variants',
            trULevels: 'Clear All LVL 6',
            trUSleeves: 'Unlock All Sleeves',
            trUFused: 'Reveal All Fused Relics',
            trURelicless: 'Unlock Relicless Mode',
            trUGallery: 'Gallery / CPU / Sandbox',
            trUGallerySub: 'gallery + cpu + wins>=1',
            trUCollection: 'Complete Card Collection',
            trCardsUnit: 'cards',
            trUnlockedDone: 'Unlocked live and saved!',
            trOfficialCodeSec: 'Run Official Debug Code',
            trOfficialCodePh: 'e.g. heartofthecards / alteregos / touchgrass / 521joeylvl6 / relic:ankh',
            trCodeApplied: 'Debug code "{code}" executed!',
            trQolSpeedSec: 'Live Animation Speed & Game QoL',
            trAnimMultTitle: 'Animation Speed Multiplier',
            trAnimMultDesc: 'Real-time Web Animations playbackRate boost (1x = default).',
            trFasterAnim: 'Faster Animations (Native Setting)',
            trFasterAnimDesc: 'Toggles the native fasterAnimations setting in settings.json.',
            trSkipPacks: 'Skip Pack Animations',
            trSkipPacksDesc: 'Jump straight to pack results without flipping cards.',
            trReduceMotion: 'Reduce Motion',
            trReduceMotionDesc: 'Disable heavy screen shake & flashes.',
            trSpeedChanged: 'Animation speed set to {n}x',
            trSlotSaveSec: 'Run Slots & Native Save Backup',
            trActiveSlot: 'Active Slot',
            trSlotN: 'Slot {n}',
            trSlotSwitched: 'Switched to save slot {n}',
            trExportSave: 'Export Full Save Backup JSON',
            trImportSave: 'Import & Restore Save JSON',
            trReloadGame: 'Reload Game',
            trExported: 'Native-compatible save backup downloaded',
            trImportedReloading: 'Save restored! Reloading game...',
            trNoSaveData: 'No save data found yet',
            trInvalidNumber: 'Enter a valid number',
            trInvalidCard: 'Enter a valid card ID or pick a suggestion',
            trInvalidRelic: 'Enter a relic key or name',
            trInvalidPack: 'Enter a pack ID or name',
            trInvalidCode: 'Enter a debug code',
            trErrorPrefix: 'Error: '
          }
        };

        let currentLang = (() => {
          try {
            const saved = localStorage.getItem(LANG_STORAGE_KEY);
            if (saved && I18N_LOCALES[saved]) return saved;
          } catch {}
          return 'zh';
        })();

        // Real Game Storage Helper (matches pwa-storage.js IndexedDB & desktop ShadowNative)
        function idbTx(mode, op) {
          return new Promise((resolve, reject) => {
            const req = indexedDB.open(IDB_NAME, 1);
            req.onupgradeneeded = () => {
              if (!req.result.objectStoreNames.contains(IDB_STORE)) req.result.createObjectStore(IDB_STORE);
            };
            req.onerror = () => reject(req.error);
            req.onsuccess = () => {
              const db = req.result;
              const tx = db.transaction(IDB_STORE, mode);
              const r = op(tx.objectStore(IDB_STORE));
              tx.oncomplete = () => { db.close(); resolve(r.result); };
              tx.onerror = () => { db.close(); reject(tx.error || r.error); };
              tx.onabort = () => { db.close(); reject(tx.error || new Error('IDB aborted')); };
            };
          });
        }

        const saves = Object.freeze({
          async loadRaw() {
            if (window.ShadowNative?.load) {
              const raw = await window.ShadowNative.load();
              return typeof raw === 'string' && raw ? raw : '{}';
            }
            const raw = await idbTx('readonly', s => s.get(IDB_KEY));
            if (typeof raw === 'string' && raw) return raw;
            const legacy = localStorage.getItem(IDB_KEY);
            return legacy || '{}';
          },
          async saveRaw(text) {
            if (window.ShadowNative?.store) {
              return await window.ShadowNative.store(text);
            }
            await idbTx('readwrite', s => s.put(text, IDB_KEY));
            return true;
          },
          async loadParsed() {
            const raw = await saves.loadRaw();
            const bundle = JSON.parse(raw || '{}');
            const out = {};
            for (const [name, content] of Object.entries(bundle)) {
              if (name.endsWith('.bak')) continue;
              try {
                const parsed = JSON.parse(content);
                out[name] = parsed && typeof parsed === 'object' && 'payload' in parsed ? parsed.payload : parsed;
              } catch {
                out[name] = content;
              }
            }
            return out;
          }
        });

        const fetchHooks = [];
        const fetchTraceListeners = new Set();
        const eventTraceListeners = new Set();
        const uiListeners = new Set();
        const eventListeners = new Map();
        const modStatuses = [];
        let uiObserverStarted = false;
        let uiNotifyScheduled = false;

        const nativeFetch = window.fetch.bind(window);
        window.fetch = async function ygoModFetch(input, init) {
          const url = typeof input === 'string'
            ? input
            : (input && typeof input.url === 'string' ? input.url : String(input));
          const method = (init?.method || (typeof input === 'object' && input?.method) || 'GET').toUpperCase();
          const t0 = performance.now();

          let response;
          try {
            response = await nativeFetch(input, init);
          } catch (netErr) {
            const ms = Math.round(performance.now() - t0);
            for (const fn of fetchTraceListeners) {
              try { fn({ url, method, status: 'ERR', ok: false, ms }); } catch {}
            }
            throw netErr;
          }

          let current = response;
          let matchedHooks = 0;
          if (fetchHooks.length > 0) {
            for (const hook of fetchHooks) {
              try {
                const matched = typeof hook.matcher === 'function'
                  ? hook.matcher(url)
                  : hook.matcher instanceof RegExp
                    ? hook.matcher.test(url)
                    : url.includes(String(hook.matcher));
                if (!matched) continue;
                matchedHooks++;
                const next = await hook.handler(current, url, init);
                if (next instanceof Response) {
                  current = next;
                }
              } catch (err) {
                console.error(`[YgoModAPI][${hook.modId}] onFetch error for ${url}:`, err);
              }
            }
          }

          if (fetchTraceListeners.size > 0) {
            const ms = Math.round(performance.now() - t0);
            const trace = { url, method, status: current.status, ok: current.ok, ms, matchedHooks };
            for (const fn of fetchTraceListeners) {
              try { fn(trace); } catch {}
            }
          }
          return current;
        };

        function ensureUiObserver() {
          if (uiObserverStarted) return;
          uiObserverStarted = true;
          const attach = () => {
            const target = document.querySelector('#app') || document.body;
            if (!target) return;
            const notify = () => {
              uiNotifyScheduled = false;
              for (const fn of uiListeners) {
                try { fn(target); } catch (err) { console.error('[YgoModAPI] onUiChange error:', err); }
              }
            };
            const obs = new MutationObserver(() => {
              if (!uiNotifyScheduled) {
                uiNotifyScheduled = true;
                requestAnimationFrame(notify);
              }
            });
            obs.observe(target, { childList: true, subtree: true });
            requestAnimationFrame(notify);
          };
          if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', attach, { once: true });
          } else {
            attach();
          }
        }

        function showToast(message, durationMs = 2600) {
          const run = () => {
            let toast = document.querySelector('#ygo-mod-toast');
            if (!toast) {
              toast = document.createElement('div');
              toast.id = 'ygo-mod-toast';
              toast.style.cssText = [
                'position:fixed', 'left:50%', 'bottom:22px', 'transform:translateX(-50%)',
                'z-index:2147483640', 'padding:9px 16px', 'border-radius:10px',
                'background:rgba(12,14,19,0.95)', 'color:#f3f4f6',
                'border:1px solid rgba(96,165,250,0.5)', 'box-shadow:0 10px 28px rgba(0,0,0,0.55)',
                'font:600 12.5px/1.4 "Microsoft YaHei UI","Segoe UI",sans-serif',
                'pointer-events:none', 'opacity:0', 'transition:opacity 180ms ease',
                'backdrop-filter:blur(8px)'
              ].join(';');
              document.body.appendChild(toast);
            }
            toast.textContent = String(message);
            toast.style.opacity = '1';
            clearTimeout(toast._hideTimer);
            toast._hideTimer = setTimeout(() => { toast.style.opacity = '0'; }, durationMs);
          };
          if (document.body) run();
          else document.addEventListener('DOMContentLoaded', run, { once: true });
        }

        function addStyle(cssText, id) {
          const mount = () => {
            let style = id ? document.getElementById(id) : null;
            if (!style) {
              style = document.createElement('style');
              if (id) style.id = id;
              document.head.appendChild(style);
            }
            style.textContent = cssText;
          };
          if (document.head) mount();
          else document.addEventListener('DOMContentLoaded', mount, { once: true });
        }

        function addStylesheetLink(href, id) {
          const mount = () => {
            if (id && document.getElementById(id)) return;
            const link = document.createElement('link');
            if (id) link.id = id;
            link.rel = 'stylesheet';
            link.href = href;
            document.head.appendChild(link);
          };
          if (document.head) mount();
          else document.addEventListener('DOMContentLoaded', mount, { once: true });
        }

        function onDomReady(callback) {
          if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', () => callback(document), { once: true });
          } else {
            queueMicrotask(() => callback(document));
          }
        }

        function createScopedStorage(modId) {
          const prefix = `ygomod:${modId}:`;
          return Object.freeze({
            get(key, fallback = null) {
              try {
                const raw = localStorage.getItem(prefix + key);
                return raw === null ? fallback : JSON.parse(raw);
              } catch {
                return fallback;
              }
            },
            set(key, value) {
              try { localStorage.setItem(prefix + key, JSON.stringify(value)); } catch {}
            },
            remove(key) {
              try { localStorage.removeItem(prefix + key); } catch {}
            }
          });
        }

        const events = Object.freeze({
          on(event, fn) {
            if (!eventListeners.has(event)) eventListeners.set(event, new Set());
            eventListeners.get(event).add(fn);
            return () => eventListeners.get(event)?.delete(fn);
          },
          off(event, fn) {
            eventListeners.get(event)?.delete(fn);
          },
          emit(event, detail) {
            for (const fn of eventTraceListeners) {
              try { fn({ event, detail }); } catch {}
            }
            const set = eventListeners.get(event);
            if (!set) return;
            for (const fn of set) {
              try { fn(detail); } catch (err) { console.error(`[YgoModAPI] event "${event}" error:`, err); }
            }
          }
        });

        const i18n = Object.freeze({
          getLang: () => currentLang,
          listLocales: () => Object.entries(I18N_LOCALES).map(([id, d]) => ({ id, name: d.__name || id })),
          registerLocale(id, name, dict) {
            if (!id || typeof dict !== 'object') return;
            I18N_LOCALES[id] = { ...(I18N_LOCALES[id] || {}), __name: name || id, ...dict };
          },
          setLang(lang) {
            if (!I18N_LOCALES[lang]) return;
            currentLang = lang;
            try { localStorage.setItem(LANG_STORAGE_KEY, lang); } catch {}
            events.emit('i18n:change', { lang });
          },
          toggleLang() {
            const ids = Object.keys(I18N_LOCALES);
            const next = ids[(ids.indexOf(currentLang) + 1) % ids.length] || 'zh';
            i18n.setLang(next);
          },
          t(key, params) {
            const dict = I18N_LOCALES[currentLang] || I18N_LOCALES.zh;
            let text = dict[key] ?? I18N_LOCALES.en[key] ?? I18N_LOCALES.zh[key] ?? key;
            if (params && typeof params === 'object') {
              for (const [k, v] of Object.entries(params)) {
                text = text.replaceAll(`{${k}}`, String(v));
              }
            }
            return text;
          }
        });

        // ==================== IN-GAME MOD SETTINGS REGISTRY (F9 -> MOD Settings) ====================
        const modSettingsRegistry = new Map(); // modId -> { modId, modName, schema: [], values: {}, listeners: Set }

        function normalizeSettingDef(raw) {
          if (!raw || typeof raw !== 'object' || !raw.key) return null;
          const type = String(raw.type || 'boolean').toLowerCase();
          return {
            key: String(raw.key),
            type: ['boolean', 'range', 'number', 'select', 'text', 'color'].includes(type) ? type : 'text',
            label: String(raw.label || raw.key),
            labelEn: raw.labelEn ? String(raw.labelEn) : null,
            desc: raw.desc ? String(raw.desc) : '',
            descEn: raw.descEn ? String(raw.descEn) : null,
            default: raw.default !== undefined ? raw.default : (type === 'boolean' ? false : (type === 'range' || type === 'number' ? Number(raw.min ?? 0) : '')),
            min: raw.min !== undefined ? Number(raw.min) : undefined,
            max: raw.max !== undefined ? Number(raw.max) : undefined,
            step: raw.step !== undefined ? Number(raw.step) : undefined,
            unit: raw.unit ? String(raw.unit) : '',
            options: Array.isArray(raw.options) ? raw.options.map(o => typeof o === 'object' ? o : { value: o, label: String(o) }) : []
          };
        }

        function ensureModSettingsBucket(modId, modName) {
          if (!modSettingsRegistry.has(modId)) {
            let saved = {};
            try {
              const raw = localStorage.getItem(`ygomod:settings:${modId}`);
              if (raw) {
                const parsed = JSON.parse(raw);
                if (parsed && typeof parsed === 'object') saved = parsed;
              }
            } catch {}
            modSettingsRegistry.set(modId, {
              modId,
              modName: modName || modId,
              schema: [],
              values: saved,
              listeners: new Set()
            });
          } else if (modName) {
            modSettingsRegistry.get(modId).modName = modName;
          }
          return modSettingsRegistry.get(modId);
        }

        function persistModSettingsBucket(bucket) {
          try { localStorage.setItem(`ygomod:settings:${bucket.modId}`, JSON.stringify(bucket.values)); } catch {}
        }

        function setModSettingValue(modId, key, value) {
          const bucket = modSettingsRegistry.get(modId);
          if (!bucket) return;
          bucket.values[key] = value;
          persistModSettingsBucket(bucket);
          const all = getModSettingsAll(modId);
          for (const fn of bucket.listeners) {
            try { fn(key, value, all); } catch (err) { console.error(`[YgoModAPI][${modId}] settings.onChange error:`, err); }
          }
          events.emit('mod:settings', { modId, key, value, all });
        }

        function getModSettingsAll(modId) {
          const bucket = modSettingsRegistry.get(modId);
          if (!bucket) return {};
          const out = {};
          for (const def of bucket.schema) {
            out[def.key] = def.key in bucket.values ? bucket.values[def.key] : def.default;
          }
          for (const [k, v] of Object.entries(bucket.values)) {
            if (!(k in out)) out[k] = v;
          }
          return out;
        }

        function createModSettingsApi(modId, modName, initialSchema) {
          const bucket = ensureModSettingsBucket(modId, modName);
          const define = (schemaList) => {
            if (!Array.isArray(schemaList)) return;
            for (const item of schemaList) {
              const def = normalizeSettingDef(item);
              if (!def) continue;
              const existingIdx = bucket.schema.findIndex(x => x.key === def.key);
              if (existingIdx >= 0) bucket.schema[existingIdx] = def;
              else bucket.schema.push(def);
              if (!(def.key in bucket.values)) {
                bucket.values[def.key] = def.default;
              }
            }
            events.emit('mod:settings-schema', { modId, count: bucket.schema.length });
          };
          if (Array.isArray(initialSchema) && initialSchema.length > 0) define(initialSchema);

          return Object.freeze({
            define,
            getSchema: () => bucket.schema.slice(),
            get(key, fallback) {
              if (key in bucket.values) return bucket.values[key];
              const def = bucket.schema.find(x => x.key === key);
              return def ? def.default : fallback;
            },
            getAll: () => getModSettingsAll(modId),
            set: (key, value) => setModSettingValue(modId, key, value),
            reset() {
              bucket.values = {};
              for (const def of bucket.schema) bucket.values[def.key] = def.default;
              persistModSettingsBucket(bucket);
              const all = getModSettingsAll(modId);
              for (const def of bucket.schema) {
                for (const fn of bucket.listeners) {
                  try { fn(def.key, bucket.values[def.key], all); } catch {}
                }
              }
              events.emit('mod:settings', { modId, reset: true, all });
            },
            onChange(fn) {
              bucket.listeners.add(fn);
              return () => bucket.listeners.delete(fn);
            },
            open: () => openModOverlay('settings', modId)
          });
        }

        let overlayTab = 'mods';
        let overlayFocusModId = null;

        const api = {
          runtimeVersion: RUNTIME_VERSION,
          baseGameVersion: 'unknown',
          mods: [],
          statuses: modStatuses,
          i18n,
          saves,
          showToast,
          addStyle,
          onDomReady,
          onUiChange(fn) {
            uiListeners.add(fn);
            ensureUiObserver();
            return () => uiListeners.delete(fn);
          },
          onFetch(matcher, handler, modId = 'global') {
            fetchHooks.push({ matcher, handler, modId });
          },
          storage: createScopedStorage('global'),
          events,
          openOverlay: (tab) => openModOverlay(tab || 'mods', null),
          openModSettings: (modId) => openModOverlay('settings', modId || null),
          hasModSettings: () => Array.from(modSettingsRegistry.values()).some(b => b.schema.length > 0),
          getAllModSettings: () => Object.fromEntries(Array.from(modSettingsRegistry.keys()).map(id => [id, getModSettingsAll(id)])),
          openTrainer: null,
          openDevTools: null,
          _telemetry: Object.freeze({
            onFetchTrace(fn) { fetchTraceListeners.add(fn); return () => fetchTraceListeners.delete(fn); },
            onEventTrace(fn) { eventTraceListeners.add(fn); return () => eventTraceListeners.delete(fn); }
          })
        };

        window.YgoModAPI = api;

        function renderModOverlay(host, root) {
          const t = i18n.t;
          const lang = i18n.getLang();
          const settingsBuckets = Array.from(modSettingsRegistry.values()).filter(b => b.schema.length > 0);
          const totalSettings = settingsBuckets.reduce((acc, b) => acc + b.schema.length, 0);

          let bodyHtml = '';
          if (overlayTab === 'settings') {
            if (settingsBuckets.length === 0) {
              bodyHtml = `<div class="empty-msg">${escapeHtml(t('modsSettingsEmpty'))}</div>`;
            } else {
              const ordered = overlayFocusModId
                ? [...settingsBuckets.filter(b => b.modId === overlayFocusModId), ...settingsBuckets.filter(b => b.modId !== overlayFocusModId)]
                : settingsBuckets;
              bodyHtml = ordered.map(b => {
                const vals = getModSettingsAll(b.modId);
                const itemsHtml = b.schema.map(def => {
                  const lbl = lang === 'en' && def.labelEn ? def.labelEn : def.label;
                  const desc = lang === 'en' && def.descEn ? def.descEn : def.desc;
                  const val = vals[def.key];
                  let ctrl = '';
                  if (def.type === 'boolean') {
                    ctrl = `<label class="sw"><input type="checkbox" data-ms-mod="${escapeHtml(b.modId)}" data-ms-key="${escapeHtml(def.key)}" data-ms-type="boolean" ${val ? 'checked' : ''}><span class="sl"></span></label>`;
                  } else if (def.type === 'range') {
                    const mn = def.min ?? 0, mx = def.max ?? 100, st = def.step ?? 1;
                    ctrl = `<div class="rng-wrap"><input type="range" min="${mn}" max="${mx}" step="${st}" value="${escapeHtml(val)}" data-ms-mod="${escapeHtml(b.modId)}" data-ms-key="${escapeHtml(def.key)}" data-ms-type="range"><span class="rng-val" data-ms-val="${escapeHtml(b.modId)}:${escapeHtml(def.key)}">${escapeHtml(val)}${escapeHtml(def.unit)}</span></div>`;
                  } else if (def.type === 'number') {
                    ctrl = `<input class="inp num" type="number" ${def.min !== undefined ? `min="${def.min}"` : ''} ${def.max !== undefined ? `max="${def.max}"` : ''} ${def.step !== undefined ? `step="${def.step}"` : ''} value="${escapeHtml(val)}" data-ms-mod="${escapeHtml(b.modId)}" data-ms-key="${escapeHtml(def.key)}" data-ms-type="number">`;
                  } else if (def.type === 'select') {
                    const opts = def.options.map(o => {
                      const ol = lang === 'en' && o.labelEn ? o.labelEn : (o.label || o.value);
                      return `<option value="${escapeHtml(o.value)}" ${String(val) === String(o.value) ? 'selected' : ''}>${escapeHtml(ol)}</option>`;
                    }).join('');
                    ctrl = `<select class="inp" data-ms-mod="${escapeHtml(b.modId)}" data-ms-key="${escapeHtml(def.key)}" data-ms-type="select">${opts}</select>`;
                  } else if (def.type === 'color') {
                    ctrl = `<input type="color" class="clr" value="${escapeHtml(val || '#60a5fa')}" data-ms-mod="${escapeHtml(b.modId)}" data-ms-key="${escapeHtml(def.key)}" data-ms-type="color">`;
                  } else {
                    ctrl = `<input class="inp" type="text" value="${escapeHtml(val)}" data-ms-mod="${escapeHtml(b.modId)}" data-ms-key="${escapeHtml(def.key)}" data-ms-type="text">`;
                  }
                  return `<div class="s-row"><div class="s-info"><div class="s-lbl">${escapeHtml(lbl)}</div>${desc ? `<div class="s-desc">${escapeHtml(desc)}</div>` : ''}</div><div class="s-ctrl">${ctrl}</div></div>`;
                }).join('');

                return `<div class="s-group">
                  <div class="s-ghead">
                    <div><span class="name">${escapeHtml(b.modName)}</span> <span class="ver">${escapeHtml(b.modId)}</span></div>
                    <button class="sm" data-ms-reset="${escapeHtml(b.modId)}">${escapeHtml(t('modsSettingsReset'))}</button>
                  </div>
                  ${itemsHtml}
                </div>`;
              }).join('');
            }
          } else {
            bodyHtml = modStatuses.map(m => {
              const hasCfg = (modSettingsRegistry.get(m.id)?.schema.length || 0) > 0;
              return `<div class="row">
                <div style="min-width:0">
                  <div class="name">${escapeHtml(m.name)} <span class="ver">v${escapeHtml(m.version)}</span></div>
                  <div class="meta">${escapeHtml(m.id)} · ${escapeHtml(m.author || t('modsUnknownAuthor'))}</div>
                  ${m.error ? `<div class="err">${escapeHtml(m.error)}</div>` : ''}
                </div>
                <div class="btns">
                  ${hasCfg ? `<button class="purple" data-open-ms="${escapeHtml(m.id)}">${escapeHtml(t('modsSettingsBtn'))}</button>` : ''}
                  <span class="badge ${m.ok ? 'ok' : 'bad'}">${escapeHtml(m.ok ? t('modsLoaded') : t('modsError'))}</span>
                </div>
              </div>`;
            }).join('') || `<div class="meta">${escapeHtml(t('modsEmpty'))}</div>`;
          }

          root.innerHTML = `<style>
            *{box-sizing:border-box}
            .backdrop{position:fixed;inset:0;z-index:2147483646;background:rgba(5,7,11,0.78);backdrop-filter:blur(4px);display:flex;align-items:center;justify-content:center;font:13px/1.45 "Microsoft YaHei UI","Segoe UI",system-ui,sans-serif;color:#e5e7eb}
            .panel{width:min(660px,94vw);max-height:84vh;display:flex;flex-direction:column;background:#0c0e13;border:1px solid #262b36;border-radius:14px;box-shadow:0 24px 64px rgba(0,0,0,0.7);overflow:hidden}
            .head{display:flex;justify-content:space-between;align-items:center;gap:8px;padding:12px 16px;background:#11141c;border-bottom:1px solid #232834}
            h3{margin:0;color:#f3f4f6;font-size:14.5px;font-weight:700}
            .tabs{display:flex;gap:4px;padding:7px 16px 0;background:#11141c;border-bottom:1px solid #232834}
            .tab{background:transparent;border:none;border-bottom:2px solid transparent;color:#9ca3af;padding:6px 12px;font-size:12.5px;font-weight:600;cursor:pointer;border-radius:6px 6px 0 0}
            .tab:hover{color:#e5e7eb;background:rgba(255,255,255,0.04)}
            .tab.active{color:#c084fc;border-bottom-color:#a855f7;background:rgba(168,85,247,0.09)}
            .btns{display:flex;align-items:center;gap:6px}
            button{background:#1a1f2c;border:1px solid #2d3446;color:#e5e7eb;border-radius:7px;padding:4px 9px;font-size:11.5px;font-weight:600;cursor:pointer}
            button:hover{background:#242b3c}
            button.sm{padding:3px 8px;font-size:11px}
            button.gold{background:rgba(217,119,6,0.2);border-color:rgba(245,158,11,0.45);color:#fcd34d}
            button.blue{background:rgba(37,99,235,0.2);border-color:rgba(96,165,250,0.45);color:#93c5fd}
            button.purple{background:rgba(168,85,247,0.2);border-color:rgba(192,132,252,0.45);color:#d8b4fe}
            .list{padding:12px 16px;overflow:auto;display:flex;flex-direction:column;gap:9px;min-height:220px}
            .row{display:flex;justify-content:space-between;align-items:center;gap:12px;padding:9px 11px;background:#131722;border-radius:10px;border:1px solid #222733}
            .name{font-weight:600;color:#f8fafc}.ver{color:#9ca3af;font-weight:400;font-size:11.5px}.meta{color:#9ca3af;font-size:11.5px;margin-top:2px}
            .err{color:#f87171;font-size:11.5px;margin-top:4px;word-break:break-all}
            .badge{font-size:11px;font-weight:600;padding:3px 8px;border-radius:999px;white-space:nowrap}
            .ok{background:rgba(34,197,94,0.16);color:#4ade80;border:1px solid rgba(34,197,94,0.35)}
            .bad{background:rgba(239,68,68,0.16);color:#fca5a5;border:1px solid rgba(239,68,68,0.35)}
            .s-group{background:#121621;border:1px solid #242a38;border-radius:11px;padding:10px 12px;display:flex;flex-direction:column;gap:8px}
            .s-ghead{display:flex;justify-content:space-between;align-items:center;padding-bottom:6px;border-bottom:1px solid #1f2533}
            .s-row{display:flex;justify-content:space-between;align-items:center;gap:14px;padding:4px 0}
            .s-info{min-width:0;flex:1}
            .s-lbl{font-size:12.5px;font-weight:600;color:#f1f5f9}
            .s-desc{font-size:11px;color:#94a3b8;margin-top:1px}
            .s-ctrl{flex-shrink:0;display:flex;align-items:center;gap:6px}
            .inp{background:#090b10;border:1px solid #2b3244;color:#f3f4f6;border-radius:7px;padding:5px 8px;font:12px "Microsoft YaHei UI",monospace;min-width:130px}
            .inp.num{width:96px;min-width:0;text-align:right}
            .rng-wrap{display:flex;align-items:center;gap:8px}
            .rng-wrap input[type=range]{width:128px;accent-color:#a855f7;cursor:pointer}
            .rng-val{min-width:44px;text-align:right;font:600 12px ui-monospace,Consolas,monospace;color:#d8b4fe}
            .clr{width:38px;height:26px;background:#090b10;border:1px solid #2b3244;border-radius:6px;cursor:pointer}
            .sw{position:relative;display:inline-block;width:38px;height:21px}
            .sw input{opacity:0;width:0;height:0}
            .sl{position:absolute;cursor:pointer;inset:0;background:#2b3140;border-radius:999px;transition:.18s}
            .sl:before{position:absolute;content:"";height:15px;width:15px;left:3px;bottom:3px;background:#e5e7eb;border-radius:50%;transition:.18s}
            .sw input:checked+.sl{background:#9333ea}
            .sw input:checked+.sl:before{transform:translateX(17px)}
            .empty-msg{padding:22px 12px;color:#94a3b8;font-size:12.5px;line-height:1.6;text-align:center}
            .foot{color:#64748b;font-size:11.5px;padding:9px 16px;border-top:1px solid #1f2430;background:#10131a}
          </style>
          <div class="backdrop">
            <div class="panel">
              <div class="head">
                <h3>${escapeHtml(t('modsTitle', { n: modStatuses.length }))}</h3>
                <div class="btns">
                  <button id="mLang">${lang === 'zh' ? 'EN' : '中文'}</button>
                  ${api.openTrainer ? `<button class="gold" id="mTrainer">${escapeHtml(t('shortTrainer'))}</button>` : ''}
                  ${api.openDevTools ? `<button class="blue" id="mDev">${escapeHtml(t('shortDev'))}</button>` : ''}
                  <button id="mClose">${escapeHtml(t('btnClose'))} (F9)</button>
                </div>
              </div>
              <div class="tabs">
                <button class="tab ${overlayTab === 'mods' ? 'active' : ''}" data-mtab="mods">${escapeHtml(t('modsTabList', { n: modStatuses.length }))}</button>
                <button class="tab ${overlayTab === 'settings' ? 'active' : ''}" data-mtab="settings">${escapeHtml(t('modsTabSettings', { n: totalSettings }))}</button>
              </div>
              <div class="list">${bodyHtml}</div>
              <div class="foot">${escapeHtml(t('modsFoot', { v: RUNTIME_VERSION }))}</div>
            </div>
          </div>`;

          root.querySelector('#mClose').onclick = () => host.remove();
          root.querySelector('#mLang').onclick = () => i18n.toggleLang();
          const bt = root.querySelector('#mTrainer');
          if (bt) bt.onclick = () => { host.remove(); api.openTrainer?.(); };
          const bd = root.querySelector('#mDev');
          if (bd) bd.onclick = () => { host.remove(); api.openDevTools?.(); };
          root.querySelector('.backdrop').onclick = e => { if (e.target === e.currentTarget) host.remove(); };

          root.querySelectorAll('[data-mtab]').forEach(btn => {
            btn.onclick = () => { overlayTab = btn.dataset.mtab; renderModOverlay(host, root); };
          });
          root.querySelectorAll('[data-open-ms]').forEach(btn => {
            btn.onclick = () => { overlayTab = 'settings'; overlayFocusModId = btn.dataset.openMs; renderModOverlay(host, root); };
          });
          root.querySelectorAll('[data-ms-reset]').forEach(btn => {
            btn.onclick = () => {
              const id = btn.dataset.msReset;
              const b = modSettingsRegistry.get(id);
              if (!b) return;
              createModSettingsApi(id, b.modName).reset();
              showToast(t('modsSettingsResetToast', { name: b.modName }), 1800);
              renderModOverlay(host, root);
            };
          });
          root.querySelectorAll('[data-ms-key]').forEach(el => {
            const modId = el.dataset.msMod;
            const key = el.dataset.msKey;
            const kind = el.dataset.msType;
            const applyVal = () => {
              let v;
              if (kind === 'boolean') v = el.checked;
              else if (kind === 'range' || kind === 'number') {
                v = Number(el.value);
                if (!Number.isFinite(v)) return;
              } else {
                v = el.value;
              }
              if (kind === 'range') {
                const lblEl = root.querySelector(`[data-ms-val="${CSS.escape(modId + ':' + key)}"]`);
                const def = modSettingsRegistry.get(modId)?.schema.find(x => x.key === key);
                if (lblEl) lblEl.textContent = `${v}${def?.unit || ''}`;
              }
              setModSettingValue(modId, key, v);
            };
            if (kind === 'range' || kind === 'text' || kind === 'color') el.oninput = applyVal;
            el.onchange = applyVal;
          });
        }

        function openModOverlay(tab = 'mods', focusModId = null) {
          const existing = document.getElementById('ygo-mod-overlay');
          if (existing && !focusModId && tab === overlayTab) {
            existing.remove();
            return;
          }
          overlayTab = tab;
          overlayFocusModId = focusModId;
          if (existing?._rerender) {
            existing._rerender();
            return;
          }
          const host = document.createElement('div');
          host.id = 'ygo-mod-overlay';
          const root = host.attachShadow({ mode: 'closed' });
          host._rerender = () => renderModOverlay(host, root);
          renderModOverlay(host, root);
          document.body.appendChild(host);
        }

        function toggleModOverlay() {
          openModOverlay('mods', null);
        }

        events.on('i18n:change', () => {
          const existing = document.getElementById('ygo-mod-overlay');
          if (existing?._rerender) existing._rerender();
        });

        function escapeHtml(text) {
          return String(text ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
        }

        addEventListener('keydown', e => {
          if (e.key === 'F9' && !e.repeat) {
            e.preventDefault();
            toggleModOverlay();
          } else if (e.key === 'Escape') {
            const existing = document.getElementById('ygo-mod-overlay');
            if (existing) existing.remove();
          }
        }, true);

        async function bootModRuntime() {
          let registry;
          try {
            const resp = await nativeFetch(REGISTRY_URL, { cache: 'no-store' });
            if (!resp.ok) return;
            registry = await resp.json();
          } catch (err) {
            console.warn('[YgoModAPI] Could not load mods-registry.json:', err);
            return;
          }

          api.baseGameVersion = registry.baseGameVersion || 'unknown';
          api.mods = Object.freeze(registry.mods || []);

          for (const mod of api.mods) {
            const status = {
              id: mod.id,
              name: mod.name,
              version: mod.version,
              author: mod.author,
              ok: true,
              error: null
            };
            modStatuses.push(status);

            try {
              for (let i = 0; i < (mod.cssEntries || []).length; i++) {
                const href = mod.cssEntries[i];
                addStylesheetLink(href, `ygo-mod-css-${mod.id}-${i}`);
              }

              const modSettings = createModSettingsApi(mod.id, mod.name, mod.settingsSchema);

              const ctx = Object.freeze({
                mod: Object.freeze({ ...mod }),
                config: mod.config || {},
                settings: modSettings,
                api,
                i18n,
                saves,
                storage: createScopedStorage(mod.id),
                showToast,
                addStyle: (css, suffix = 'inline') => addStyle(css, `ygo-mod-style-${mod.id}-${suffix}`),
                onDomReady,
                onUiChange: api.onUiChange,
                onFetch: (matcher, handler) => api.onFetch(matcher, handler, mod.id),
                events,
                log: (...args) => console.log(`[MOD:${mod.id}]`, ...args),
                warn: (...args) => console.warn(`[MOD:${mod.id}]`, ...args),
                error: (...args) => console.error(`[MOD:${mod.id}]`, ...args)
              });

              for (const entryPath of (mod.jsEntries || [])) {
                const moduleNs = await import('./' + entryPath.replace(/^\.?\//, ''));
                const initFn = typeof moduleNs.init === 'function'
                  ? moduleNs.init
                  : (typeof moduleNs.default === 'function' ? moduleNs.default : null);
                if (initFn) {
                  await initFn(ctx);
                }
              }
            } catch (err) {
              status.ok = false;
              status.error = err?.message || String(err);
              console.error(`[YgoModAPI] Failed to load MOD "${mod.id}":`, err);
            }
          }

          events.emit('runtime:ready', { mods: api.mods, statuses: modStatuses });
        }

        await bootModRuntime();
        """;
}
