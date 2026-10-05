// 起動: 公開先では、圧縮した .gz のファイルを取ってきてブラウザーの中で戻す（ダウンロードを小さくする）
(function () {
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
  const canGunzip = typeof DecompressionStream !== 'undefined';
  // 起動に失敗したとき（公開し直した直後に、古い保存が残っていたときなど）は、このアプリの保存を消して 1 度だけ読み直す
  const flag = 'quantscope-recovered';
  const recover = async (manual) => {
    try { if (!manual && sessionStorage.getItem(flag)) return; sessionStorage.setItem(flag, '1'); } catch (e) { /* 保存できなくても続ける */ }
    try {
      const regs = navigator.serviceWorker ? await navigator.serviceWorker.getRegistrations() : [];
      await Promise.all(regs.filter(r => r.scope === new URL('.', document.baseURI).href).map(r => r.unregister()));
    } catch (e) { /* なし */ }
    try {
      const keys = await caches.keys();
      await Promise.all(keys.filter(k => k.startsWith('offline-cache-quantscope-') || /^offline-cache-[A-Za-z0-9+/=]+$/.test(k)).map(k => caches.delete(k)));
    } catch (e) { /* なし */ }
    location.reload();
  };
  let started = false;
  const errorUi = document.getElementById('blazor-error-ui');
  if (errorUi) {
    const reloadLink = errorUi.querySelector('.reload');
    if (reloadLink) reloadLink.addEventListener('click', (e) => { e.preventDefault(); recover(true); });
    // 起動の途中でエラーの帯が出たら（部品が読めなかった）、自動で 1 度だけ読み直す
    new MutationObserver(() => {
      if (!started && getComputedStyle(errorUi).display !== 'none') recover(false);
    }).observe(errorUi, { attributes: true, attributeFilter: ['style', 'class'] });
  }
  Blazor.start({
    webAssembly: {
      loadBootResource(type, name, defaultUri, integrity) {
        if (local || !canGunzip || type === 'dotnetjs' || type === 'configuration') return;
        return (async () => {
          const res = await fetch(defaultUri + '.gz', { cache: 'no-cache' });
          if (!res.ok) return fetch(defaultUri, { cache: 'no-cache', integrity });
          const stream = res.body.pipeThrough(new DecompressionStream('gzip'));
          const ct = name.endsWith('.wasm') ? 'application/wasm' : name.endsWith('.json') ? 'application/json' : 'application/octet-stream';
          return new Response(stream, { headers: { 'content-type': ct } });
        })();
      }
    }
  }).then(() => { started = true; try { sessionStorage.removeItem(flag); } catch (e) { /* なし */ } }, () => recover(false));
})();

if ('serviceWorker' in navigator && !['localhost', '127.0.0.1'].includes(location.hostname)) {
  navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' });
}
