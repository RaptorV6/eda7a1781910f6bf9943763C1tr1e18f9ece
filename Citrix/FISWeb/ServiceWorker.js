self.addEventListener("install", e => {
    e.waitUntil(
        (async () => {
            self.skipWaiting();
        })()
    );
});

self.addEventListener('fetch', function (event) {
});