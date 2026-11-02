// EveryDownload browser extension: hands downloads to the app through native messaging,
// sniffs videos and audio on pages, and adds right-click entries.

const HOST = 'com.everydownload.host';
const DEFAULTS = { capture: true, minSizeKB: 0, blockedHosts: '' };

/** tabId -> list of media found on that tab (kept in memory; the worker may restart, which only clears the list). */
const media = new Map();

function send(message) {
  return new Promise((resolve, reject) => {
    chrome.runtime.sendNativeMessage(HOST, message, (reply) => {
      if (chrome.runtime.lastError) reject(new Error(chrome.runtime.lastError.message));
      else if (reply && reply.ok === false) reject(new Error(reply.error || 'The app refused the request.'));
      else resolve(reply);
    });
  });
}

async function settings() {
  return { ...DEFAULTS, ...(await chrome.storage.local.get(DEFAULTS)) };
}

async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join('; ');
  } catch {
    return '';
  }
}

function baseName(path) {
  return (path || '').split(/[\\/]/).pop() || '';
}

async function sendToApp({ url, urls, referrer, filename, silent }) {
  const all = urls && urls.length ? urls : [url];
  await send({
    type: 'add',
    url: all[0],
    urls: all,
    referrer: referrer || '',
    cookie: await cookieHeader(all[0]),
    userAgent: navigator.userAgent,
    filename: filename || '',
    silent: !!silent,
  });
}

// ---- catching the browser's own downloads -----------------------------------------------------------------------

chrome.downloads.onCreated.addListener(async (item) => {
  try {
    const s = await settings();
    if (!s.capture || item.byExtensionId) return;
    const url = item.finalUrl || item.url;
    if (!/^(https?|ftp):/i.test(url)) return; // blob: and data: addresses only exist inside the browser
    if (s.minSizeKB > 0 && item.fileSize > 0 && item.fileSize < s.minSizeKB * 1024) return;
    const host = new URL(url).hostname;
    const blocked = s.blockedHosts.split(/[\s,;]+/).filter(Boolean);
    if (blocked.some((b) => host === b || host.endsWith('.' + b))) return;

    await chrome.downloads.cancel(item.id);
    await chrome.downloads.erase({ id: item.id });
    try {
      await sendToApp({ url, referrer: item.referrer, filename: baseName(item.filename) });
    } catch (error) {
      // The app is not reachable: give the download back to the browser so nothing is lost.
      console.warn('EveryDownload is not reachable, the browser downloads it instead.', error);
      chrome.downloads.download({ url });
    }
  } catch (error) {
    console.warn('EveryDownload could not process a download', error);
  }
});

// ---- finding videos and audio -----------------------------------------------------------------------------------

const MEDIA_EXT = /\.(mp4|m4v|webm|mkv|mov|flv|mp3|m4a|aac|ogg|opus|wav|m3u8)(\?|#|$)/i;
const SEGMENT_EXT = /\.(ts|m4s|aac|vtt)(\?|#|$)/i;

function header(headers, name) {
  const h = headers.find((x) => x.name.toLowerCase() === name);
  return h ? h.value || '' : '';
}

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (details.tabId < 0 || details.statusCode >= 400) return;
    const type = header(details.responseHeaders, 'content-type').toLowerCase();
    const isPlaylist = /mpegurl/.test(type) || /\.m3u8(\?|#|$)/i.test(details.url);
    const isMedia = /^(video|audio)\//.test(type) || MEDIA_EXT.test(details.url);
    if (!isPlaylist && !isMedia) return;
    if (!isPlaylist && /\.(ts|m4s)(\?|#|$)/i.test(details.url)) return; // pieces of a stream, not files

    let size = parseInt(header(details.responseHeaders, 'content-length'), 10) || 0;
    const range = header(details.responseHeaders, 'content-range').match(/\/(\d+)$/);
    if (range) size = parseInt(range[1], 10);
    if (!isPlaylist && size > 0 && size < 300 * 1024) return; // too small to be the video itself
    if (SEGMENT_EXT.test(details.url) && !isPlaylist) return;

    const list = media.get(details.tabId) || [];
    if (list.some((m) => m.url === details.url)) return;
    list.push({ url: details.url, type: isPlaylist ? 'stream' : type.split(';')[0] || 'media', size, time: Date.now() });
    media.set(details.tabId, list.slice(-30));
    chrome.action.setBadgeBackgroundColor({ tabId: details.tabId, color: '#2680EB' });
    chrome.action.setBadgeText({ tabId: details.tabId, text: String(Math.min(list.length, 99)) });
  },
  { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other'] },
  ['responseHeaders']
);

chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === 'loading' && change.url) {
    media.delete(tabId); // a new page: forget the old page's videos
    chrome.action.setBadgeText({ tabId, text: '' });
  }
});
chrome.tabs.onRemoved.addListener((tabId) => media.delete(tabId));

/** The most useful thing to offer for a tab: a stream playlist first, otherwise the biggest file. */
function bestMedia(tabId) {
  const list = media.get(tabId) || [];
  return list.find((m) => m.type === 'stream') || [...list].sort((a, b) => b.size - a.size)[0] || null;
}

// ---- messages from the popup and the page button ----------------------------------------------------------------

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  (async () => {
    const tabId = message.tabId ?? sender.tab?.id;
    const tab = tabId != null ? await chrome.tabs.get(tabId).catch(() => null) : null;
    switch (message.cmd) {
      case 'status': {
        try { await send({ type: 'ping' }); respond({ connected: true, ...(await settings()) }); }
        catch (error) { respond({ connected: false, error: error.message, ...(await settings()) }); }
        break;
      }
      case 'media':
        respond({ items: media.get(tabId) || [], title: tab?.title || '' });
        break;
      case 'download':
        await sendToApp({ url: message.url, referrer: tab?.url, filename: message.filename });
        respond({ ok: true });
        break;
      case 'downloadVideo': {
        const best = message.src && /^https?:/i.test(message.src) ? { url: message.src } : bestMedia(tabId);
        if (!best) { respond({ ok: false, error: 'No video found yet. Press play first.' }); break; }
        await sendToApp({ url: best.url, referrer: tab?.url });
        respond({ ok: true });
        break;
      }
      case 'grab':
        await send({ type: 'grab', url: message.url || tab?.url });
        respond({ ok: true });
        break;
      case 'open':
        await send({ type: 'open' });
        respond({ ok: true });
        break;
      case 'set':
        await chrome.storage.local.set(message.values);
        respond({ ok: true });
        break;
      default:
        respond({ ok: false });
    }
  })().catch((error) => respond({ ok: false, error: error.message }));
  return true; // the answer comes asynchronously
});

// ---- right-click menu -------------------------------------------------------------------------------------------

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: 'ed-link', title: 'Download with EveryDownload', contexts: ['link', 'image', 'video', 'audio', 'selection'] });
  chrome.contextMenus.create({ id: 'ed-grab', title: 'Find files on this page with EveryDownload', contexts: ['page'] });
});

/** Every link inside the text the user selected, plus any plain web addresses typed in it. */
async function linksInSelection(tabId, frameId, selectionText) {
  const found = new Set();
  try {
    const [result] = await chrome.scripting.executeScript({
      target: { tabId, frameIds: [frameId || 0] },
      func: () => {
        const selection = window.getSelection();
        if (!selection || selection.isCollapsed) return [];
        const urls = new Set();
        for (let i = 0; i < selection.rangeCount; i++) {
          const range = selection.getRangeAt(i);
          const node = range.commonAncestorContainer;
          const root = node.nodeType === 1 ? node : node.parentElement;
          if (!root) continue;
          root.querySelectorAll('a[href]').forEach((a) => { if (range.intersectsNode(a)) urls.add(a.href); });
          const enclosing = root.closest('a[href]');
          if (enclosing) urls.add(enclosing.href);
        }
        return [...urls];
      },
    });
    (result?.result || []).forEach((u) => found.add(u));
  } catch (error) {
    console.warn('Could not read the selection on this page', error);
  }
  for (const m of (selectionText || '').match(/(https?|ftp):\/\/[^\s"'<>]+|magnet:\?[^\s"'<>]+/gi) || []) found.add(m);
  return [...found].filter((u) => /^(https?|ftp|magnet):/i.test(u));
}

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  try {
    if (info.menuItemId === 'ed-link') {
      const urls = new Set(await linksInSelection(tab.id, info.frameId, info.selectionText));
      // The link under the cursor, or the picture / video itself, when nothing was selected.
      const direct = info.linkUrl || info.srcUrl;
      if (direct && urls.size === 0) urls.add(direct);
      if (urls.size > 0) await sendToApp({ urls: [...urls], referrer: tab?.url });
    } else if (info.menuItemId === 'ed-grab') {
      await send({ type: 'grab', url: tab?.url });
    }
  } catch (error) {
    console.warn('EveryDownload is not reachable', error);
  }
});
