// 公開版: 一度開けば、オフラインでも開けるようにする（アプリの部品だけを保存。画像は保存しない）
self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'offline-cache-';
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
  await Promise.all(keys.filter(k => k.startsWith(cacheNamePrefix) && k !== cacheName).map(k => caches.delete(k)));
  await self.clients.claim();
}

async function onFetch(event) {
  if (event.request.method !== 'GET') return fetch(event.request);
  const url = new URL(event.request.url);
  const isPage = event.request.mode === 'navigate' && !manifestUrlList.some(u => u === event.request.url);
  const request = isPage ? 'index.html' : event.request;
  const cache = await caches.open(cacheName);
  const cached = await cache.match(request, { ignoreSearch: url.origin === self.origin });
  return cached || fetch(event.request);
}
