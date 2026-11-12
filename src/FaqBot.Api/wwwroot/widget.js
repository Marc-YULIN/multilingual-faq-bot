(() => {
  'use strict';
  const script = document.currentScript;
  const base = new URL('.', script.src);
  const site = script.dataset.site || 'demo';
  if (!document.querySelector('link[data-faq-widget]')) {
    const style = document.createElement('link');
    style.rel = 'stylesheet'; style.href = new URL('widget.css', base); style.dataset.faqWidget = '';
    document.head.append(style);
  }
  let sessionId;
  try { sessionId = sessionStorage.getItem('faq-session') || crypto.randomUUID(); sessionStorage.setItem('faq-session', sessionId); }
  catch { sessionId = crypto.randomUUID(); }
  const launcher = document.createElement('button');
  launcher.className = 'faq-launcher'; launcher.textContent = 'Chat with us'; launcher.setAttribute('aria-expanded', 'false');
  launcher.setAttribute('aria-controls', 'faq-panel');
  const panel = document.createElement('section'); panel.id = 'faq-panel'; panel.className = 'faq-panel'; panel.hidden = true;
  panel.setAttribute('aria-label', 'Acme Store support');
  panel.innerHTML = '<header><div><strong>Acme Store</strong><small>Here to help, in your language</small></div><button class="faq-close" aria-label="Close chat">×</button></header><label class="faq-language">Language <select aria-label="Chat language"><option value="">Auto</option><option value="zh-TW">繁體中文</option><option value="en">EN</option><option value="ko">한국어</option><option value="vi">Tiếng Việt</option></select></label><div class="faq-messages" role="log" aria-live="polite"></div><form><input aria-label="Your question" maxlength="2000" placeholder="Ask a question…" required><button type="submit">Send</button></form>';
  document.body.append(panel, launcher);
  const messages = panel.querySelector('.faq-messages');
  const form = panel.querySelector('form'); const input = panel.querySelector('input'); const select = panel.querySelector('select');
  const greeting = { 'zh-TW': '你好！有什麼購物問題需要協助？', en: 'Hello! How can we help with your shopping today?', ko: '안녕하세요! 쇼핑에 관해 무엇을 도와드릴까요?', vi: 'Xin chào! Bạn cần hỗ trợ gì khi mua sắm?' };
  const failures = { 'zh-TW': '目前無法連線，請稍後再試。', en: 'Unable to connect. Please try again shortly.', ko: '연결할 수 없습니다. 잠시 후 다시 시도해 주세요.', vi: 'Không thể kết nối. Vui lòng thử lại sau.' };
  const labels = { faq: 'FAQ', suggestions: 'Suggested', llm: 'Assistant', handoff: 'Human handoff', disabled: 'Unavailable' };
  let busy = false;
  const locale = () => select.value || (navigator.language.startsWith('zh') ? 'zh-TW' : navigator.language.split('-')[0]);
  function bubble(text, kind) {
    const element = document.createElement('div'); element.className = 'faq-bubble ' + kind; element.textContent = text;
    messages.append(element); messages.scrollTop = messages.scrollHeight; return element;
  }
  function toggle(open) { panel.hidden = !open; launcher.setAttribute('aria-expanded', String(open)); if (open) input.focus(); else launcher.focus(); }
  launcher.addEventListener('click', () => toggle(panel.hidden));
  panel.querySelector('.faq-close').addEventListener('click', () => toggle(false));
  panel.addEventListener('keydown', e => { if (e.key === 'Escape') toggle(false); });
  bubble(greeting[locale()] || greeting.en, 'assistant');
  async function send(text) {
    if (busy || !text.trim()) return;
    busy = true; input.value = ''; form.querySelector('button').disabled = true;
    bubble(text, 'user'); const typing = bubble('•••', 'assistant typing'); typing.setAttribute('aria-label', 'Preparing a reply');
    try {
      const response = await fetch(new URL('api/chat', base), { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ siteCode: site, message: text, locale: select.value || null, sessionId }) });
      if (!response.ok) throw new Error('Request failed');
      const data = await response.json(); typing.remove();
      const reply = bubble(data.reply, 'assistant'); const badge = document.createElement('small'); badge.className = 'faq-source';
      badge.textContent = labels[data.source] || data.source; badge.title = `Confidence: ${(data.confidence * 100).toFixed(1)}%`; reply.append(badge);
      for (const suggestion of data.suggestions || []) {
        const chip = document.createElement('button'); chip.type = 'button'; chip.className = 'faq-chip'; chip.textContent = suggestion.question;
        chip.addEventListener('click', () => send(suggestion.question)); reply.append(chip);
      }
    } catch { typing.remove(); bubble(failures[locale()] || failures.en, 'assistant'); }
    finally { busy = false; form.querySelector('button').disabled = false; messages.scrollTop = messages.scrollHeight; input.focus(); }
  }
  form.addEventListener('submit', e => { e.preventDefault(); send(input.value); });
})();
