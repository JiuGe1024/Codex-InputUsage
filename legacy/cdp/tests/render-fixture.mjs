import fs from 'node:fs';
import { locateComposer, installInputUsage } from '../src/input-usage-view.mjs';
const id = '00000000-0000-0000-0000-000000000001';
fs.writeFileSync('work/preview.html', `<!doctype html><meta charset="utf-8"><style>
body{font:14px "Segoe UI",sans-serif;margin:0;padding:20px;background:Canvas;color:CanvasText}main{max-width:640px;margin:240px auto 0}.composer{border:1px solid #8885;border-radius:14px;padding:14px}
</style><main data-codex-composer-root><div data-above-composer-conversation-id="${id}"></div><div class="composer" data-codex-composer contenteditable="true">输入消息</div></main><script>
(${installInputUsage})(${locateComposer}); window.snapshot={threadId:'${id}',rows:[{turn:{total:539697,input:538769,output:928,cached:530000},cumulative:{total:6800000},speed:23.2,durationMs:40004}]}; window.refresh=()=>window.__codexInputUsage.update(window.snapshot); refresh();setInterval(refresh,1000);
</script>`);