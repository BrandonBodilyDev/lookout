// Lookout service worker: shows Web Push alerts while the crew page is closed.
// Push payloads are JSON: { title, body, tag, level, url }.

self.addEventListener("push", (event) => {
  let data = { title: "Lookout", body: "New alert", tag: "lookout", level: "warning", url: "/?mode=crew" };
  try {
    data = { ...data, ...event.data.json() };
  } catch {
    /* keep the defaults */
  }
  event.waitUntil(
    self.registration.showNotification(data.title, {
      body: data.body,
      tag: data.tag, // a newer alert for the same crew replaces the older one
      renotify: true,
      requireInteraction: data.level === "critical" || data.level === "warning",
      vibrate: [400, 150, 400, 150, 400],
      icon: "/icon.svg",
      data: { url: data.url },
    }),
  );
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const url = event.notification.data?.url ?? "/?mode=crew";
  event.waitUntil(
    self.clients.matchAll({ type: "window", includeUncontrolled: true }).then((windows) => {
      const open = windows.find((w) => w.url.includes("mode=crew"));
      return open ? open.focus() : self.clients.openWindow(url);
    }),
  );
});
