// 示例子模块：演示 MOD 内部支持标准 ES Module 相对路径 import
export function mountHudBadge(ctx, state) {
  let badge = document.getElementById('sample-mod-hud-badge');
  if (!badge) {
    badge = document.createElement('button');
    badge.id = 'sample-mod-hud-badge';
    badge.type = 'button';
    badge.className = 'sample-mod-badge';
    badge.title = '点击打开已加载 MOD 面板 (F9) · Alt+M 隐藏/显示';
    badge.addEventListener('click', () => ctx.api.openOverlay());
    document.body.appendChild(badge);
  }

  badge.style.display = state.visible ? 'inline-flex' : 'none';
  const cardNote = state.customCardDetected ? ' · 自定义卡已就绪' : '';
  badge.textContent = `MOD x${ctx.api.mods.length}${cardNote}`;
}
