export function locateComposer() {
  const hidden = '[data-app-shell-active-page="false"],[aria-hidden="true"],[inert]';
  const roots = [...document.querySelectorAll('[data-codex-composer-root]')]
    .filter(root => !root.closest(hidden) && root.getClientRects().length &&
      root.querySelector('[data-codex-composer][contenteditable="true"]'));
  if (roots.length !== 1) return null;

  const root = roots[0];
  const markers = [...root.querySelectorAll('[data-above-composer-conversation-id]')]
    .filter(marker => !marker.closest(hidden));
  const id = markers.length === 1 ? markers[0].getAttribute('data-above-composer-conversation-id') : '';
  if (!/^[\da-f]{8}-(?:[\da-f]{4}-){3}[\da-f]{12}$/i.test(id)) return null;

  const layout = getComputedStyle(root);
  if (layout.display !== 'block' && !(layout.display === 'flex' && layout.flexDirection === 'column')) return null;
  return { root, threadId: id.toLowerCase() };
}

export function installInputUsage(locate) {
  const key = '__codexInputUsage';
  if (window[key]) return { ok: true };
  if (typeof HTMLElement.prototype.showPopover !== 'function') return { ok: false, reason: 'popover-unavailable' };

  const host = document.createElement('div');
  host.dataset.codexInputUsage = '';
  const shadow = host.attachShadow({ mode: 'open' });
  shadow.innerHTML = `<style>
    :host{display:block;min-width:0;padding-top:5px;color:var(--color-text-secondary,var(--text-secondary,CanvasText));font:13px/1.45 "Times New Roman",SimSun,serif;font-variant-numeric:tabular-nums}
    button{display:inline-flex;flex-wrap:wrap;align-items:center;gap:2px 10px;padding:4px 10px;min-height:26px;border:0;border-radius:999px;background:color-mix(in srgb,currentColor 7%,transparent);color:inherit;font:inherit;text-align:left;cursor:pointer}
    button:hover,button[aria-expanded="true"]{background:color-mix(in srgb,currentColor 13%,transparent)}
    button:focus-visible{outline:2px solid currentColor;outline-offset:2px}
    strong,h2,dd{color:var(--color-text-primary,var(--text-primary,CanvasText))}
    .details{position:fixed;inset:auto;box-sizing:border-box;width:min(310px,calc(100vw - 16px));margin:0;padding:12px 14px;overflow:auto;border:1px solid color-mix(in srgb,currentColor 18%,transparent);border-radius:12px;background:var(--color-bg-primary,var(--color-background,Canvas));color:inherit;font:inherit}
    h2{margin:0 0 7px;font-size:14px}
    dl{margin:0;display:grid;grid-template-columns:minmax(0,1fr) auto;gap:5px 12px}
    dl+dl{margin-top:8px;padding-top:8px;border-top:1px solid color-mix(in srgb,currentColor 18%,transparent)}
    dd{margin:0;text-align:right;font-weight:700}
    table{width:100%;border-collapse:collapse;text-align:right;font-size:12px}
    th,td{padding:3px 0}
    th:first-child,td:first-child{text-align:left}
    .rounds{margin-top:10px;padding-top:8px;border-top:1px solid color-mix(in srgb,currentColor 18%,transparent)}
  </style>
  <button type="button" aria-expanded="false" aria-controls="input-usage-details" title="查看用量详情">
    <strong>Token 用量</strong><span>最近一轮 <b data-value="turn">--</b></span><span>会话累计 <b data-value="cumulative">--</b></span><span>累计缓存 <b data-value="cumulativeCache">--</b></span>
  </button>
  <section class="details" id="input-usage-details" popover="auto" aria-labelledby="usage-heading">
    <h2 id="usage-heading">最近一轮用量</h2>
    <dl><dt>输入 Token</dt><dd data-value="input">--</dd><dt>输出 Token</dt><dd data-value="output">--</dd><dt>缓存命中</dt><dd data-value="cache">--</dd></dl>
    <dl><dt>平均速度</dt><dd data-value="speed">--</dd><dt>时长（扣除工具）</dt><dd data-value="duration">--</dd></dl>
    <div class="rounds"><h2>逐轮消耗</h2><table aria-label="逐轮消耗"><thead><tr><th scope="col">轮次</th><th scope="col">Token</th><th scope="col">缓存命中</th></tr></thead><tbody></tbody></table></div>
  </section>`;
  const button = shadow.querySelector('button');
  const details = shadow.querySelector('.details');
  const fields = new Map([...shadow.querySelectorAll('[data-value]')].map(el => [el.dataset.value, el]));
  const exact = value => Number.isSafeInteger(value) && value >= 0 ? value.toLocaleString('en-US') : '--';
  const cacheRate = usage => usage?.input > 0 && usage.cached != null
    ? (100 * usage.cached / usage.input).toFixed(2) + '%' : '--';
  const history = shadow.querySelector('tbody');
  let threadId, lease, historyKey;
  let rows = [];

  function renderHistory() {
    if (!details.matches(':popover-open')) return;
    const key = JSON.stringify(rows.map(row => [row.turn?.total, cacheRate(row.turn)]));
    if (key === historyKey) return;
    historyKey = key;
    const fragment = document.createDocumentFragment();
    for (const [index, row] of rows.entries()) {
      const tr = document.createElement('tr');
      for (const value of [index + 1, exact(row.turn?.total), cacheRate(row.turn)]) {
        const cell = document.createElement('td');
        cell.textContent = value;
        tr.append(cell);
      }
      fragment.append(tr);
    }
    history.replaceChildren(fragment);
  }

  function position() {
    if (!details.matches(':popover-open')) return;
    const rect = button.getBoundingClientRect();
    details.style.maxHeight = Math.max(0, rect.top - 16) + 'px';
    details.style.left = Math.max(8, Math.min(rect.left, innerWidth - details.offsetWidth - 8)) + 'px';
    details.style.top = Math.max(8, rect.top - details.offsetHeight - 8) + 'px';
  }
  button.popoverTargetElement = details;
  details.addEventListener('toggle', () => {
    button.setAttribute('aria-expanded', String(details.matches(':popover-open')));
    renderHistory();
    position();
  });
  const stop = () => {
    clearTimeout(lease);
    window.removeEventListener('resize', position);
    window.removeEventListener('scroll', position, true);
    host.remove();
    delete window[key];
  };
  const renew = () => {
    clearTimeout(lease);
    lease = setTimeout(stop, 15000);
  };
  window.addEventListener('resize', position);
  window.addEventListener('scroll', position, { capture: true, passive: true });
  window[key] = {
    context: () => ({ threadId: locate()?.threadId ?? null }),
    stop,
    update(snapshot) {
      renew();
      const composer = locate();
      if (!composer || composer.threadId !== snapshot.threadId) {
        host.remove();
        return { ok: true, mountedRows: 0, reason: 'composer-unavailable' };
      }
      if (threadId !== composer.threadId || host.parentElement !== composer.root) {
        details.hidePopover();
        threadId = composer.threadId;
        composer.root.append(host);
      }
      if (snapshot.rows) rows = snapshot.rows;
      const row = rows.at(-1);
      const turn = row?.turn;
      const values = {
        turn: exact(turn?.total),
        cumulative: exact(row?.cumulative?.total),
        cumulativeCache: cacheRate(row?.cumulative),
        input: exact(turn?.input),
        output: exact(turn?.output),
        cache: cacheRate(turn),
        speed: Number.isFinite(row?.speed) ? row.speed.toFixed(1) + ' tok/s' : '--',
        duration: Number.isFinite(row?.durationMs) ? (row.durationMs / 1000).toFixed(1) + ' s' : '--',
      };
      for (const [name, value] of Object.entries(values)) {
        const field = fields.get(name);
        if (field.textContent !== value) field.textContent = value;
      }
      renderHistory();
      position();
      return { ok: true, mountedRows: 1 };
    },
  };
  renew();
  return { ok: true };
}
