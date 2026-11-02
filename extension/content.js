// Adds a "Download" button over videos, like IDM's floating panel.
(() => {
  if (window.top !== window) return; // only the main page; embedded players are reached through the tab's sniffed media

  let button = null;
  let target = null;
  let hideTimer = 0;

  function make() {
    const b = document.createElement('button');
    b.textContent = '⬇ Download';
    b.setAttribute('aria-label', 'Download this video with EveryDownload');
    Object.assign(b.style, {
      position: 'fixed', zIndex: 2147483647, padding: '7px 14px', border: 'none', borderRadius: '16px',
      background: '#2680EB', color: '#fff', font: '600 12px system-ui, sans-serif', cursor: 'pointer',
      boxShadow: '0 2px 10px rgba(0,0,0,.35)', display: 'none',
    });
    b.addEventListener('mouseenter', () => clearTimeout(hideTimer));
    b.addEventListener('mouseleave', schedule);
    b.addEventListener('click', async (event) => {
      event.preventDefault();
      event.stopPropagation();
      const src = target && (target.currentSrc || target.src);
      b.textContent = 'Sending…';
      const reply = await chrome.runtime.sendMessage({ cmd: 'downloadVideo', src }).catch((e) => ({ ok: false, error: e.message }));
      b.textContent = reply && reply.ok ? '✓ Added' : (reply && reply.error) || 'Could not reach EveryDownload';
      setTimeout(() => { b.textContent = '⬇ Download'; }, 2200);
    });
    document.documentElement.appendChild(b);
    return b;
  }

  function place(video) {
    const r = video.getBoundingClientRect();
    button.style.top = Math.max(8, r.top + 10) + 'px';
    button.style.left = Math.max(8, r.right - button.offsetWidth - 10) + 'px';
  }

  function schedule() {
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => { if (button) button.style.display = 'none'; }, 1400);
  }

  document.addEventListener('mouseover', (event) => {
    const video = event.target && event.target.closest ? event.target.closest('video') : null;
    if (!video) return;
    const r = video.getBoundingClientRect();
    if (r.width < 200 || r.height < 120) return; // not a real player
    target = video;
    button = button || make();
    button.style.display = 'block';
    place(video);
    schedule();
  }, true);

  window.addEventListener('scroll', () => { if (button && target) place(target); }, { passive: true });
})();
