(() => {
  "use strict";

  const SCENARIOS = [
    { group: "duplicates", ids: ["payment", "duplicate", "burst", "checkout"] },
    { group: "crashes", ids: ["crash-between-steps", "crash-before-commit", "crash-after-email"] },
    { group: "failures", ids: ["flaky-provider", "poison"] },
    { group: "sources", ids: ["whatsapp", "lead"] },
  ];

  // localStorage can be missing or throw (private windows, blocked storage); the page works without it.
  const saved = {
    get(key) {
      try { return localStorage.getItem(key); } catch { return null; }
    },
    set(key, value) {
      try { localStorage.setItem(key, value); } catch { /* not persisted, that's fine */ }
    },
  };

  const state = {
    lang: initialLanguage(),
    theme: saved.get("webhookdemo.theme"), // null follows the OS
    overview: null,
    detail: null,
    selectedId: null,
    clockOffset: 0,
    offline: false,
    busy: false,
    payloadOpen: false,
    stampsSeen: new Set(),
    timelineSeen: new Map(),
    primed: false,
  };

  function initialLanguage() {
    const stored = saved.get("webhookdemo.lang");
    if (stored === "en" || stored === "es") return stored;
    return (navigator.language || "en").toLowerCase().startsWith("es") ? "es" : "en";
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  const $ = (selector) => document.querySelector(selector);

  // Strings with a count are { one, other } objects; the count picks the form.
  function t(key, vars = {}, count) {
    let template = I18N[state.lang][key] ?? I18N.en[key] ?? key;
    if (typeof template === "object") {
      template = template[new Intl.PluralRules(state.lang).select(count ?? 0)] ?? template.other;
    }
    return template.replace(/\{(\w+)\}/g, (match, name) => (name in vars ? vars[name] : match));
  }

  // Everything that comes from a webhook is attacker-controlled in real life: escape it all.
  const esc = (value) =>
    String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

  const fmt = (n) => new Intl.NumberFormat(state.lang).format(n ?? 0);
  const serverNow = () => Date.now() + state.clockOffset;
  const secondsUntil = (iso) => Math.max(0, Math.ceil((Date.parse(iso) - serverNow()) / 1000));
  const invoiceCode = (n) => `INV-${String(n).padStart(6, "0")}`;
  const sourceLabel = (source) => ({ stripe: "Stripe", whatsapp: "WhatsApp", forms: "Form" })[source] ?? source;

  function stepLabel(name) {
    const key = `step.${name}`;
    const label = t(key);
    return label === key ? name : label;
  }

  // Step results come from the server in English ("msg_123 (already sent)"); translate the known phrases.
  function stepResult(text) {
    const phrases = I18N[state.lang].results ?? {};
    return Object.entries(phrases).reduce((result, [english, local]) => result.replace(english, local), text ?? "");
  }

  function statusOf(e) {
    return e.status === "Pending" && e.lastError ? "Retrying" : e.status;
  }

  const leaseExpired = (e) => e.status === "Processing" && e.lockedUntil && Date.parse(e.lockedUntil) <= serverNow();

  function clockTime(iso) {
    return new Date(iso).toLocaleTimeString(state.lang, {
      hour: "2-digit", minute: "2-digit", second: "2-digit", fractionalSecondDigits: 3, hour12: false,
    });
  }

  // Re-rendering replaces buttons; put keyboard focus back where it was.
  function keepFocus(render) {
    const key = document.activeElement?.dataset?.focusKey;
    render();
    if (key) document.querySelector(`[data-focus-key="${CSS.escape(key)}"]`)?.focus({ preventScroll: true });
  }

  // Each section only re-renders when what it shows has changed, so hover, scroll and animations survive polling.
  const lastRendered = {};
  function changed(section, ...inputs) {
    const key = JSON.stringify([state.lang, ...inputs]);
    if (lastRendered[section] === key) return false;
    lastRendered[section] = key;
    return true;
  }

  // ── Data ───────────────────────────────────────────────────────────────

  async function refresh() {
    try {
      const response = await fetch("/api/overview", { cache: "no-store" });
      if (!response.ok) throw new Error(response.statusText);
      const overview = await response.json();
      state.overview = overview;
      state.clockOffset = Date.parse(overview.serverTime) - Date.now();

      if (state.selectedId == null && overview.events.length) state.selectedId = overview.events[0].id;
      if (state.selectedId != null) {
        const detail = await fetch(`/api/events/${state.selectedId}`, { cache: "no-store" });
        state.detail = detail.ok ? await detail.json() : null;
        if (!detail.ok) state.selectedId = null;
      } else {
        state.detail = null;
      }
      state.offline = false;
    } catch {
      state.offline = true;
    }
    render();
  }

  async function poll() {
    await refresh();
    setTimeout(poll, document.hidden ? 4000 : 1000);
  }

  async function post(url) {
    const response = await fetch(url, { method: "POST" });
    if (!response.ok) {
      const text = await response.text();
      throw new Error(text || `${response.status} ${response.statusText}`);
    }
    return response.status === 204 ? null : response.json();
  }

  // ── Actions ────────────────────────────────────────────────────────────

  async function runScenario(id, button) {
    if (state.busy) return;
    state.busy = true;
    button.setAttribute("aria-busy", "true");
    try {
      const result = await post(`/demo/scenarios/${encodeURIComponent(id)}`);
      announceDeliveries(result.events);
      const last = result.events.at(-1);
      if (last) select(last.inboxId);
    } catch (error) {
      notify(t("notice.failed", { error: error.message }), true);
    } finally {
      state.busy = false;
      button.removeAttribute("aria-busy");
      refresh();
    }
  }

  function announceDeliveries(events) {
    const count = (status) => events.filter((e) => e.status === status).length;
    const parts = [
      ["accepted", "notice.accepted"],
      ["duplicate", "notice.duplicate"],
      ["ignored", "notice.ignored"],
    ]
      .filter(([status]) => count(status) > 0)
      .map(([status, key]) => t(key, { n: count(status) }, count(status)));
    notify(t("notice.sent", { n: events.length, parts: parts.join(" · ") }, events.length));
  }

  async function act(url, successMessage) {
    try {
      await post(url);
      if (successMessage) notify(successMessage);
    } catch (error) {
      notify(t("notice.failed", { error: error.message }), true);
    }
    refresh();
  }

  function select(id) {
    if (state.selectedId === id) return;
    state.selectedId = id;
    state.payloadOpen = false;
    state.timelineSeen.delete(id);
    render();
    refresh();
  }

  let noticeTimer;
  function notify(message, isError = false) {
    const el = $("#notice");
    el.textContent = message;
    el.classList.toggle("error", isError);
    el.classList.add("show");
    clearTimeout(noticeTimer);
    noticeTimer = setTimeout(() => el.classList.remove("show"), 4200);
  }

  // ── Rendering ──────────────────────────────────────────────────────────

  function render() {
    renderChrome();
    renderThesis();
    renderScenarios();
    keepFocus(renderInbox);
    keepFocus(renderDetail);
    renderLive();
    renderInvoices();
    renderOutbox();
    renderOffline();
    state.primed = true;
  }

  function renderChrome() {
    document.documentElement.lang = state.lang;
    if (state.theme) document.documentElement.dataset.theme = state.theme;
    document.title = t("title");
    document.querySelectorAll("[data-i18n]").forEach((el) => { el.textContent = t(el.dataset.i18n); });
    document.querySelectorAll("[data-i18n-aria]").forEach((el) => el.setAttribute("aria-label", t(el.dataset.i18nAria)));
    document.querySelectorAll("[data-lang]").forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.lang === state.lang)));

    const toggle = $("#theme-toggle");
    const dark = effectiveTheme() === "dark";
    toggle.textContent = dark ? "☀" : "☾";
    toggle.setAttribute("aria-label", t(dark ? "themeToLight" : "themeToDark"));
    toggle.title = toggle.getAttribute("aria-label");
  }

  function effectiveTheme() {
    return state.theme ?? (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
  }

  function freshStamp(key) {
    const fresh = state.primed && !state.stampsSeen.has(key);
    state.stampsSeen.add(key);
    return fresh ? " fresh" : "";
  }

  function renderThesis() {
    const el = $("#thesis");
    const s = state.overview?.stats;
    if (!s || !changed("thesis", s)) return;
    if (s.deliveries === 0) {
      el.innerHTML = `<p class="thesis-line empty">${esc(t("thesis.empty"))}</p>`;
      return;
    }

    const count = (key, n) => t(key, { n: `<span class="num">${fmt(n)}</span>` }, n);
    const line = t("thesis.line", {
      deliveries: count("thesis.deliveries", s.deliveries),
      events: count("thesis.events", s.events),
      arrow: `<span class="arrow" aria-hidden="true">→</span>`,
    });
    const stamp = s.duplicates > 0
      ? `<span class="stamp stamp-lg${freshStamp(`thesis:${s.duplicates}`)}">${esc(t("thesis.stamp", { n: fmt(s.duplicates) }, s.duplicates))}</span>`
      : "";

    const fact = (key, n, cls = "") => `<li${cls ? ` class="${cls}"` : ""}>${t(key, { n: `<b>${fmt(n)}</b>` }, n)}</li>`;
    const facts = [fact("facts.completed", s.completed)];
    if (s.processing) facts.push(fact("facts.processing", s.processing));
    if (s.pending) facts.push(fact("facts.pending", s.pending));
    if (s.deadLettered) facts.push(fact("facts.dead", s.deadLettered, "fact-bad"));
    if (s.crashes) facts.push(fact("facts.crashes", s.crashes));
    if (s.recoveries) facts.push(fact("facts.recoveries", s.recoveries));
    if (s.retries) facts.push(fact("facts.retries", s.retries));
    if (s.emailsSent) facts.push(fact("facts.emails", s.emailsSent));
    if (s.emailsDeduplicated) facts.push(fact("facts.emailRepeats", s.emailsDeduplicated));
    if (s.invoices) {
      facts.push(s.invoiceGaps === 0
        ? `<li class="fact-ok">${esc(t("facts.invoicesOk", { first: invoiceCode(1), last: invoiceCode(s.invoices) }))}</li>`
        : fact("facts.invoicesGap", s.invoiceGaps, "fact-bad"));
    }

    el.innerHTML = `<p class="thesis-line">${line} ${stamp}</p><ul class="facts">${facts.join("")}</ul>`;
  }

  function renderScenarios() {
    const el = $("#scenarios");
    const demo = state.overview?.demoEnabled;
    const key = `${state.lang}:${demo}`;
    if (demo === undefined || el.dataset.rendered === key) return;
    el.dataset.rendered = key;

    if (!demo) {
      el.innerHTML = `
        <header class="panel-head"><h2 class="label">${esc(t("scenarios.title"))}</h2></header>
        <p class="hint">${esc(t("scenarios.disabled"))}</p>`;
      return;
    }

    keepFocus(() => {
      el.innerHTML = `
        <header class="panel-head">
          <h2 class="label">${esc(t("scenarios.title"))}</h2>
          <p class="hint">${esc(t("scenarios.hint"))}</p>
        </header>
        <div class="groups">
          ${SCENARIOS.map((group) => `
            <section class="group">
              <h3 class="group-title">${esc(t(`group.${group.group}`))}</h3>
              ${group.ids.map((id) => `
                <button type="button" class="scenario" data-scenario="${id}" data-focus-key="scenario:${id}">
                  <span class="scenario-title">${esc(t(`scenario.${id}`))}</span>
                  <span class="scenario-desc">${esc(t(`scenario.${id}.desc`))}</span>
                </button>`).join("")}
            </section>`).join("")}
        </div>
        <button type="button" class="reset" data-action="reset" data-focus-key="reset">${esc(t("scenarios.reset"))}</button>`;
    });
  }

  function renderInbox() {
    const el = $("#inbox");
    const events = state.overview?.events;
    if (!events || !changed("inbox", events, state.selectedId, events.map(leaseExpired))) return;
    if (!events.length) {
      el.innerHTML = `<p class="empty">${esc(t("inbox.empty"))}</p>`;
      return;
    }
    el.innerHTML = `<ol class="events">${events.map(eventRow).join("")}</ol>`;
  }

  function eventRow(e) {
    const selected = e.id === state.selectedId;
    const status = statusOf(e);
    const stamp = e.deliveries > 1
      ? `<span class="stamp${freshStamp(`row:${e.id}:${e.deliveries}`)}" title="${esc(t("inbox.delivered", { n: e.deliveries }))}">×${e.deliveries}</span>`
      : `<span class="stamp-slot"></span>`;
    return `
      <li>
        <button type="button" class="event${selected ? " selected" : ""}" data-select="${e.id}" data-focus-key="event:${e.id}" aria-pressed="${selected}">
          <span class="src src-${esc(e.source)}">${esc(sourceLabel(e.source))}</span>
          <span class="event-main">
            <span class="event-type">${esc(e.type)}</span>
            <span class="event-summary">${esc(e.summary || e.externalId)}</span>
          </span>
          <span class="event-state">
            ${pips(e)}
            <span class="status st-${status.toLowerCase()}">${esc(t(`status.${status}`))}</span>
          </span>
          ${stamp}
        </button>
      </li>`;
  }

  function pipState(e, step, index, current) {
    if (step.done) return "done";
    if (index !== current) return "todo";
    if (e.status === "Processing") return leaseExpired(e) ? "stalled" : "active";
    if (e.status === "DeadLettered") return "dead";
    if (e.status === "Pending" && e.lastError) return "retry";
    return "todo";
  }

  function pips(e) {
    if (!e.steps.length) return `<span></span>`;
    const current = e.steps.findIndex((s) => !s.done);
    return `<ol class="pips" aria-hidden="true">${e.steps
      .map((s, i) => `<li class="pip ${pipState(e, s, i, current)}"></li>`)
      .join("")}</ol>`;
  }

  function renderDetail() {
    const el = $("#detail");
    const d = state.detail;
    if (!changed("detail", d, d && leaseExpired(d.event), state.overview?.demoEnabled)) return;
    if (!d) {
      el.innerHTML = `<p class="empty">${esc(t("detail.empty"))}</p>`;
      return;
    }

    const e = d.event;
    const status = statusOf(e);
    const demo = state.overview?.demoEnabled;
    const actions = [];
    if (demo) actions.push(`<button type="button" class="action" data-action="redeliver" data-id="${e.id}" data-focus-key="redeliver">${esc(t("detail.redeliver"))}</button>`);
    if (e.status === "DeadLettered") actions.push(`<button type="button" class="action action-primary" data-action="replay" data-id="${e.id}" data-focus-key="replay">${esc(t("detail.replay"))}</button>`);

    el.innerHTML = `
      <header class="detail-head">
        <div>
          <span class="src src-${esc(e.source)}">${esc(sourceLabel(e.source))}</span>
          <p class="detail-type">${esc(e.type)}</p>
          <p class="detail-id">${esc(e.externalId)}</p>
        </div>
        <span class="status st-${status.toLowerCase()}">${esc(t(`status.${status}`))}</span>
      </header>
      ${e.summary ? `<p class="detail-summary">${esc(e.summary)}</p>` : ""}
      <p class="detail-meta">
        <span>${esc(t("detail.deliveries", { n: fmt(e.deliveries) }, e.deliveries))}</span>
        ${e.attempts ? `<span>${esc(t("detail.attempts", { n: fmt(e.attempts) }))}</span>` : ""}
      </p>
      <div id="live"></div>
      ${actions.length ? `<div class="actions">${actions.join("")}</div>` : ""}
      ${e.steps.length ? `<h3 class="label section-title">${esc(t("detail.steps"))}</h3><ol class="steps">${e.steps.map((s, i) => stepRow(e, s, i)).join("")}</ol>` : ""}
      <h3 class="label section-title">${esc(t("detail.timeline"))}</h3>
      ${timeline(e, d.timeline)}
      <details class="payload"${state.payloadOpen ? " open" : ""}>
        <summary class="label">${esc(t("detail.payload"))}</summary>
        <pre>${esc(d.payload)}</pre>
      </details>`;
  }

  // The countdowns tick every second without re-rendering the rest of the detail.
  function renderLive() {
    const el = $("#live");
    if (el && state.detail) el.innerHTML = liveLine(state.detail.event);
  }

  function liveLine(e) {
    if (e.status === "Processing" && e.lockedUntil) {
      return leaseExpired(e)
        ? `<p class="live live-stalled">${esc(t("detail.leaseExpired", { worker: e.lockedBy }))}</p>`
        : `<p class="live live-lease">${esc(t("detail.lease", { worker: e.lockedBy, s: secondsUntil(e.lockedUntil) }))}</p>`;
    }
    if (e.status === "Pending" && e.lastError) {
      return `<p class="live live-retry">${esc(t("detail.retryIn", { s: secondsUntil(e.nextAttemptAt) }))}</p>`;
    }
    if (e.status === "DeadLettered" && e.lastError) {
      return `<p class="live live-error">${esc(e.lastError)}</p>`;
    }
    return "";
  }

  function stepRow(e, step, index) {
    const current = e.steps.findIndex((s) => !s.done);
    const detail = step.done
      ? `${esc(stepResult(step.result))} <span class="who">· ${esc(step.worker ?? "")}</span>`
      : esc(t("step.pending"));
    return `
      <li class="step">
        <span class="pip ${pipState(e, step, index, current)}"></span>
        <span class="step-name">${esc(stepLabel(step.name))}<code>${esc(step.name)}</code></span>
        <span class="step-kind">${esc(t(`kind.${step.kind}`))}</span>
        <span class="step-result">${detail}</span>
      </li>`;
  }

  function timeline(e, entries) {
    const seen = state.timelineSeen.get(e.id) ?? 0;
    state.timelineSeen.set(e.id, entries.length);

    const rows = [];
    entries.forEach((entry, i) => {
      // Entries that weren't on screen before slide in, one after another.
      const arrival = i >= seen ? ` fresh" style="--i:${Math.min(i - seen, 20)}` : "";
      const attrs = (classes) => `class="${classes}${arrival}"`;
      const stamp = entry.kind === "Duplicate"
        ? ` <span class="stamp${freshStamp(`tl:${e.id}:${i}`)}">${esc(t("tl.stamp"))}</span>`
        : "";
      rows.push(`
        <li ${attrs(`tl tl-${entry.kind.toLowerCase()}`)}>
          <time class="tl-time" datetime="${esc(entry.at)}">${esc(clockTime(entry.at))}</time>
          <span class="tl-dot"></span>
          <span class="tl-text">${timelineText(entry)}${stamp}</span>
        </li>`);

      // The time nobody was in charge of the event is drawn as a break in the rail.
      const next = entries[i + 1];
      const gap = next ? ((Date.parse(next.at) - Date.parse(entry.at)) / 1000).toFixed(1) : "";
      if (next && entry.kind === "Crashed" && next.kind === "LeaseExpired") {
        rows.push(`<li ${attrs("tl-gap")}><span>${esc(t("tl.gap.lease", { s: gap }))}</span></li>`);
      } else if (next && entry.kind === "RetryScheduled" && next.kind === "Claimed") {
        rows.push(`<li ${attrs("tl-gap backoff")}><span>${esc(t("tl.gap.backoff", { s: gap }))}</span></li>`);
      }
    });
    return `<ol class="timeline">${rows.join("")}</ol>`;
  }

  function timelineText(entry) {
    const vars = {
      worker: `<span class="who">${esc(entry.worker)}</span>`,
      step: `<b>${esc(stepLabel(entry.step ?? ""))}</b>`,
      detail: esc(entry.detail),
      attempt: esc(entry.attempt),
    };
    switch (entry.kind) {
      case "Crashed":
        return t(`tl.Crashed.${entry.detail}`, vars);
      case "DeadLettered":
        return t(`tl.DeadLettered.${entry.detail}`, vars);
      case "LeaseExpired":
        return t("tl.LeaseExpired", { ...vars, detail: `<span class="who">${esc(entry.detail)}</span>` });
      case "Ignored":
        return t("tl.Ignored", { ...vars, detail: `<code>${esc(entry.detail)}</code>` });
      case "StepCompleted":
        return `${t("tl.StepCompleted", vars)}<span class="result">${esc(stepResult(entry.detail))}</span>`;
      case "Resumed":
        return t("tl.Resumed", vars, Number(entry.detail));
      default:
        return t(`tl.${entry.kind}`, vars);
    }
  }

  function renderInvoices() {
    const el = $("#invoices");
    const o = state.overview;
    if (!o || !changed("invoices", o.invoices, o.stats.invoices, o.stats.invoiceGaps)) return;
    const invoices = [...o.invoices].reverse();
    const s = o.stats;
    const check = s.invoices === 0
      ? ""
      : s.invoiceGaps === 0
        ? `<p class="check fact-ok">✓ ${esc(t("facts.invoicesOk", { first: invoiceCode(1), last: invoiceCode(s.invoices) }))}</p>`
        : `<p class="check fact-bad">${esc(t("facts.invoicesGap", { n: s.invoiceGaps }, s.invoiceGaps))}</p>`;

    el.innerHTML = `
      <header class="panel-head">
        <h2 class="label">${esc(t("invoices.title"))}</h2>
        <p class="hint">${esc(t("invoices.hint"))}</p>
      </header>
      ${invoices.length
        ? `<ol class="stubs">${invoices.map((i) => `
            <li class="stub" title="${esc(i.customerEmail ?? "")}">
              <span class="stub-code">${esc(i.code)}</span>
              <span class="stub-amount">${esc(i.amount)}</span>
            </li>`).join("")}</ol>${check}`
        : `<p class="empty">${esc(t("invoices.empty"))}</p>`}`;
  }

  function renderOutbox() {
    const el = $("#outbox");
    const calls = state.overview?.providerCalls;
    if (!calls || !changed("outbox", calls)) return;
    el.innerHTML = `
      <header class="panel-head">
        <h2 class="label">${esc(t("outbox.title"))}</h2>
        <p class="hint">${esc(t("outbox.hint"))}</p>
      </header>
      ${calls.length
        ? `<ul class="calls">${calls.map((c) => `
            <li class="call">
              <span class="src">${esc(c.provider)}</span>
              <span class="call-summary">${esc(c.summary)}</span>
              <span class="call-tag${c.deduplicated ? " dedup" : ""}">${esc(t(c.deduplicated ? "outbox.dedup" : "outbox.done"))}</span>
              <span class="call-key">${esc(c.operation)} · ${esc(c.key)} → ${esc(c.resourceId)}</span>
            </li>`).join("")}</ul>`
        : `<p class="empty">${esc(t("outbox.empty"))}</p>`}`;
  }

  let wasOffline = false;
  function renderOffline() {
    if (state.offline && !wasOffline) notify(t("offline"), true);
    wasOffline = state.offline;
  }

  // ── Events ─────────────────────────────────────────────────────────────

  document.addEventListener("click", (event) => {
    const target = event.target.closest("button, [data-select]");
    if (!target) return;

    if (target.dataset.scenario) return runScenario(target.dataset.scenario, target);
    if (target.dataset.select) return select(Number(target.dataset.select));
    if (target.dataset.lang) {
      state.lang = target.dataset.lang;
      saved.set("webhookdemo.lang", state.lang);
      return render();
    }
    if (target.id === "theme-toggle") {
      state.theme = effectiveTheme() === "dark" ? "light" : "dark";
      saved.set("webhookdemo.theme", state.theme);
      return render();
    }

    switch (target.dataset.action) {
      case "reset":
        if (confirm(t("scenarios.resetConfirm"))) {
          state.selectedId = null;
          state.detail = null;
          act("/demo/reset", t("notice.reset"));
        }
        break;
      case "redeliver":
        post(`/demo/events/${target.dataset.id}/redeliver`)
          .then((result) => announceDeliveries(result.events))
          .catch((error) => notify(t("notice.failed", { error: error.message }), true))
          .finally(refresh);
        break;
      case "replay":
        act(`/api/events/${target.dataset.id}/replay`, t("notice.replayed"));
        break;
    }
  });

  document.addEventListener("toggle", (event) => {
    if (event.target.matches?.("details.payload")) state.payloadOpen = event.target.open;
  }, true);

  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => render());

  render();
  poll();
})();
