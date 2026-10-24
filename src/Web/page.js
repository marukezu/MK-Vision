// MK Vision — roda em toda página (e em cada iframe) do navegador do app. Avisa o app quando um vídeo começa a
// tocar (modo automático) e, na tecla ".", qual é o vídeo da tela ou a imagem sob o mouse (com as outras imagens
// grandes da página, para passar com Page Up/Down no visualizador). O app decide o que fazer.
(() => {
  if (window.__mkvisionPage) return;
  window.__mkvisionPage = true;
  const host = window.chrome && window.chrome.webview;
  if (!host) return;
  const post = message => { try { host.postMessage(message); } catch { } };
  // Endereços que a página baixou (para achar o arquivo de um vídeo montado em pedaços); o padrão guarda só 250.
  try { performance.setResourceTimingBufferSize(20000); } catch { }

  // Sites de feed (RedGIFs): cada vídeo fica num bloco com o identificador dele; o arquivo é o que a página
  // baixou com esse nome (ex.: .../ActiveLoneWhale.m4s para "activelonewhale").
  function feedItem(video) {
    const box = video.closest('[data-feed-item-id]');
    const id = box ? box.getAttribute('data-feed-item-id') || '' : '';
    if (!id) return { item: '', itemMedia: '' };
    const needle = '/' + id.toLowerCase() + '.';
    const names = performance.getEntriesByType('resource').map(r => r.name);
    let media = '';
    for (let i = names.length - 1; i >= 0 && !media; i--) if (names[i].toLowerCase().includes(needle)) media = names[i];
    return { item: id, itemMedia: media };
  }

  // Parte do elemento visível na tela (0–1 da área da janela).
  function visible(el) {
    const r = el.getBoundingClientRect();
    const w = Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0));
    const h = Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0));
    return innerWidth * innerHeight > 0 ? (w * h) / (innerWidth * innerHeight) : 0;
  }

  function videoInfo(video, reason) {
    return {
      type: 'video', reason,
      src: video.currentSrc || video.src || '',
      frame: location.href, top: window.top === window, title: (window.top === window ? document.title : '') || document.title,
      muted: video.muted || video.volume === 0,
      duration: isFinite(video.duration) ? video.duration : -1,
      time: video.currentTime || 0,
      visible: visible(video),
      width: video.videoWidth || 0, height: video.videoHeight || 0,
      // Mesma escolha da extensão: o que está tocando vence; depois, o que mais aparece.
      score: (!video.paused && !video.ended ? 1e9 : 0) + visible(video) * 1e6 + (video.videoWidth || 0),
      ...feedItem(video)
    };
  }

  // Modo automático: o app recebe todo vídeo que começa a tocar e decide (com som, grande, não é prévia).
  document.addEventListener('play', e => {
    if (e.target instanceof HTMLVideoElement) post(videoInfo(e.target, 'play'));
  }, true);
  document.addEventListener('volumechange', e => {
    const v = e.target;
    if (v instanceof HTMLVideoElement && !v.paused && !v.muted && v.volume > 0) post(videoInfo(v, 'unmuted'));
  }, true);

  function bestVideo() {
    let best = null, bestScore = -1;
    for (const v of document.querySelectorAll('video')) {
      const info = videoInfo(v, 'key');
      if ((info.src || info.width) && info.score > bestScore) { best = info; bestScore = info.score; }
    }
    return best;
  }

  // ---------- Imagens ----------

  // Itens do srcset como o navegador lê: o endereço vai até o primeiro espaço — pode ter vírgulas (DeviantArt:
  // ".../w_375,h_211,q_70,strp/..."); depois dele, até a vírgula, o tamanho ("375w", "2x"). Cortar em toda vírgula
  // quebrava esses endereços (o "." pedia um pedaço do link e dava 404).
  function srcsetItems(set) {
    const items = [];
    let i = 0;
    while (i < set.length) {
      while (i < set.length && /[\s,]/.test(set[i])) i++;
      let j = i;
      while (j < set.length && !/\s/.test(set[j])) j++;
      let url = set.slice(i, j), size = '';
      i = j;
      if (/,$/.test(url)) url = url.replace(/,+$/, ''); // sem tamanho
      else {
        const k = set.indexOf(',', i) < 0 ? set.length : set.indexOf(',', i);
        size = set.slice(i, k).trim();
        i = k + 1;
      }
      if (url) items.push({ url, size });
    }
    return items;
  }

  // Maior versão conhecida: srcset (maior largura; "2x" = o dobro da imagem), link da imagem em volta
  // (<a href="....jpg">), o próprio src.
  function largest(img) {
    let url = img.currentSrc || img.src || '', width = img.naturalWidth || 0;
    for (const { url: u, size } of srcsetItems(img.getAttribute('srcset') || '')) {
      const n = parseFloat(size) || 0;
      const w = /x$/i.test(size) ? n * (img.naturalWidth || 0) : /w$/i.test(size) ? n : 0;
      if (w > width) {
        try { url = new URL(u, location.href).href; width = w; } catch { }
      }
    }
    const link = img.closest('a[href]');
    if (link && /\.(jpe?g|png|webp|gif|avif|bmp)(\?|#|$)/i.test(link.href)) url = link.href;
    return url;
  }

  function imageAt(el) {
    if (!el || !(el instanceof Element)) return null;
    const img = el.closest('img') || (el.querySelector && el.querySelector(':scope > img, :scope > picture img'));
    if (img && img.naturalWidth >= 120 && img.naturalHeight >= 120) return img;
    return null;
  }

  function gallery() {
    const urls = [];
    for (const img of document.images) {
      if (img.naturalWidth < 300 || img.naturalHeight < 300) continue;
      const u = largest(img);
      if (u && !u.startsWith('data:') && !urls.includes(u)) urls.push(u);
    }
    return urls;
  }

  let hovered = null;
  document.addEventListener('mouseover', e => { hovered = e.target; }, true);

  function imageMessage(img) {
    const src = largest(img), all = gallery();
    if (!all.includes(src)) all.unshift(src);
    return { type: 'image', src, gallery: all, index: all.indexOf(src), frame: location.href, title: img.alt || document.title };
  }

  // Imagem no ponto (x, y) da página, mesmo coberta por camadas (o Instagram põe uma camada transparente sobre
  // cada foto; no modo celular a página nem sabe onde está o mouse — o app manda a posição). Vídeo por cima: é
  // o vídeo (null). Chamado pelo app.
  window.__mkvImageAt = (x, y) => {
    for (const el of document.elementsFromPoint(x, y)) {
      if (el instanceof HTMLVideoElement) return null;
      if (el instanceof HTMLImageElement && el.naturalWidth >= 120 && el.naturalHeight >= 120) return imageMessage(el);
    }
    // Camadas que nem o elementsFromPoint vê (pointer-events: none): a maior imagem cujo retângulo contém o ponto.
    let best = null, bestArea = 0;
    for (const img of document.images) {
      if (img.naturalWidth < 120 || img.naturalHeight < 120) continue;
      const r = img.getBoundingClientRect();
      if (x < r.left || x > r.right || y < r.top || y > r.bottom) continue;
      if (r.width * r.height > bestArea) { best = img; bestArea = r.width * r.height; }
    }
    return best ? imageMessage(best) : null;
  };

  // ".": imagem sob o mouse; senão, o melhor vídeo — o app também pergunta aos outros quadros (iframes).
  document.addEventListener('keydown', e => {
    if (e.key !== '.' || e.ctrlKey || e.altKey || e.metaKey) return;
    const t = e.target;
    if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
    e.preventDefault();
    e.stopPropagation();
    const img = imageAt(hovered);
    if (img) {
      post(imageMessage(img));
      return;
    }
    post({ type: 'dot' }); // o app procura a imagem sob o cursor (por baixo das camadas) e, se não houver, o vídeo
  }, true);

  // Botão direito: guarda onde foi (o app pergunta depois, para o "Abrir no player" do menu).
  let menu = null;
  document.addEventListener('contextmenu', e => { menu = { x: e.clientX, y: e.clientY, target: e.target, at: Date.now() }; }, true);

  // Imagem ou vídeo do botão direito: o elemento clicado (imagem de qualquer tamanho — foi escolhida), senão o que
  // está no ponto, mesmo por baixo de camadas (como o "."). Vídeo por cima de imagem: o vídeo.
  function mediaAt(x, y, target) {
    if (target instanceof Element) {
      const video = target.closest('video');
      if (video) return videoInfo(video, 'menu');
      const img = target.closest('img') || target.querySelector(':scope > img, :scope > picture img');
      if (img && img.naturalWidth >= 24 && img.naturalHeight >= 24) return imageMessage(img);
    }
    for (const el of document.elementsFromPoint(x, y)) {
      if (el instanceof HTMLVideoElement) return videoInfo(el, 'menu');
      if (el instanceof HTMLImageElement && el.naturalWidth >= 120 && el.naturalHeight >= 120) return imageMessage(el);
    }
    let best = null, bestArea = 0;
    for (const el of document.querySelectorAll('video, img')) {
      if (el instanceof HTMLImageElement && (el.naturalWidth < 120 || el.naturalHeight < 120)) continue;
      const r = el.getBoundingClientRect();
      if (x < r.left || x > r.right || y < r.top || y > r.bottom) continue;
      const area = r.width * r.height * (el instanceof HTMLVideoElement ? 2 : 1); // empate: o vídeo
      if (area > bestArea) { best = el; bestArea = area; }
    }
    return best ? (best instanceof HTMLVideoElement ? videoInfo(best, 'menu') : imageMessage(best)) : null;
  }

  window.__mkvMenuMedia = () => {
    if (!menu || Date.now() - menu.at > 5000) return null;
    const info = mediaAt(menu.x, menu.y, menu.target);
    return info ? { ...info, at: menu.at } : null;
  };

  // ---------- Botão "Abrir no player" sobre o vídeo ----------

  // Com o mouse sobre um vídeo (de qualquer site: o YouTube e outros trocam o menu do botão direito pelo deles, e o
  // "Abrir no player" do menu some), um botão no canto de cima do vídeo — o mesmo que o "." com aquele vídeo. Os
  // sites cobrem o vídeo com camadas (controles, títulos): o vídeo é achado pelo retângulo, não pelo elemento sob o
  // mouse. Some ao sair do vídeo; vídeos pequenos (prévias, miniaturas) não têm.
  const MinWidth = 200, MinHeight = 120;
  let openHost = null, openButton = null, openVideo = null, openHide = 0, openPending = false;

  function openButtonElement() {
    if (openHost && openHost.isConnected) return openButton;
    openHost = document.createElement('div');
    openHost.id = '__mkvision_open';
    openHost.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;pointer-events:none';
    const root = openHost.attachShadow({ mode: 'open' }); // as regras do site não chegam aqui dentro
    // Montado elemento por elemento e com o estilo no próprio elemento: o YouTube (e outros) proíbem innerHTML
    // ("Trusted Types") e podem barrar <style>.
    const idle = 'rgba(20,20,24,.86)', hover = 'rgba(42,92,210,.95)';
    openButton = document.createElement('button');
    openButton.type = 'button';
    openButton.title = 'Abrir este vídeo no player do MK Vision';
    openButton.style.cssText = 'position:fixed;display:none;align-items:center;gap:6px;pointer-events:auto;cursor:pointer;margin:0;' +
      `border:0;border-radius:8px;padding:7px 12px;background:${idle};color:#fff;font:600 13px "Segoe UI",sans-serif;` +
      'box-shadow:0 2px 10px rgba(0,0,0,.45);transition:background .12s;line-height:16px';
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 12 12');
    svg.setAttribute('width', '12');
    svg.setAttribute('height', '12');
    svg.style.fill = 'currentColor';
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', 'M2 1l9 5-9 5z');
    svg.appendChild(path);
    openButton.append(svg, 'Abrir no player');
    openButton.addEventListener('mouseenter', () => { openButton.style.background = hover; });
    openButton.addEventListener('mouseleave', () => { openButton.style.background = idle; });
    root.append(openButton);
    // O clique é só do botão: o site não pausa/toca nem abre nada por baixo.
    for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'dblclick', 'contextmenu'])
      openButton.addEventListener(type, e => { e.stopPropagation(); if (type !== 'contextmenu') e.preventDefault(); });
    openButton.addEventListener('click', e => {
      e.preventDefault();
      e.stopPropagation();
      if (!openVideo) return;
      post({ ...videoInfo(openVideo, 'button'), type: 'open-video' });
      showOpen(null);
    });
    openButton.addEventListener('mouseenter', () => clearTimeout(openHide));
    openButton.addEventListener('mouseleave', () => hideOpenSoon());
    (document.documentElement || document.body).appendChild(openHost);
    return openButton;
  }

  function showOpen(video) {
    clearTimeout(openHide);
    openVideo = video;
    if (!video) {
      if (openButton) openButton.style.display = 'none';
      return;
    }
    const button = openButtonElement();
    const r = video.getBoundingClientRect();
    button.style.display = 'flex';
    button.style.left = Math.max(4, Math.max(r.left, 0) + 12) + 'px';
    button.style.top = Math.max(4, Math.max(r.top, 0) + 12) + 'px';
  }

  function hideOpenSoon() {
    clearTimeout(openHide);
    openHide = setTimeout(() => showOpen(null), 700);
  }

  // O maior vídeo cujo retângulo contém o ponto (grande o bastante e visível).
  function videoAtPoint(x, y) {
    let best = null, bestArea = 0;
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect();
      if (r.width < MinWidth || r.height < MinHeight || x < r.left || x > r.right || y < r.top || y > r.bottom) continue;
      const style = getComputedStyle(v);
      if (style.visibility === 'hidden' || style.display === 'none' || parseFloat(style.opacity) === 0) continue;
      if (r.width * r.height > bestArea) { best = v; bestArea = r.width * r.height; }
    }
    return best;
  }

  document.addEventListener('mousemove', e => {
    if (openPending) return;
    openPending = true;
    const { clientX: x, clientY: y } = e;
    requestAnimationFrame(() => {
      openPending = false;
      if (openButton && e.composedPath && e.composedPath().includes(openButton)) return; // sobre o próprio botão
      const video = document.fullscreenElement ? null : videoAtPoint(x, y); // na tela cheia do site, não
      if (video) showOpen(video);
      else if (openVideo) hideOpenSoon();
    });
  }, true);
  document.addEventListener('mouseleave', () => { if (openVideo) hideOpenSoon(); });
  document.addEventListener('scroll', () => { if (openVideo) showOpen(document.contains(openVideo) ? openVideo : null); }, true);
  document.addEventListener('fullscreenchange', () => showOpen(null));

  // Botão do meio ou Ctrl+clique num link: o app abre a aba atrás, como no Chrome. O aviso vai ao APERTAR — chega
  // antes do pedido de aba nova, que o motor só faz ao soltar.
  document.addEventListener('mousedown', e => {
    if (!(e.button === 1 || (e.button === 0 && (e.ctrlKey || e.metaKey)))) return;
    const a = e.target instanceof Element ? e.target.closest('a[href], area[href]') : null;
    if (!a) return;
    const href = typeof a.href === 'string' ? a.href : (a.href && a.href.baseVal) || ''; // <a> de SVG
    post({ type: 'link-press', href, background: !e.shiftKey });
  }, true);

  // Chamados pelo app.
  window.__mkvReport =() => { const v = bestVideo(); post(v ? { ...v, type: 'candidate' } : { type: 'candidate', score: -1 }); };
  window.__mkvPause = () => { for (const v of document.querySelectorAll('video')) { try { v.pause(); } catch { } } };
  window.__mkvToast = text => {
    if (window.top !== window) return;
    let box = document.getElementById('__mkvision_toast');
    if (!box) {
      box = document.createElement('div');
      box.id = '__mkvision_toast';
      box.style.cssText = 'position:fixed;top:16px;right:16px;z-index:2147483647;background:rgba(20,20,24,.92);color:#fff;' +
        'font:600 14px Segoe UI,sans-serif;padding:10px 16px;border-radius:10px;box-shadow:0 4px 16px rgba(0,0,0,.4);pointer-events:none';
      (document.body || document.documentElement).appendChild(box);
    }
    box.textContent = text;
    box.style.opacity = '1';
    clearTimeout(box.__hide);
    box.__hide = setTimeout(() => { box.style.opacity = '0'; }, 2500);
  };

  // Instagram: Enter na caixa de pesquisa leva aos posts e reels do assunto (no site, a pesquisa só sugere contas;
  // a página de resultados por assunto existe, mas a caixa não leva até ela).
  if (/(^|\.)instagram\.com$/i.test(location.hostname) && window.top === window) {
    document.addEventListener('keydown', e => {
      if (e.key !== 'Enter' || e.isComposing || e.shiftKey || e.ctrlKey || e.altKey) return;
      const input = e.target;
      if (!(input instanceof HTMLInputElement)) return;
      const label = `${input.type} ${input.placeholder || ''} ${input.getAttribute('aria-label') || ''}`;
      if (!/search|pesquis|busca/i.test(label)) return;
      const term = input.value.trim();
      if (!term) return;
      e.preventDefault();
      e.stopPropagation();
      location.assign('/explore/search/keyword/?q=' + encodeURIComponent(term));
    }, true);
  }
})();
