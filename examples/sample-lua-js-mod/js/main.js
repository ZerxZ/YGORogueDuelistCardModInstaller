// 示例 MOD 主入口（ES Module）
// 由 mod-runtime.js 在 web-boot.js 之前加载，可提前拦截 fetch 并挂载全局事件/UI。
import { mountHudBadge } from './hud.js';

const SAMPLE_CARD_ID = 190099001;

export async function init(ctx) {
  // 1. 持久化计数（自动按 modId 隔离在 localStorage）
  const launchCount = Number(ctx.storage.get('launchCount', 0)) + 1;
  ctx.storage.set('launchCount', launchCount);

  const state = {
    visible: ctx.config?.showHudBadge !== false && ctx.storage.get('hudVisible', true),
    customCardDetected: false
  };

  ctx.log(`已启动第 ${launchCount} 次，游戏原版版本=${ctx.api.baseGameVersion}`);

  // 2. Fetch 钩子：在游戏加载 content.json 时检测自制卡是否存在（不修改 mobile.js）
  ctx.onFetch(/(?:^|\/)content\.json(?:\?|$)/, async (response) => {
    try {
      const cloned = response.clone();
      const data = await cloned.json();
      const found = Array.isArray(data?.cards) && data.cards.some(c => Number(c.id) === SAMPLE_CARD_ID);
      state.customCardDetected = found;
      ctx.log(`检测到 content.json 共 ${data?.cards?.length ?? 0} 张卡，示例卡 ${SAMPLE_CARD_ID}=${found}`);
      ctx.events.emit('sample-mod:content-checked', { found, totalCards: data?.cards?.length ?? 0 });
      if (document.body) {
        mountHudBadge(ctx, state);
      }
    } catch (err) {
      ctx.warn('读取 content.json 失败：', err);
    }
    return response;
  });

  // 3. DOM 就绪后挂载右上角小徽章（样式定义在 styles/sample.css）
  ctx.onDomReady(() => {
    mountHudBadge(ctx, state);
    if (ctx.config?.showBootToast !== false) {
      ctx.showToast(`已加载 ${ctx.mod.name}（按 F9 查看全部 MOD）`, 3200);
    }
  });

  // 4. 注册快捷键 Alt+M 切换徽章显示状态
  window.addEventListener('keydown', (e) => {
    if (e.altKey && !e.ctrlKey && !e.shiftKey && e.key.toLowerCase() === 'm' && !e.repeat) {
      e.preventDefault();
      state.visible = !state.visible;
      ctx.storage.set('hudVisible', state.visible);
      mountHudBadge(ctx, state);
      ctx.showToast(state.visible ? '已显示示例 MOD 徽章' : '已隐藏示例 MOD 徽章', 1800);
    }
  });
}
