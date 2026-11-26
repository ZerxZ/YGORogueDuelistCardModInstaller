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
}

/// <summary>
/// Generates the CSP-safe in-game ES Module runtime (mod-runtime.js), mods-registry.json,
/// optional F8 Trainer overlay (mod-trainer.js), and injects the module bootstrap tag into index.html.
/// 100% Native AOT compatible (source-generated JSON only).
/// </summary>
public static class ModRuntimeGenerator
{
    public const string RuntimeScriptPath = "mod-runtime.js";
    public const string RegistryJsonPath = "mods-registry.json";
    public const string TrainerOverlayScriptPath = "mod-trainer-overlay.js";
    public const string DevToolsScriptPath = "mod-dev-tools.js";
    public const string RuntimeVersion = "1.2.0";

    public static string BuildDevToolsScript() => """
        // Built-in Developer Mode Tools (mod-dev-tools.js) - active when Developer Mode is enabled
        // Exposes window.YgoModDev, F10 Dev Inspector, and floating DEV quick bar.
        export function init(ctx) {
          window.__YGO_MOD_DEV__ = true;
          let lastContent = null;

          ctx.onFetch(/(?:^|\/)content\.json(?:\?|$)/, async (resp) => {
            try { lastContent = await resp.clone().json(); } catch {}
            return resp;
          });

          const devApi = Object.freeze({
            getMods: () => ctx.api.mods,
            getStatuses: () => ctx.api.statuses,
            getContent: () => lastContent,
            openModsOverlay: () => ctx.api.openOverlay(),
            reload: () => location.reload(),
            summary() {
              return {
                runtimeVersion: ctx.api.runtimeVersion,
                baseGameVersion: ctx.api.baseGameVersion,
                loadedMods: ctx.api.mods.length,
                cards: lastContent?.cards?.length ?? 0,
                characters: lastContent?.characters?.length ?? 0,
                playable: lastContent?.playable?.length ?? 0,
                packs: lastContent?.packs?.length ?? 0,
                preconstructedDecks: lastContent?.preconstructedDecks?.length ?? 0,
                relics: lastContent?.artifacts ? Object.keys(lastContent.artifacts).length : 0
              };
            }
          });
          window.YgoModDev = devApi;

          function openInspector() {
            const existing = document.getElementById('ygo-mod-dev-inspector');
            if (existing) { existing.remove(); return; }
            const host = document.createElement('div');
            host.id = 'ygo-mod-dev-inspector';
            const root = host.attachShadow({ mode: 'closed' });
            const info = JSON.stringify(devApi.summary(), null, 2);
            root.innerHTML = `<style>
              .bg{position:fixed;inset:0;z-index:2147483644;background:rgba(4,10,18,0.72);display:flex;align-items:center;justify-content:center;font:13px/1.45 "Microsoft YaHei UI",monospace}
              .panel{width:min(560px,94vw);background:#0c1320;color:#e2e8f0;border:1px solid #38bdf8;border-radius:12px;padding:16px 18px}
              h3{margin:0 0 10px;color:#38bdf8}
              pre{background:#060a12;padding:10px;border-radius:8px;overflow:auto;max-height:50vh;color:#7dd3fc}
              .row{display:flex;gap:8px;justify-content:flex-end;margin-top:10px}
              button{background:#1e293b;border:1px solid #475569;color:#f8fafc;border-radius:6px;padding:6px 12px;cursor:pointer}
            </style>
            <div class="bg"><div class="panel">
              <h3>MOD Developer Inspector (F10)</h3>
              <pre>${info}</pre>
              <div class="row">
                <button id="mods">MODs (F9)</button>
                <button id="reload">Reload</button>
                <button id="close">Close</button>
              </div>
            </div></div>`;
            root.querySelector('#close').onclick = () => host.remove();
            root.querySelector('#mods').onclick = () => { host.remove(); ctx.api.openOverlay(); };
            root.querySelector('#reload').onclick = () => location.reload();
            root.querySelector('.bg').onclick = e => { if (e.target === e.currentTarget) host.remove(); };
            document.body.appendChild(host);
          }

          window.addEventListener('keydown', (e) => {
            if (e.key === 'F10' && !e.repeat) {
              e.preventDefault();
              openInspector();
            }
          }, true);

          ctx.onDomReady(() => {
            if (document.getElementById('ygo-dev-pill')) return;
            const pill = document.createElement('div');
            pill.id = 'ygo-dev-pill';
            pill.style.cssText = 'position:fixed;left:10px;bottom:10px;z-index:2147483635;display:flex;gap:6px;font:600 11px/1 monospace';
            const mkBtn = (label, fn) => {
              const b = document.createElement('button');
              b.textContent = label;
              b.style.cssText = 'background:rgba(12,24,38,0.88);color:#38bdf8;border:1px solid #38bdf8;border-radius:6px;padding:4px 8px;cursor:pointer';
              b.onclick = fn;
              return b;
            };
            pill.appendChild(mkBtn('DEV F10', openInspector));
            pill.appendChild(mkBtn('MODS F9', () => ctx.api.openOverlay()));
            pill.appendChild(mkBtn('RELOAD', () => location.reload()));
            document.body.appendChild(pill);
            console.info('[YgoModDev] Developer Mode active. Inspect via window.YgoModDev or press F10.');
          });
        }
        """;

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

    public static string BuildTrainerOverlayScript() => """
        // Built-in F8 In-Game Trainer / Modifier Overlay (mod-trainer-overlay.js)
        // Edits active campaign save safely (gold, LP, cards, relics, unlocks) via ShadowNative or localStorage.
        export function init(ctx) {
          let cachedContent = null;
          ctx.onFetch(/(?:^|\/)content\.json(?:\?|$)/, async (resp) => {
            try { cachedContent = await resp.clone().json(); } catch {}
            return resp;
          });

          async function loadCampaignJson() {
            if (window.ShadowNative?.load) {
              const raw = await window.ShadowNative.load();
              return raw ? JSON.parse(raw) : {};
            }
            const raw = localStorage.getItem('campaign') || localStorage.getItem('ygo-campaign');
            return raw ? JSON.parse(raw) : {};
          }

          async function saveCampaignJson(obj) {
            const text = JSON.stringify(obj);
            if (window.ShadowNative?.store) {
              await window.ShadowNative.store(text);
              return true;
            }
            localStorage.setItem('campaign', text);
            return true;
          }

          function findActiveRun(campaign) {
            for (const key of ['run', 'run.json', 'activeRun']) {
              if (campaign && campaign[key] && typeof campaign[key] === 'object') return campaign[key];
            }
            if (campaign?.files) {
              for (const k of ['run.json', 'run-2.json', 'run-3.json']) {
                const entry = campaign.files[k];
                if (entry?.payload) return entry.payload;
                if (entry && typeof entry === 'object' && 'gold' in entry) return entry;
              }
            }
            return null;
          }

          function openTrainer() {
            const existing = document.getElementById('ygo-mod-trainer');
            if (existing) { existing.remove(); return; }

            const host = document.createElement('div');
            host.id = 'ygo-mod-trainer';
            const root = host.attachShadow({ mode: 'closed' });
            root.innerHTML = `<style>
              .bg{position:fixed;inset:0;z-index:2147483645;background:rgba(4,10,18,0.72);display:flex;align-items:center;justify-content:center;font:13px/1.45 "Microsoft YaHei UI","Segoe UI",sans-serif}
              .box{width:min(520px,94vw);background:#101826;color:#ecf1f8;border:1px solid #c9a227;border-radius:12px;padding:16px 18px;box-shadow:0 16px 40px rgba(0,0,0,0.6)}
              .top{display:flex;justify-content:space-between;align-items:center;margin-bottom:10px}
              h3{margin:0;color:#f5d061;font-size:16px}
              .row{display:flex;gap:8px;align-items:center;margin:8px 0}
              label{width:110px;color:#94a3b8;font-size:12px}
              input{flex:1;background:#0b111b;color:#f8fafc;border:1px solid #334155;border-radius:6px;padding:6px 8px;font:13px monospace}
              button{background:#2563eb;color:#fff;border:none;border-radius:6px;padding:6px 12px;font-weight:600;cursor:pointer}
              button.sec{background:#1e293b;border:1px solid #475569;color:#e2e8f0}
              button.ok{background:#15803d}
              .msg{margin-top:8px;font-size:12px;color:#4ade80;min-height:18px}
              .note{margin-top:6px;font-size:11.5px;color:#64748b}
            </style>
            <div class="bg">
              <div class="box">
                <div class="top">
                  <h3>MOD Trainer / 修改器 (F8)</h3>
                  <button class="sec" id="close">Close</button>
                </div>
                <div class="row"><label>Gold / 金币</label><input id="gold" type="number" placeholder="+1000 / 9999"><button id="setGold">Apply</button></div>
                <div class="row"><label>LP / 生命值</label><input id="lp" type="number" placeholder="16000"><button id="setLp">Apply</button></div>
                <div class="row"><label>Add Card ID</label><input id="cardId" type="number" placeholder="e.g. 46986414 / 190099001"><button id="addCard">Add</button></div>
                <div class="row"><label>Add Relic Key</label><input id="relicKey" type="text" placeholder="e.g. urn / radiance_of_ra"><button id="addRelic">Add</button></div>
                <div class="row">
                  <button class="ok" id="reload">Save &amp; Reload / 保存并重载</button>
                  <button class="sec" id="speed">Toggle 2x Anim</button>
                </div>
                <div class="msg" id="msg"></div>
                <div class="note">Static rules in trainer.json (unlock all, free shop, starting gold/LP) apply automatically on new runs. Press F8 to toggle.</div>
              </div>
            </div>`;

            const msg = root.querySelector('#msg');
            const setMsg = (t, err = false) => { msg.style.color = err ? '#f87171' : '#4ade80'; msg.textContent = t; };
            root.querySelector('#close').onclick = () => host.remove();
            root.querySelector('.bg').onclick = (e) => { if (e.target === e.currentTarget) host.remove(); };

            async function mutateRun(fn) {
              try {
                const camp = await loadCampaignJson();
                const run = findActiveRun(camp);
                if (!run) { setMsg('No active run found. Start a run first, or use trainer.json new-run hooks.', true); return; }
                fn(run);
                await saveCampaignJson(camp);
                setMsg('Saved to campaign! Click "Save & Reload" to refresh UI.');
              } catch (e) {
                setMsg('Error: ' + (e?.message || e), true);
              }
            }

            root.querySelector('#setGold').onclick = () => {
              const v = Number(root.querySelector('#gold').value);
              if (!Number.isFinite(v) || v < 0) return setMsg('Invalid gold value', true);
              mutateRun(r => { r.gold = Math.floor(v); });
            };
            root.querySelector('#setLp').onclick = () => {
              const v = Number(root.querySelector('#lp').value);
              if (!Number.isFinite(v) || v <= 0) return setMsg('Invalid LP value', true);
              mutateRun(r => { r.lp = Math.floor(v); });
            };
            root.querySelector('#addCard').onclick = () => {
              const id = Number(root.querySelector('#cardId').value);
              if (!Number.isFinite(id) || id <= 0) return setMsg('Invalid card ID', true);
              mutateRun(r => { r.pool = Array.isArray(r.pool) ? r.pool : []; r.pool.push(Math.floor(id)); });
            };
            root.querySelector('#addRelic').onclick = () => {
              const key = root.querySelector('#relicKey').value.trim();
              if (!key) return setMsg('Enter a relic key', true);
              mutateRun(r => {
                r.artifacts = Array.isArray(r.artifacts) ? r.artifacts : [];
                if (!r.artifacts.includes(key)) r.artifacts.push(key);
              });
            };
            root.querySelector('#reload').onclick = () => location.reload();
            root.querySelector('#speed').onclick = () => {
              const cur = Number(localStorage.getItem('ygo_anim_speed') || '1');
              const next = cur >= 2 ? 1 : 2;
              localStorage.setItem('ygo_anim_speed', String(next));
              setMsg(`Animation speed preference set to ${next}x`);
            };

            document.body.appendChild(host);
          }

          window.addEventListener('keydown', (e) => {
            if (e.key === 'F8' && !e.repeat) {
              e.preventDefault();
              openTrainer();
            }
          }, true);

          ctx.onDomReady(() => {
            if (ctx.config?.showTrainerHint !== false) {
              ctx.showToast('Trainer Ready · Press F8 / 修改器已就绪（按 F8 打开）', 2800);
            }
          });
        }
        """;

    public static string BuildRuntimeScript() => """
        // Yu-Gi-Oh! Rogue Duelist - In-Game JS Mod Runtime (v1.1.0)
        // Automatically injected by YGORogueDuelistCardModInstaller when JS/CSS/Trainer mods are enabled.
        // Exposes window.YgoModAPI and loads every mod's styles & ES modules in load order.

        const RUNTIME_VERSION = '1.1.0';
        const REGISTRY_URL = './mods-registry.json';

        const fetchHooks = [];
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

          const response = await nativeFetch(input, init);
          if (fetchHooks.length === 0) {
            return response;
          }

          let current = response;
          for (const hook of fetchHooks) {
            try {
              const matched = typeof hook.matcher === 'function'
                ? hook.matcher(url)
                : hook.matcher instanceof RegExp
                  ? hook.matcher.test(url)
                  : url.includes(String(hook.matcher));
              if (!matched) continue;

              const next = await hook.handler(current, url, init);
              if (next instanceof Response) {
                current = next;
              }
            } catch (err) {
              console.error(`[YgoModAPI][${hook.modId}] onFetch error for ${url}:`, err);
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
                'z-index:2147483640', 'padding:9px 16px', 'border-radius:8px',
                'background:rgba(12,24,36,0.94)', 'color:#f5ecd6',
                'border:1px solid #c9a227', 'box-shadow:0 8px 24px rgba(0,0,0,0.45)',
                'font:600 13px/1.4 "Microsoft YaHei UI","Segoe UI",sans-serif',
                'pointer-events:none', 'opacity:0', 'transition:opacity 180ms ease'
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
            const set = eventListeners.get(event);
            if (!set) return;
            for (const fn of set) {
              try { fn(detail); } catch (err) { console.error(`[YgoModAPI] event "${event}" error:`, err); }
            }
          }
        });

        const api = {
          runtimeVersion: RUNTIME_VERSION,
          baseGameVersion: 'unknown',
          mods: [],
          statuses: modStatuses,
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
          openOverlay: toggleModOverlay
        };

        window.YgoModAPI = api;

        function toggleModOverlay() {
          const existing = document.getElementById('ygo-mod-overlay');
          if (existing) {
            existing.remove();
            return;
          }
          const host = document.createElement('div');
          host.id = 'ygo-mod-overlay';
          const root = host.attachShadow({ mode: 'closed' });
          const rows = modStatuses.map(m => `
            <div class="row">
              <div>
                <div class="name">${escapeHtml(m.name)} <span class="ver">v${escapeHtml(m.version)}</span></div>
                <div class="meta">${escapeHtml(m.id)} · ${escapeHtml(m.author || 'Unknown')}</div>
                ${m.error ? `<div class="err">${escapeHtml(m.error)}</div>` : ''}
              </div>
              <span class="badge ${m.ok ? 'ok' : 'bad'}">${m.ok ? 'Loaded' : 'Error'}</span>
            </div>`).join('');
          root.innerHTML = `<style>
            .backdrop{position:fixed;inset:0;z-index:2147483646;background:rgba(4,10,18,0.72);display:flex;align-items:center;justify-content:center;font:13px/1.45 "Microsoft YaHei UI","Segoe UI",sans-serif}
            .panel{width:min(540px,92vw);max-height:78vh;overflow:auto;background:#101926;color:#ecf1f8;border:1px solid #c9a227;border-radius:12px;padding:16px 18px;box-shadow:0 16px 40px rgba(0,0,0,0.6)}
            .head{display:flex;justify-content:space-between;align-items:center;margin-bottom:12px}
            h3{margin:0;color:#f5d061;font-size:16px}
            .close{background:transparent;border:1px solid #475569;color:#e2e8f0;border-radius:6px;padding:4px 10px;cursor:pointer}
            .row{display:flex;justify-content:space-between;align-items:center;gap:12px;padding:9px 10px;margin-bottom:8px;background:#162233;border-radius:8px;border:1px solid #26354d}
            .name{font-weight:600}.ver{color:#94a3b8;font-weight:400;font-size:12px}.meta{color:#94a3b8;font-size:11.5px;margin-top:2px}
            .err{color:#f87171;font-size:11.5px;margin-top:4px;word-break:break-all}
            .badge{font-size:11px;font-weight:600;padding:3px 8px;border-radius:999px;white-space:nowrap}
            .ok{background:#14532d;color:#86efac}.bad{background:#7f1d1d;color:#fecaca}
            .foot{color:#64748b;font-size:11.5px;margin-top:6px}
          </style>
          <div class="backdrop">
            <div class="panel">
              <div class="head">
                <h3>Loaded MODs (${modStatuses.length})</h3>
                <button class="close">Close (F9)</button>
              </div>
              ${rows || '<div class="meta">No JS/CSS plugins registered.</div>'}
              <div class="foot">YgoModAPI v${RUNTIME_VERSION} · Press F9 to toggle</div>
            </div>
          </div>`;
          root.querySelector('.close').onclick = () => host.remove();
          root.querySelector('.backdrop').onclick = e => { if (e.target === e.currentTarget) host.remove(); };
          document.body.appendChild(host);
        }

        function escapeHtml(text) {
          return String(text ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
        }

        addEventListener('keydown', e => {
          if (e.key === 'F9' && !e.repeat) {
            e.preventDefault();
            toggleModOverlay();
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

              const ctx = Object.freeze({
                mod: Object.freeze({ ...mod }),
                config: mod.config || {},
                api,
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
