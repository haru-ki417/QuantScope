// 起動: 公開先では、圧縮した .gz のファイルを取ってきてブラウザーの中で戻す（ダウンロードを小さくする）
(function () {
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
  const canGunzip = typeof DecompressionStream !== 'undefined';
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
  });
})();

if ('serviceWorker' in navigator && !['localhost', '127.0.0.1'].includes(location.hostname)) {
  navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' });
}
