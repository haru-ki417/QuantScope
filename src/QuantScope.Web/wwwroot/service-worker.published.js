// 公開版: 一度開けば、オフラインでも開けるようにする（アプリの部品だけを保存。画像は保存しない）
self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

// 同じ github.io の上に別のアプリもあるので、名前にアプリ名を入れて、ほかのアプリの保存を消さない
const cacheNamePrefix = 'offline-cache-quantscope-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.gz$/, /\.js$/, /\.json$/, /\.css$/, /\.png$/, /\.svg$/, /\.html$/, /\.webmanifest$/];
const offlineAssetsExclude = [/^service-worker\.js$/, /\.br$/];
const base = '/QuantScope/';
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall() {
  const assetsRequests = self.assetsManifest.assets
    .filter(asset => offlineAssetsInclude.some(p => p.test(asset.url)))
    .filter(asset => !offlineAssetsExclude.some(p => p.test(asset.url)))
    .map(asset => new Request(asset.url, { cache: 'no-cache' }));
  await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
  self.skipWaiting();
}

async function onActivate() {
  const keys = await caches.keys();
  // 古い版の保存と、アプリ名の入っていない以前の形式の保存を消す
  const legacy = /^offline-cache-[A-Za-z0-9+/=]+$/;
  await Promise.all(keys.filter(k => (k.startsWith(cacheNamePrefix) && k !== cacheName) || legacy.test(k)).map(k => caches.delete(k)));
  await self.clients.claim();
}

async function onFetch(event) {
  if (event.request.method !== 'GET') return fetch(event.request);
  const url = new URL(event.request.url);
  const isPage = event.request.mode === 'navigate' && !manifestUrlList.some(u => u === event.request.url);
  const cache = await caches.open(cacheName);
  if (isPage) {
    // ページ（index.html）は、つながっていればいつもネットから取る。新しい版を公開したあとに、
    // 保存しておいた古いページから、もう無い古い部品を読みに行って起動できなくなるのを防ぐ。
    // 部品のファイル名には中身の指紋が入っているので、ほかのファイルは保存したものを使ってよい
    try {
      const res = await fetch(event.request.url, { cache: 'no-cache', credentials: 'same-origin' });
      if (res.ok) { cache.put('index.html', res.clone()); return res; }
    } catch (e) { /* オフライン */ }
    const saved = await cache.match('index.html');
    return saved || fetch(event.request);
  }
  const cached = await cache.match(event.request, { ignoreSearch: url.origin === self.origin });
  return cached || fetch(event.request);
}
