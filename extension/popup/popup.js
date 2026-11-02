const $ = (id) => document.getElementById(id);

function size(bytes) {
  if (!bytes) return 'unknown size';
  const units = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  while (bytes >= 1024 && i < units.length - 1) { bytes /= 1024; i++; }
  return `${bytes.toFixed(i ? 1 : 0)} ${units[i]}`;
}

function nameOf(url) {
  try { return decodeURIComponent(new URL(url).pathname.split('/').pop()) || new URL(url).hostname; }
  catch { return url; }
}

async function tabId() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  return tab && tab.id;
}

async function init() {
  const id = await tabId();
  const status = await chrome.runtime.sendMessage({ cmd: 'status' });
  $('state').textContent = status.connected ? 'connected' : 'not connected';
  $('state').className = 'state ' + (status.connected ? 'ok' : 'bad');
  $('help').hidden = status.connected;
  $('capture').checked = status.capture;
  $('capture').addEventListener('change', () => chrome.runtime.sendMessage({ cmd: 'set', values: { capture: $('capture').checked } }));

  const { items } = await chrome.runtime.sendMessage({ cmd: 'media', tabId: id });
  if (items.length) {
    $('media').innerHTML = '';
    for (const m of items.slice().reverse()) {
      const li = document.createElement('li');
      const label = document.createElement('div');
      label.className = 'name';
      label.title = m.url;
      label.textContent = nameOf(m.url);
      const small = document.createElement('small');
      small.textContent = `${m.type === 'stream' ? 'video stream' : m.type} · ${m.type === 'stream' ? 'all pieces' : size(m.size)}`;
      label.appendChild(small);
      const button = document.createElement('button');
      button.textContent = 'Download';
      button.addEventListener('click', async () => {
        const reply = await chrome.runtime.sendMessage({ cmd: 'download', url: m.url, tabId: id });
        button.textContent = reply && reply.ok ? 'Added' : 'Failed';
      });
      li.append(label, button);
      $('media').appendChild(li);
    }
  }

  $('grab').addEventListener('click', async () => { await chrome.runtime.sendMessage({ cmd: 'grab', tabId: id }); window.close(); });
  $('open').addEventListener('click', async () => { await chrome.runtime.sendMessage({ cmd: 'open' }); window.close(); });
}

init();
