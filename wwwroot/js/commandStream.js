// ═══════════════════════════════════════════════════════
//  SMART-X — PART 2: REAL-TIME COMMAND STREAM (client)
//
//  • Everything here talks to /api/stream/* — nothing is faked in the browser.
//  • State lives in this module, not in the DOM, so switching between modules/tabs never loses it:
//    filters, the selected device, dismissed suggestions and the running device simulator all survive.
//  • Polling uses chained timeouts (never overlapping requests) and pauses when the module is not on screen.
// ═══════════════════════════════════════════════════════
(() => {
    'use strict';

    const API = '/api/stream';
    const STORE_KEY = 'smartx.cs.state';

    // ─── helpers ───────────────────────────────────────
    const $ = id => document.getElementById(id);
    const sleep = ms => new Promise(r => setTimeout(r, ms));
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const toast = (msg, type = 'info') => { if (typeof showToast === 'function') showToast(msg, type); };

    async function api(path, opts) {
        const res = await fetch(API + path, opts);
        let data = null;
        try { data = await res.json(); } catch { /* empty body */ }
        if (!res.ok) throw new Error((data && data.error) || `Request failed (HTTP ${res.status})`);
        return data;
    }
    const post = (path, body) => api(path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body)
    });

    const HUMAN = { MoistureLow: 'Moisture critically low', PowerSpike: 'Power spike', OverTemp: 'Over-temperature', WeakSignal: 'Weak signal', Disconnected: 'Disconnected' };
    const humanAlert = t => HUMAN[t] || t;

    function ago(iso) {
        if (!iso) return 'no data yet';
        const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
        if (s < 5) return 'live';
        if (s < 60) return `${Math.floor(s / 5) * 5}s ago`;
        if (s < 3600) return `${Math.floor(s / 60)}m ago`;
        return `${Math.floor(s / 3600)}h ago`;
    }
    const clock = iso => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });

    function fmtValue(d) {
        if (d.category === 'Actuator') return d.isOn ? 'ON' : 'OFF';
        if (d.lastValue === null || d.lastValue === undefined) return '—';
        return d.metric === 'power' ? String(Math.round(d.lastValue)) : d.lastValue.toFixed(1);
    }

    function setText(id, text) { const el = $(id); if (el && el.textContent !== String(text)) el.textContent = text; }
    function setHtml(el, html) { if (el && el._html !== html) { el.innerHTML = html; el._html = html; } }

    /**
     * Keyed list patching: only elements whose markup changed are touched, so the dashboard
     * updates every 1.5 s without flicker and without dropping keyboard focus.
     */
    function patchList(container, items, { key, html, cls, tag = 'div', empty = '' }) {
        if (!container) return;
        if (items.length === 0) {
            if (!container._empty) { container.innerHTML = empty; container._empty = true; }
            return;
        }
        if (container._empty) { container.innerHTML = ''; container._empty = false; }

        const existing = new Map();
        for (const c of Array.from(container.children)) existing.set(c.dataset.key, c);

        items.forEach((item, i) => {
            const k = String(key(item));
            let el = existing.get(k);
            const markup = html(item);
            if (!el) {
                el = document.createElement(tag);
                el.dataset.key = k;
                el.innerHTML = markup;
                el._html = markup;
            } else {
                existing.delete(k);
                if (el._html !== markup) {
                    const active = document.activeElement;
                    const hadFocus = el.contains(active) ? (active.dataset && active.dataset.action) : undefined;
                    el.innerHTML = markup;
                    el._html = markup;
                    if (hadFocus !== undefined) {
                        const target = (hadFocus && el.querySelector(`[data-action="${hadFocus}"]`)) || el;
                        target.focus({ preventScroll: true });
                    }
                }
            }
            if (cls) { const c = cls(item); if (el.className !== c) el.className = c; }
            if (container.children[i] !== el) container.insertBefore(el, container.children[i] || null);
        });
        existing.forEach(el => el.remove());
    }

    // ─── state ─────────────────────────────────────────
    const CS = {
        initialised: false,
        visible: false,
        tab: 'live',
        devices: [],
        allDevices: [],
        allAt: 0,
        queues: null,
        alerts: null,
        commands: null,
        insights: null,
        history: null,
        selectedMac: null,
        filters: { q: '', category: '', state: '' },
        dismissed: new Set(),
        timers: {},
        connected: true,
        demoSeq: null,
        demoResult: ''
    };

    function save() {
        try {
            sessionStorage.setItem(STORE_KEY, JSON.stringify({
                filters: CS.filters,
                selectedMac: CS.selectedMac,
                dismissed: [...CS.dismissed],
                simRunning: SIM.running,
                simInterval: SIM.intervalMs
            }));
        } catch { /* storage unavailable */ }
    }

    function restore() {
        try {
            const s = JSON.parse(sessionStorage.getItem(STORE_KEY) || 'null');
            if (!s) return null;
            CS.filters = { q: '', category: '', state: '', ...(s.filters || {}) };
            CS.selectedMac = s.selectedMac || null;
            CS.dismissed = new Set(s.dismissed || []);
            if (s.simInterval) SIM.intervalMs = s.simInterval;
            return s;
        } catch { return null; }
    }

    // ═══ POLLING ═════════════════════════════════════════
    const LOOPS = {
        devices:  { ms: 1500, fn: pollDevices,  always: false },
        queues:   { ms: 1500, fn: pollQueues,   always: true },   // also feeds the sidebar alert badge
        alerts:   { ms: 3000, fn: pollAlerts,   always: false },
        insights: { ms: 3000, fn: pollInsights, always: false },
        commands: { ms: 2500, fn: pollCommands, always: false },
        detail:   { ms: 2500, fn: pollDetail,   always: false }
    };

    function shouldRun(loop) { return CS.visible || loop.always || (loop === LOOPS.devices && SIM.running); }

    function startLoop(name) {
        const loop = LOOPS[name];
        const run = async () => {
            if (!shouldRun(loop)) { CS.timers[name] = null; return; }
            try { await loop.fn(); setConnected(true); }
            catch { setConnected(false); }
            const slow = document.hidden || !CS.visible;
            CS.timers[name] = setTimeout(run, slow ? Math.max(loop.ms, 5000) : loop.ms);
        };
        CS.timers[name] = setTimeout(run, 0);
    }

    function ensureLoops() { for (const name of Object.keys(LOOPS)) if (!CS.timers[name]) startLoop(name); }

    function refreshNow() {
        return Promise.allSettled([pollDevices(), pollQueues(), pollAlerts(), pollInsights(), pollCommands(), pollDetail()]);
    }

    function setConnected(ok) {
        if (CS.connected === ok) return;
        CS.connected = ok;
        const dot = $('gw-dot'), txt = $('gw-text');
        if (dot) dot.classList.toggle('offline', !ok);
        if (txt) txt.textContent = ok ? 'Gateway online' : 'Reconnecting…';
    }

    function deviceQuery() {
        const p = new URLSearchParams();
        if (CS.filters.q.trim()) p.set('q', CS.filters.q.trim());
        if (CS.filters.category) p.set('category', CS.filters.category);
        if (CS.filters.state) p.set('state', CS.filters.state);
        const s = p.toString();
        return s ? `?${s}` : '';
    }
    const filtersActive = () => !!(CS.filters.q.trim() || CS.filters.category || CS.filters.state);

    let devicesSeq = 0;
    async function pollDevices() {
        const mine = ++devicesSeq;
        const list = await api('/devices' + deviceQuery());
        if (mine !== devicesSeq) return;               // a newer request (e.g. a keystroke) superseded this one
        CS.devices = list;
        if (!filtersActive()) { CS.allDevices = list; CS.allAt = Date.now(); }
        if (CS.visible) { renderDevices(); renderDetailHeaderOnly(); fillSelects(); if (CS.tab === 'commands') renderCommandsTab(); }
    }

    async function getAllDevices(maxAgeMs = 1200) {
        if (Date.now() - CS.allAt <= maxAgeMs && CS.allDevices.length) return CS.allDevices;
        CS.allDevices = await api('/devices');
        CS.allAt = Date.now();
        return CS.allDevices;
    }

    async function pollQueues() {
        CS.queues = await api('/queues');
        const dv = CS.queues.devices;
        const badge = $('mod-alert-badge');
        if (badge) {
            const n = dv.critical + dv.disconnected;
            badge.textContent = n;
            badge.classList.toggle('hidden', n === 0);
        }
        if (CS.visible) { renderKpis(); if (CS.tab === 'pipeline') renderPipeline(); }
    }

    async function pollAlerts() {
        CS.alerts = await api('/alerts');
        if (CS.visible && CS.tab === 'pipeline') renderAlerts();
    }

    async function pollInsights() {
        CS.insights = await api('/insights');
        const present = new Set(CS.insights.suggestedActions.map(s => s.id));
        let changed = false;
        for (const id of [...CS.dismissed]) if (!present.has(id)) { CS.dismissed.delete(id); changed = true; }   // a recurrence shows again
        if (changed) save();
        if (CS.visible && CS.tab === 'live') renderInsightPanels();
    }

    async function pollCommands() {
        CS.commands = await api('/commands');
        updateUndoButtons();
        if (CS.visible && CS.tab === 'commands') renderCommandsTab();
    }

    async function pollDetail() {
        if (!CS.selectedMac) return;
        const mac = CS.selectedMac;
        const h = await api(`/devices/${encodeURIComponent(mac)}/history?limit=180`);
        if (mac !== CS.selectedMac) return;
        CS.history = h;
        if (CS.visible && CS.tab === 'live') renderDetail();
    }

    // ═══ RENDER: KPIs + devices ══════════════════════════
    function renderKpis() {
        const q = CS.queues;
        if (!q) return;
        const dv = q.devices;
        const set = (id, text, cls) => { setText(id, text); const el = $(id); if (el && cls !== undefined) el.className = 'stat-value ' + cls; };
        set('kpi-devices', `${dv.healthy}/${dv.total}`, dv.total && dv.healthy === dv.total ? 'good' : '');
        setText('kpi-devices-sub', `${dv.warning} warning · ${dv.awaiting} awaiting data`);
        set('kpi-critical', dv.critical, dv.critical ? 'bad' : 'good');
        setText('kpi-critical-sub', dv.critical ? 'needs action' : 'all clear');
        set('kpi-disc', dv.disconnected, dv.disconnected ? 'bad' : 'good');
        setText('kpi-disc-sub', dv.disconnected ? 'no packets recently' : 'all reporting');
        set('kpi-queue', `${q.standardDepth} · ${q.priorityDepth}`, q.priorityDepth ? 'warn' : '');
        set('kpi-rate', `${q.throughputPerSec.toFixed(1)}/s`, '');
        setText('kpi-rate-sub', `${q.processedStandard + q.processedPriority} processed`);
        set('kpi-lat', q.processedPriority ? `${q.avgLatencyPriorityMs} ms` : '—', '');
        setText('kpi-lat-sub', `standard ${q.avgLatencyStandardMs} ms`);
    }

    function sparkSvg(values) {
        if (!values || values.length < 2) return '<svg class="cs-spark" viewBox="0 0 90 28" aria-hidden="true"></svg>';
        const min = Math.min(...values), max = Math.max(...values), span = max - min || 1;
        const pts = values.map((v, i) => `${(i / (values.length - 1) * 88 + 1).toFixed(1)},${(26 - ((v - min) / span) * 24).toFixed(1)}`).join(' ');
        return `<svg class="cs-spark" viewBox="0 0 90 28" aria-hidden="true"><polyline points="${pts}"/></svg>`;
    }

    function deviceCardHtml(d) {
        const actuator = d.category === 'Actuator';
        const reading = actuator
            ? `<span class="cs-dev-value">${d.isOn ? 'ON' : 'OFF'}</span>`
            : `<span class="cs-dev-value">${esc(fmtValue(d))}<small>${esc(d.unit)}</small></span>`;
        const right = actuator
            ? `<button type="button" class="cs-switch ${d.isOn ? 'on' : ''}" data-action="toggle" data-mac="${esc(d.mac)}" aria-label="Toggle ${esc(d.name)}">${d.isOn ? 'Turn off' : 'Turn on'}</button>`
            : sparkSvg(d.spark);
        const status = d.alerts.length ? humanAlert(d.alerts[0].type) : ago(d.lastSeen);
        const sync = actuator && !d.inSync ? '<span class="cs-pending" title="Waiting for the device to confirm">⏳ pending</span>' : `<span>${esc(status)}</span>`;
        return `
            <div class="cs-dev-top"><span class="cs-dev-name">${esc(d.name)}</span><span class="cs-pill ${d.state.toLowerCase()}">${esc(d.state)}</span></div>
            <div class="cs-dev-mac">${esc(d.mac)}</div>
            <div class="cs-dev-loc">${esc(d.location)}</div>
            <div class="cs-dev-reading">${reading}${right}</div>
            <div class="cs-dev-foot">${sync}<span>${d.signal != null ? d.signal + ' dBm' : ''}</span></div>`;
    }

    function renderDevices() {
        const grid = $('cs-device-grid');
        const total = CS.queues ? CS.queues.devices.total : CS.devices.length;
        setText('cs-device-count', filtersActive() ? `${CS.devices.length} of ${total} shown` : `${CS.devices.length} devices`);
        patchList(grid, CS.devices, {
            key: d => d.mac,
            html: deviceCardHtml,
            cls: d => `cs-device state-${d.state.toLowerCase()}${d.mac === CS.selectedMac ? ' selected' : ''}`,
            empty: ''
        });
        // every card is a keyboard-reachable button
        for (const el of grid.children) { if (!el.hasAttribute('tabindex')) { el.tabIndex = 0; el.setAttribute('role', 'button'); } el.dataset.mac = el.dataset.key; }
        $('cs-device-empty').classList.toggle('hidden', CS.devices.length > 0);
    }

    // ═══ RENDER: device detail / timeline ════════════════
    function chartSvg(points, d) {
        const W = 640, H = 210, L = 46, R = 14, T = 14, B = 26;
        if (!points.length) return '<p class="placeholder-text">No history yet.</p>';
        const actuator = d.category === 'Actuator';
        const t0 = new Date(points[0].timestamp).getTime();
        const t1 = new Date(points[points.length - 1].timestamp).getTime();
        const tSpan = Math.max(1, t1 - t0);
        const vals = points.map(p => p.value);
        let lo = Math.min(...vals), hi = Math.max(...vals);
        if (actuator) { lo = 0; hi = 1; }
        else {
            const pad = Math.max((hi - lo) * 0.12, Math.abs(hi) * 0.01, 0.5);
            lo -= pad; hi += pad;
            // include a limit line only when it is close enough not to flatten the data
            for (const lim of [d.criticalLow, d.criticalHigh]) {
                if (lim != null && lim > lo - (hi - lo) * 1.2 && lim < hi + (hi - lo) * 1.2) { lo = Math.min(lo, lim - 0.5); hi = Math.max(hi, lim + 0.5); }
            }
        }
        const x = t => L + ((t - t0) / tSpan) * (W - L - R);
        const y = v => T + (1 - (v - lo) / (hi - lo)) * (H - T - B);

        let grid = '';
        for (let i = 0; i <= 3; i++) {
            const v = lo + (hi - lo) * (i / 3), yy = y(v);
            const label = actuator ? (i === 0 ? 'OFF' : i === 3 ? 'ON' : '') : (d.metric === 'power' ? Math.round(v) : v.toFixed(1));
            grid += `<line class="grid" x1="${L}" x2="${W - R}" y1="${yy}" y2="${yy}"/><text class="axis" x="${L - 6}" y="${yy + 3}" text-anchor="end">${label}</text>`;
        }
        let limits = '';
        for (const [lim, word] of [[d.criticalLow, 'min'], [d.criticalHigh, 'max']]) {
            if (lim != null && lim >= lo && lim <= hi)
                limits += `<line class="limit" x1="${L}" x2="${W - R}" y1="${y(lim)}" y2="${y(lim)}"/><text class="limit-label" x="${W - R}" y="${y(lim) - 4}" text-anchor="end">critical ${word} ${lim}${esc(d.unit)}</text>`;
        }

        const coords = points.map(p => [x(new Date(p.timestamp).getTime()), y(p.value)]);
        const line = coords.map((c, i) => (i ? 'L' : 'M') + c[0].toFixed(1) + ' ' + c[1].toFixed(1)).join(' ');
        const area = `${line} L${coords[coords.length - 1][0].toFixed(1)} ${H - B} L${coords[0][0].toFixed(1)} ${H - B} Z`;
        const last = coords[coords.length - 1];
        const bad = d.state === 'Critical' || d.state === 'Disconnected';

        return `<svg class="cs-chart ${bad ? 'bad' : ''}" viewBox="0 0 ${W} ${H}" role="img" aria-label="Timeline of ${esc(d.name)}">
            ${grid}${limits}<path class="area" d="${area}"/><path class="line" d="${line}"/>
            <circle class="dot" cx="${last[0]}" cy="${last[1]}" r="4.5"/>
            <text class="axis" x="${L}" y="${H - 6}">${clock(points[0].timestamp)}</text>
            <text class="axis" x="${W - R}" y="${H - 6}" text-anchor="end">${clock(points[points.length - 1].timestamp)}</text></svg>`;
    }

    function renderDetailHeaderOnly() { if (CS.selectedMac && CS.tab === 'live') renderDetail(); }

    function renderDetail() {
        const body = $('cs-detail-body');
        if (!CS.selectedMac) {
            setText('cs-detail-title', 'Device timeline');
            setText('cs-detail-meta', '');
            setHtml(body, '<p class="placeholder-text">Select a device to see its timeline, thresholds and history.</p>');
            return;
        }
        const live = CS.devices.find(x => x.mac === CS.selectedMac) || CS.allDevices.find(x => x.mac === CS.selectedMac)
            || (CS.history && CS.history.device.mac === CS.selectedMac ? CS.history.device : null);
        if (!live) { setHtml(body, '<p class="placeholder-text">Loading…</p>'); return; }

        const h = CS.history && CS.history.device.mac === CS.selectedMac ? CS.history : null;
        const points = h ? h.points : [];
        setText('cs-detail-title', live.name);
        setText('cs-detail-meta', h ? `${h.totalStored} readings stored · showing ${h.count}` : 'loading…');

        const alerts = live.alerts.map(a => `<div class="cs-alert-line ${esc(a.severity)}"><b>${esc(humanAlert(a.type))}</b> — ${esc(a.message)}</div>`).join('');
        const limit = live.criticalLow != null ? `min ${live.criticalLow}${live.unit}` : live.criticalHigh != null ? `max ${live.criticalHigh}${live.unit}` : '—';
        const rows = points.slice(-8).reverse().map(p => `<tr><td class="num">${clock(p.timestamp)}</td><td>${live.category === 'Actuator' ? (p.value ? 'ON' : 'OFF') : esc(p.value) + esc(live.unit)}</td><td>${p.signal} dBm</td></tr>`).join('');

        setHtml(body, `
            <div class="cs-detail-grid">
                <div><small>Status</small><b><span class="cs-pill ${live.state.toLowerCase()}">${esc(live.state)}</span></b></div>
                <div><small>Latest</small><b>${esc(fmtValue(live))}${live.category === 'Actuator' ? '' : esc(live.unit)}</b></div>
                <div><small>Critical limit</small><b>${esc(limit)}</b></div>
                <div><small>Last seen</small><b>${esc(ago(live.lastSeen))}</b></div>
            </div>
            ${alerts ? `<div class="cs-detail-alerts">${alerts}</div>` : ''}
            ${chartSvg(points, live)}
            <div class="cs-sub-head">Recent readings (from the SortedDictionary, newest first)</div>
            <table class="data-table cs-log"><thead><tr><th>Time</th><th>Value</th><th>Signal</th></tr></thead><tbody>${rows}</tbody></table>`);
    }

    function selectDevice(mac, { scroll = false } = {}) {
        CS.selectedMac = mac;
        CS.history = null;
        save();
        renderDevices();
        renderDetail();
        pollDetail().catch(() => { });
        if (scroll) $('cs-detail-card').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }

    // ═══ RENDER: suggestions + insights ══════════════════
    function renderInsightPanels() {
        const ins = CS.insights;
        if (!ins) return;

        const suggestions = ins.suggestedActions.filter(s => !CS.dismissed.has(s.id));
        setText('cs-sugg-count', suggestions.length ? `${suggestions.length} active` : 'none right now');
        patchList($('cs-suggestions'), suggestions, {
            key: s => s.id,
            cls: () => 'cs-sugg',
            html: s => {
                const pct = Math.round(s.confidence * 100);
                return `<div class="cs-sugg-main">
                        <div class="cs-sugg-title">${esc(s.title)}</div>
                        <div class="cs-sugg-reason">${esc(s.reason)}</div>
                        <div class="cs-conf" aria-hidden="true"><span style="width:${pct}%"></span></div>
                        <div class="cs-sugg-meta">${pct}% confidence · seen ${s.evidence}×</div>
                    </div>
                    <div class="cs-sugg-actions">
                        <button class="btn-primary" data-action="sugg-run" data-id="${esc(s.id)}">${s.kind === 'RunCommand' ? 'Run' : 'Open'}</button>
                        <button class="cs-link-btn" data-action="sugg-dismiss" data-id="${esc(s.id)}">Dismiss</button>
                    </div>`;
            },
            empty: '<p class="empty-note">Nothing to suggest right now. When an active alert matches a pattern the engine has seen before, the most likely next action shows up here — before you search for it.</p>'
        });

        const m = ins.model || {};
        setText('cs-model-note', `Learned from ${m.events ?? 0} operator events (${m.seededEvents ?? 0} from the seeded history) · ${m.transitions ?? 0} transitions · window ${m.windowMinutes ?? 5} min`);

        setText('cs-insight-count', ins.insights.length ? `${ins.insights.length} active` : 'all quiet');
        patchList($('cs-insights'), ins.insights, {
            key: i => i.id,
            cls: () => 'cs-insight',
            html: i => `<span class="cs-ins-dot ${esc(i.severity)}"></span><div><div class="cs-ins-title">${esc(i.title)}</div><div class="cs-ins-detail">${esc(i.detail)}</div></div>`,
            empty: '<p class="empty-note">No trends or anomalies detected. Forecasts appear when a reading is steadily heading for its limit.</p>'
        });
        $('cs-insights').onclick = e => { const el = e.target.closest('.cs-insight'); if (el) selectDevice(el.dataset.key.split('|')[1], { scroll: true }); };

        const maxScore = Math.max(60, ...ins.atRisk.map(r => r.score));
        patchList($('cs-atrisk'), ins.atRisk, {
            key: r => r.mac,
            cls: () => 'cs-risk',
            html: r => `<div>${esc(r.name)}<small>${esc(r.why)}</small></div><div class="cs-risk-bar" title="Risk score ${r.score}"><span style="width:${Math.min(100, r.score / maxScore * 100)}%"></span></div>`,
            empty: '<p class="empty-note">No devices at risk.</p>'
        });
        $('cs-atrisk').onclick = e => { const el = e.target.closest('.cs-risk'); if (el) selectDevice(el.dataset.key, { scroll: true }); };

        const chips = $('cs-frequent');
        const freq = ins.frequentSearches || [];
        const sig = freq.map(f => f.name + f.count).join('|');
        if (chips._sig !== sig) {
            chips._sig = sig;
            chips.innerHTML = freq.length ? '<span class="cs-dev-loc">Often searched:</span>' + freq.map(f => `<button class="cs-chip" data-action="chip" data-name="${esc(f.name)}">${esc(f.name)} · ${f.count}</button>`).join('') : '';
        }
    }

    // ═══ COMMANDS ════════════════════════════════════════
    async function runCommand(mac, action, label) {
        try {
            const rec = await post('/commands', { deviceId: mac, action, operator: 'operator' });
            toast(rec.description + (label ? '' : ''), 'success');
            await Promise.allSettled([pollDevices(), pollCommands(), pollInsights()]);
        } catch (e) { toast(e.message, 'error'); }
    }

    async function bulkCommand(kind, act) {
        try {
            const rec = await post('/commands/bulk', { kind, action: act, operator: 'operator' });
            toast(rec.description, 'success');
            await Promise.allSettled([pollDevices(), pollCommands(), pollInsights()]);
        } catch (e) { toast(e.message, 'error'); }
    }

    async function undo() {
        try {
            const rec = await post('/commands/undo');
            toast(rec.description, 'info');
            await Promise.allSettled([pollDevices(), pollCommands(), pollInsights()]);
        } catch (e) { toast(e.message, 'error'); }
    }

    function updateUndoButtons() {
        const depth = CS.commands ? CS.commands.undoDepth : 0;
        for (const id of ['cs-undo-btn', 'cs-undo-btn-2']) {
            const b = $(id);
            if (!b) continue;
            b.disabled = depth === 0;
            b.textContent = depth ? `↶ Undo last command (${depth})` : '↶ Undo last command';
            b.title = depth ? `Reverts: ${CS.commands.nextUndo}` : 'Nothing to undo';
        }
        setText('cs-undo-depth', `Undo stack: ${depth}`);
    }

    function renderCommandsTab() {
        const c = CS.commands;
        if (!c) return;
        setText('cs-history-count', `${c.total} entr${c.total === 1 ? 'y' : 'ies'}`);
        patchList($('cs-history-body'), c.history, {
            key: r => r.id,
            tag: 'tr',
            cls: r => r.undone ? 'undone' : '',
            html: r => {
                const status = r.kind === 'Undo' ? ['undo', 'Undo'] : r.undone ? ['undone', 'Undone'] : ['active', 'Applied'];
                return `<td class="num">${clock(r.timestamp)}</td><td>${esc(r.description)}</td><td>${esc(r.operator)}</td><td><span class="cs-status ${status[0]}">${status[1]}</span></td>`;
            },
            empty: '<tr><td colspan="4"><p class="placeholder-text">No commands issued yet.</p></td></tr>'
        });

        setText('cs-stack-meta', c.undoDepth ? `${c.undoDepth} item${c.undoDepth === 1 ? '' : 's'} · top is undone first` : 'empty');
        patchList($('cs-stack'), c.undoStack, {
            key: s => s.id,
            cls: s => 'cs-stack-item' + (s.id === c.undoStack[0].id ? ' top' : ''),
            html: s => `<span>${esc(s.description)}</span>${s.id === c.undoStack[0].id ? '<small>TOP</small>' : ''}`,
            empty: '<p class="placeholder-text">Nothing to undo.</p>'
        });
        fillSelects();
    }

    // ═══ PIPELINE MONITOR ════════════════════════════════
    function renderPipeline() {
        const q = CS.queues;
        if (!q) return;
        setText('cs-depth-priority', q.priorityDepth);
        setText('cs-depth-standard', q.standardDepth);
        const bar = (id, pct) => { const el = $(id); if (el) el.style.width = Math.min(100, pct) + '%'; };
        bar('cs-bar-priority', q.priorityDepth * 10);
        bar('cs-bar-standard', (q.standardDepth / Math.max(60, q.standardDepth)) * 100 * (q.standardDepth ? 1 : 0));

        const pend = (id, list, more) => {
            const el = $(id);
            const html = list.map(p => `<span class="cs-pend">#${p.sequence} ${esc(p.deviceName)} ${esc(p.display)}</span>`).join('') + (more > list.length ? `<span class="cs-pend">+${more - list.length} more</span>` : '');
            setHtml(el, html || '<span class="field-hint">empty</span>');
        };
        pend('cs-pending-priority', q.pending.priority, q.priorityDepth);
        pend('cs-pending-standard', q.pending.standard, q.standardDepth);

        setText('cs-c-ingested', q.ingestedTotal);
        setText('cs-c-processed', `${q.processedStandard} / ${q.processedPriority}`);
        setText('cs-c-rejected', q.rejectedTotal);
        setText('cs-c-latency', `${q.avgLatencyStandardMs} / ${q.processedPriority ? q.avgLatencyPriorityMs : '—'} ms`);
        setText('cs-proc-state', q.paused ? 'Processor PAUSED — packets are piling up' : 'Processor running');
        const t = $('cs-proc-toggle'); if (t) t.textContent = q.paused ? 'Resume processor' : 'Pause processor';

        const banner = $('cs-demo-result');
        if (banner) { banner.classList.toggle('hidden', !CS.demoResult); setText('cs-demo-result', CS.demoResult || ''); }

        patchList($('cs-log-body'), q.recent.slice(0, 20), {
            key: r => r.sequence,
            tag: 'tr',
            cls: r => r.sequence === CS.demoSeq ? 'hl' : '',
            html: r => `<td class="num">${r.order}</td><td class="num">${r.sequence}</td><td><span class="lane-badge ${esc(r.lane)}">${esc(r.lane)}</span></td><td>${esc(r.deviceName)}</td><td>${esc(r.display)}</td><td class="num">${r.latencyMs} ms</td>`,
            empty: '<tr><td colspan="6"><p class="placeholder-text">Nothing processed yet.</p></td></tr>'
        });
        setText('cs-dupes', q.duplicatesSuppressed);
        updateSimUi();
    }

    function renderAlerts() {
        const a = CS.alerts;
        if (!a) return;
        setText('cs-alert-meta', `HashSet · ${a.activeKeys} unique`);
        setText('cs-dupes', a.duplicatesSuppressed);
        patchList($('cs-alerts'), a.active, {
            key: x => x.key,
            cls: () => 'cs-alert-row',
            html: x => `<div><b>${esc(x.deviceName)}</b> — ${esc(humanAlert(x.type))}<small>${esc(x.message)}</small></div><div><span class="cs-pill ${x.severity === 'Critical' ? 'critical' : 'warning'}">${esc(x.severity)}</span><small>×${x.occurrences}</small></div>`,
            empty: '<p class="placeholder-text">No active alerts.</p>'
        });
    }

    async function reportDisconnects() {
        const mac = $('cs-disc-device').value;
        if (!mac) return;
        let fresh = 0, dupes = 0;
        for (let i = 1; i <= 5; i++) {
            try { const r = await post('/disconnect-reports', { deviceId: mac, reportedBy: `Node ${i}` }); r.isNew ? fresh++ : dupes++; }
            catch (e) { toast(e.message, 'error'); return; }
        }
        toast(`5 peer reports → ${fresh} new alert, ${dupes} duplicate${dupes === 1 ? '' : 's'} suppressed`, 'success');
        await Promise.allSettled([pollAlerts(), pollQueues(), pollDevices()]);
    }

    async function toggleProcessor() {
        try {
            const paused = CS.queues && CS.queues.paused;
            await post(paused ? '/processor/resume' : '/processor/pause');
            await pollQueues();
        } catch (e) { toast(e.message, 'error'); }
    }

    /** Pauses the processor, queues 40 routine packets and one critical one, then resumes — the critical packet is processed first. */
    async function runPriorityDemo() {
        const btn = $('cs-demo-btn');
        btn.disabled = true;
        const simWasRunning = SIM.running;
        if (simWasRunning) stopSim();                    // keep the lanes clean: exactly 40 routine + 1 critical
        try {
            const devices = await getAllDevices(5000);
            const sensors = devices.filter(d => d.metric === 'moisture' || d.metric === 'temperature');
            const meter = devices.find(d => d.metric === 'power');
            if (!sensors.length || !meter) throw new Error('The demo needs at least one sensor and one power meter.');

            await post('/processor/pause');
            const normal = Array.from({ length: 40 }, (_, i) => {
                const s = sensors[i % sensors.length];
                return { deviceId: s.mac, metric: s.metric, value: s.metric === 'moisture' ? 55 + (i % 7) : 23 + (i % 5) * 0.4, signalStrength: -60 };
            });
            await post('/ingest/batch', normal);
            const crit = await post('/ingest', { deviceId: meter.mac, metric: 'power', value: 4650, signalStrength: -58 });
            CS.demoSeq = crit.sequence;
            toast('Queued 40 routine packets, then 1 critical power spike (processor paused)', 'info');
            await pollQueues();
            await sleep(2200);                           // time to see both lanes filled
            await post('/processor/resume');
            await sleep(900);
            await pollQueues();

            // Work out, from the server's processing log, how many earlier routine packets the critical one overtook.
            const rec = CS.queues.recent.find(r => r.sequence === crit.sequence);
            if (rec) {
                const overtaken = CS.queues.recent.filter(r => r.lane === 'Standard' && r.sequence < crit.sequence && r.order > rec.order).length;
                CS.demoResult = `Critical packet #${rec.sequence} (${rec.deviceName}, ${rec.display}) arrived after the routine packets, yet was processed ahead of ${overtaken} of them.`;
            }
            await Promise.allSettled([pollAlerts(), pollDevices()]);
            toast('Resumed — the critical packet was processed before the 40 routine packets that arrived earlier', 'success');
        } catch (e) { toast(e.message, 'error'); }
        finally {
            btn.disabled = false;
            if (simWasRunning) startSim();
            renderPipeline();
        }
    }

    // ═══ DEVICE SIMULATOR (acts like firmware: one POST per reading) ═══
    const SIM = { running: false, timer: null, intervalMs: 1500, vals: new Map(), scen: new Map(), sent: 0, failed: 0, failStreak: 0, busy: false, lastTick: null };

    const simBase = d => ({ moisture: 60, temperature: 25, power: 1800 }[d.metric] ?? 0);
    const rnd = (a, b) => a + Math.random() * (b - a);

    function nextPacket(d) {
        const kind = SIM.scen.get(d.mac);
        if (kind === 'dropout') return null;                              // the node goes silent
        const signal = kind === 'weak' ? Math.round(rnd(-92, -86)) : Math.round(rnd(-72, -52));

        if (d.metric === 'state') return { deviceId: d.mac, metric: 'state', state: !!d.isOn, signalStrength: signal };   // device acknowledges the commanded state

        let v = SIM.vals.has(d.mac) ? SIM.vals.get(d.mac) : (d.lastValue ?? simBase(d));
        if (kind === 'critical') {
            if (d.metric === 'moisture') v = Math.max(9, v - rnd(3.5, 5));
            else if (d.metric === 'temperature') v = Math.min(58, v + rnd(2.5, 3.5));
            else v = rnd(4400, 4800);
        } else {
            const noise = { moisture: 1.2, temperature: 0.4, power: 90 }[d.metric] || 0;
            v += (simBase(d) - v) * 0.2 + rnd(-noise, noise);
        }
        if (d.metric === 'moisture') v = Math.min(100, Math.max(0, v));
        SIM.vals.set(d.mac, v);
        return { deviceId: d.mac, metric: d.metric, value: Math.round(v * 100) / 100, signalStrength: signal };
    }

    async function simTick() {
        if (!SIM.running || SIM.busy) return;
        SIM.busy = true;
        try {
            const devices = await getAllDevices(900);
            const packets = devices.map(nextPacket).filter(Boolean);
            const results = await Promise.allSettled(packets.map(p => post('/ingest', p)));
            const ok = results.filter(r => r.status === 'fulfilled').length;
            SIM.sent += ok;
            SIM.failed += results.length - ok;
            SIM.failStreak = ok === 0 && results.length ? SIM.failStreak + 1 : 0;
            SIM.lastTick = Date.now();
            if (SIM.failStreak >= 5) { stopSim(); toast('Simulator stopped: the API is not accepting packets', 'error'); }
        } catch { SIM.failStreak++; }
        finally { SIM.busy = false; updateSimUi(); }
    }

    function startSim() {
        if (SIM.running) return;
        SIM.running = true;
        SIM.failStreak = 0;
        SIM.timer = setInterval(simTick, SIM.intervalMs);
        simTick();
        ensureLoops();
        save();
        updateSimUi();
    }

    function stopSim() {
        SIM.running = false;
        clearInterval(SIM.timer);
        SIM.timer = null;
        save();
        updateSimUi();
    }

    function setSimInterval(ms) {
        SIM.intervalMs = ms;
        if (SIM.running) { clearInterval(SIM.timer); SIM.timer = setInterval(simTick, ms); }
        save();
    }

    function updateSimUi() {
        const run = SIM.running;
        const t1 = $('cs-sim-toggle'), t2 = $('cs-sim-toggle-2');
        if (t1) { t1.textContent = run ? '■ Stop device simulator' : '▶ Start device simulator'; t1.classList.toggle('btn-primary', !run); t1.classList.toggle('btn-secondary', run); }
        if (t2) t2.textContent = run ? '■ Stop' : '▶ Start';
        setText('cs-sim-meta', run ? `running · ${SIM.sent} packets sent` : 'stopped');
        const scen = [...SIM.scen.entries()];
        const el = $('cs-sim-stats');
        if (el) el.textContent = `Sent ${SIM.sent} · failed ${SIM.failed}` + (scen.length ? ` · active scenarios: ${scen.map(([mac, k]) => `${nameOf(mac)} (${k})`).join(', ')}` : '');
    }

    const nameOf = mac => (CS.allDevices.find(d => d.mac === mac) || {}).name || mac;

    async function applyScenario(mac, kind) {
        if (!mac) return;
        SIM.scen.set(mac, kind);
        if (!SIM.running) startSim();
        toast(`${nameOf(mac)}: ${kind === 'critical' ? 'driving toward a critical value' : kind === 'dropout' ? 'going offline' : 'signal degrading'}`, 'info');
        updateSimUi();
    }

    function clearScenarios() {
        SIM.scen.clear();
        updateSimUi();
        toast('All simulated devices restored to normal', 'success');
    }

    async function burst() {
        try {
            const devices = (await getAllDevices(5000)).filter(d => d.metric !== 'state');
            const batch = Array.from({ length: 60 }, (_, i) => {
                const d = devices[i % devices.length];
                return { deviceId: d.mac, metric: d.metric, value: Math.round(simBase(d) * (0.95 + Math.random() * 0.1) * 100) / 100, signalStrength: -60 };
            });
            const r = await post('/ingest/batch', batch);
            toast(`Burst accepted: ${r.accepted} packets (${r.priority} priority)`, 'success');
            await pollQueues();
        } catch (e) { toast(e.message, 'error'); }
    }

    // ═══ selects (rebuilt only when the device list changes) ═══
    function fillSelects() {
        const all = CS.allDevices;
        if (!all.length) return;
        const fill = (id, list, placeholder) => {
            const sel = $(id);
            if (!sel) return;
            const sig = list.map(d => d.mac + d.name).join('|');
            if (sel._sig === sig) return;
            const keep = sel.value;
            sel._sig = sig;
            sel.innerHTML = (placeholder ? `<option value="">${placeholder}</option>` : '') + list.map(d => `<option value="${esc(d.mac)}">${esc(d.name)} — ${esc(d.location)}</option>`).join('');
            if (keep && list.some(d => d.mac === keep)) sel.value = keep;
        };
        fill('cs-cmd-device', all.filter(d => d.category === 'Actuator'), '');
        fill('cs-disc-device', all, '');
        fill('sim-device', all.filter(d => d.category !== 'Actuator'), '');
    }

    // ═══ EVENTS ═════════════════════════════════════════
    let searchTimer = null, logTimer = null;

    function onSearchInput(e) {
        CS.filters.q = e.target.value;
        save();
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => pollDevices().catch(() => { }), 220);
        // log the search for the predictive engine once the user stops typing
        clearTimeout(logTimer);
        logTimer = setTimeout(logSearch, 1600);
    }

    async function logSearch() {
        const q = CS.filters.q.trim();
        if (q.length < 2) return;
        try { await post('/events/search', { query: q }); pollInsights().catch(() => { }); } catch { /* best effort */ }
    }

    function onFilterChange() {
        CS.filters.category = $('cs-filter-category').value;
        CS.filters.state = $('cs-filter-state').value;
        save();
        pollDevices().catch(() => { });
    }

    function onClick(e) {
        const el = e.target.closest('[data-action]');
        if (el && el.closest('#app-shell')) {
            const a = el.dataset.action;
            switch (a) {
                case 'toggle': e.stopPropagation(); runCommand(el.dataset.mac, 'Toggle'); return;
                case 'undo': undo(); return;
                case 'bulk': bulkCommand(el.dataset.kind, el.dataset.act); return;
                case 'cmd-selected': { const mac = $('cs-cmd-device').value; if (mac) runCommand(mac, el.dataset.act); return; }
                case 'sim-toggle': SIM.running ? stopSim() : startSim(); return;
                case 'proc-toggle': toggleProcessor(); return;
                case 'priority-demo': runPriorityDemo(); return;
                case 'disc-report': reportDisconnects(); return;
                case 'burst': burst(); return;
                case 'scenario-clear': clearScenarios(); return;
                case 'scenario-apply': applyScenario($('sim-device').value, $('sim-kind').value); return;
                case 'scenario-preset': {
                    const d = CS.allDevices.find(x => x.name === el.dataset.name);
                    if (d) applyScenario(d.mac, el.dataset.kind);
                    return;
                }
                case 'chip': $('cs-search').value = el.dataset.name; onSearchInput({ target: $('cs-search') }); return;
                case 'sugg-dismiss': CS.dismissed.add(el.dataset.id); save(); renderInsightPanels(); return;
                case 'sugg-run': {
                    const s = (CS.insights?.suggestedActions || []).find(x => x.id === el.dataset.id);
                    if (!s) return;
                    if (s.kind === 'RunCommand') runCommand(s.deviceMac, s.action);
                    else selectDevice(s.deviceMac, { scroll: true });
                    return;
                }
            }
        }
        const card = e.target.closest('.cs-device');
        if (card && card.dataset.key) selectDevice(card.dataset.key);
    }

    function onKeydown(e) {
        if ((e.key === 'Enter' || e.key === ' ') && e.target.classList && e.target.classList.contains('cs-device')) {
            e.preventDefault();
            selectDevice(e.target.dataset.key);
        }
    }

    // ═══ LIFECYCLE (called by navigate() in dashboard.js) ═══
    function init() {
        if (CS.initialised) return;
        CS.initialised = true;
        const saved = restore();

        $('cs-search').value = CS.filters.q;
        $('cs-filter-category').value = CS.filters.category;
        $('cs-filter-state').value = CS.filters.state;
        $('sim-interval').value = String(SIM.intervalMs);

        $('cs-search').addEventListener('input', onSearchInput);
        $('cs-search').addEventListener('change', () => { clearTimeout(logTimer); logSearch(); });
        $('cs-filter-category').addEventListener('change', onFilterChange);
        $('cs-filter-state').addEventListener('change', onFilterChange);
        $('sim-interval').addEventListener('change', e => setSimInterval(parseInt(e.target.value, 10)));
        document.getElementById('app-shell').addEventListener('click', onClick);
        document.getElementById('app-shell').addEventListener('keydown', onKeydown);

        // The simulator starts by default the first time the module opens, so the dashboard is alive; after that the
        // operator's choice (running / stopped) is remembered for the browser session.
        if (!saved || saved.simRunning !== false) startSim();
    }

    function onShow(tab) {
        init();
        CS.visible = true;
        CS.tab = tab || 'live';

        document.querySelectorAll('#nav-commands button').forEach(b => b.classList.toggle('active', b.dataset.tab === CS.tab));

        // paint instantly from the cache (state survives navigation), then refresh in the background
        renderKpis();
        renderDevices();
        renderDetail();
        renderInsightPanels();
        renderCommandsTab();
        renderPipeline();
        renderAlerts();
        updateUndoButtons();
        updateSimUi();
        ensureLoops();
        refreshNow();
    }

    function onHide() {
        CS.visible = false;     // loops wind themselves down; the simulator (if running) keeps its own device poll alive
    }

    window.CS = { onShow, onHide, state: CS, sim: SIM };
})();
