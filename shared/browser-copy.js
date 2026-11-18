(() => {
  const status = document.getElementById('copy-status');
  function fallback(value) {
    const previous = document.activeElement;
    const field = document.createElement('textarea');
    field.value = value; field.readOnly = true;
    field.style.cssText = 'position:fixed;left:0;top:0;width:1px;height:1px;opacity:0';
    document.body.append(field);
    try { field.focus(); field.select(); field.setSelectionRange(0, value.length); if (!document.execCommand('copy')) throw new Error('copy unavailable'); }
    finally { field.remove(); if (previous && previous.focus) previous.focus(); }
  }
  for (const button of document.querySelectorAll('button[data-copy-value]')) {
    button.addEventListener('click', async () => {
      const value = button.dataset.copyValue;
      if (!value || button.disabled) return;
      button.disabled = true;
      try {
        if (window.isSecureContext && navigator.clipboard && navigator.clipboard.writeText) {
          try { await navigator.clipboard.writeText(value); } catch (_) { fallback(value); }
        } else { fallback(value); }
        if (status) status.textContent = '地址已复制';
      } catch (_) { if (status) status.textContent = '未能自动复制，请长按或选择地址复制。'; }
      finally { button.disabled = false; }
    });
  }
})();
