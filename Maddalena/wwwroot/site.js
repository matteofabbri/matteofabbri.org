// Il ponte tra le pagine statiche e il backend di Maddalena.
// Contratto: JSON dentro, JSON fuori; la sessione e' il cookie che il browser
// gestisce da solo; 401 = non loggato.

// Chiama un endpoint /api/*. Ritorna { ok, status, data } senza mai lanciare.
async function api(method, path, body) {
  const res = await fetch(path, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  let data = null;
  try { data = await res.json(); } catch { /* nessun corpo: ok cosi' */ }
  return { ok: res.ok, status: res.status, data };
}

// L'utente loggato ({ email }) oppure null.
async function whoami() {
  const r = await api('GET', '/api/account/me');
  return r.ok ? r.data : null;
}

// Messaggi leggibili da una risposta di errore (400 di validazione o { message }).
function apiErrors(r) {
  if (r.data && r.data.errors) return Object.values(r.data.errors).flat();
  if (r.data && r.data.message) return [r.data.message];
  return ['Something went wrong (HTTP ' + r.status + ').'];
}

// Riempie una <ul class="errors"> (svuotarla = nasconderla, via CSS :empty).
function showErrors(ul, messages) {
  ul.innerHTML = '';
  for (const m of messages) {
    const li = document.createElement('li');
    li.textContent = m;
    ul.appendChild(li);
  }
}

// Dove tornare dopo il login (?return=...): solo percorsi locali.
function returnUrl() {
  const raw = new URLSearchParams(location.search).get('return');
  return raw && raw.startsWith('/') && !raw.startsWith('//') ? raw : '/';
}

// Manda al login ricordando dove tornare.
function gotoLogin() {
  location = '/account.html?return=' + encodeURIComponent(location.pathname + location.search);
}
