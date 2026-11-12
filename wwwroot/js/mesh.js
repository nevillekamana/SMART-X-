// ═══════════════════════════════════════════════════════
//  FINAL POE — Network Topology & Mesh Routing (client)
//  Module state lives in one object (M) and is mirrored to sessionStorage, so leaving the module and coming back
//  (or refreshing) restores the algorithm, selection, tracked route and history query.
//  Polling is a chained setTimeout (3 s): the next request is only scheduled after the previous one finished,
//  an unchanged response skips the re-render, and everything stops while the module is hidden.
// ═══════════════════════════════════════════════════════
(function () {
    'use strict';
    const API = '/api/mesh';
    const STORE = 'smartx.mesh';
    const POLL_MS = 3000;
    const $ = id => document.getElementById(id);
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const toast = (msg, type) => { try { showToast(msg, type || 'info'); } catch { /* dashboard.js not loaded */ } };

    // ─── module state ───
    const M = {
        tab: 'topology', running: false, timer: null, inflight: false,
        algo: 'prim', showTree: true, selected: null, routeId: null, recent: [],
        topo: null, conn: null, nodes: [], topoJson: '', connJson: '', routeJson: '',
        hist: { node: null, from: '', to: '', page: 1, pageSize: 15, last: null, preset: 120 },
        polls: 0
    };

    function save() {
        try {
            sessionStorage.setItem(STORE, JSON.stringify({
                algo: M.algo, showTree: M.showTree, selected: M.selected, routeId: M.routeId, recent: M.recent.slice(0, 6),
                hist: { node: M.hist.node, from: M.hist.from, to: M.hist.to, page: M.hist.page, preset: M.hist.preset }
            }));
        } catch { /* storage unavailable */ }
    }
    function load() {
        try {
            const s = JSON.parse(sessionStorage.getItem(STORE) || 'null');
            if (!s) return;
            M.algo = s.algo || 'prim'; M.showTree = s.showTree !== false; M.selected = s.selected || null;
            M.routeId = s.routeId || null; M.recent = s.recent || [];
            Object.assign(M.hist, s.hist || {});
        } catch { /* ignore corrupt state */ }
    }

    async function api(path, opts) {
        try {
            const r = await fetch(API + path, opts);
            let data = null; try { data = await r.json(); } catch { /* empty body */ }
            return { ok: r.ok, status: r.status, data };
        } catch (e) { return { ok: false, status: 0, data: { error: 'Network error: ' + e.message } }; }
    }
    const post = (path, body) => api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });

    // ═══ SVG map ═══════════════════════════════════════════════
    const NS = 'http://www.w3.org/2000/svg';
    function pairKey(a, b) { return a < b ? a + '|' + b : b + '|' + a; }
    const GLYPH = { Sensor: 'S', Meter: 'M', Pump: 'P', Valve: 'V', Fan: 'F', Relay: 'R', Gateway: 'GW' };

    function drawMap(svg, topo, opt) {
        opt = opt || {};
        const byId = new Map(topo.nodes.map(n => [n.id, n]));
        const linkByPair = new Map(topo.links.map(l => [pairKey(l.a, l.b), l]));
        const treeSet = new Set(topo.tree.edgeIds);
        const hl = new Set(), hlDij = new Set();
        const addPath = (path, set) => { for (let i = 0; i + 1 < (path || []).length; i++) { const l = linkByPair.get(pairKey(path[i], path[i + 1])); if (l) set.add(l.id); } };
        if (opt.treePath) addPath(opt.treePath, hl);
        if (opt.dijPath) addPath(opt.dijPath, hlDij);
        const routeMode = !!opt.treePath || !!opt.dijPath;

        let h = '';
        for (const c of topo.clusters)
            h += `<rect class="mesh-cluster" x="${c.x}" y="${c.y}" width="${c.w}" height="${c.h}" rx="14"/><text class="mesh-cluster-label" x="${c.x + 12}" y="${c.y + 20}">${esc(c.label.toUpperCase())}</text>`;
        if (topo.nodes.some(n => n.status === 'Unlinked'))
            h += `<text class="mesh-cluster-label" x="60" y="530">UNLINKED SENSORS (add a link to join the mesh)</text>`;

        // links: thin first, then tree/highlight on top so bold links are never hidden
        const pos = id => byId.get(id);
        const line = (l, cls) => { const a = pos(l.a), b = pos(l.b); return a && b ? `<line class="mesh-link ${cls}" x1="${a.x}" y1="${a.y}" x2="${b.x}" y2="${b.y}"/>` : ''; };
        for (const l of topo.links) if (!(routeMode ? (hl.has(l.id) || hlDij.has(l.id)) : (opt.showTree && l.inTree))) h += line(l, (l.live ? '' : 'dead ') + (routeMode ? 'faint' : ''));
        if (routeMode) {
            for (const l of topo.links) if (hlDij.has(l.id) && !hl.has(l.id)) h += line(l, 'hl-dij');
            for (const l of topo.links) if (hl.has(l.id)) h += line(l, 'hl');
        } else if (opt.showTree) for (const l of topo.links) if (l.inTree) h += line(l, 'tree');

        // weight labels in small background pills (bold links only)
        const pillLinks = routeMode ? topo.links.filter(l => hl.has(l.id) || hlDij.has(l.id)) : (opt.showTree ? topo.links.filter(l => l.inTree) : []);
        for (const l of pillLinks) {
            const a = pos(l.a), b = pos(l.b); if (!a || !b) continue;
            const t = String(l.weight.toFixed(2)), w = t.length * 5.8 + 10, mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
            h += `<g class="mesh-pill"><rect x="${mx - w / 2}" y="${my - 8}" width="${w}" height="16" rx="8"/><text x="${mx}" y="${my}">${t}</text></g>`;
        }

        for (const n of topo.nodes) {
            const st = n.status.toLowerCase();
            const sel = opt.selectable && M.selected === n.id ? ' selected' : '';
            const sub = n.status === 'Up' ? (n.hops != null ? `${n.hops} hop${n.hops === 1 ? '' : 's'}` : '') : n.status === 'Gateway' ? '' : n.status.toLowerCase();
            const aria = `${n.name}, ${n.status}${n.singlePointOfFailure ? ', single point of failure' : ''}`;
            const r = n.status === 'Gateway' ? 0 : 15;
            h += `<g class="mesh-node ${st}${sel}" data-id="${esc(n.id)}" tabindex="${opt.selectable ? 0 : -1}" role="${opt.selectable ? 'button' : 'img'}" aria-label="${esc(aria)}" transform="translate(${n.x},${n.y})">`;
            if (n.status === 'Gateway') h += `<rect class="body" x="-20" y="-20" width="40" height="40" rx="6"/><rect class="ring" x="-25" y="-25" width="50" height="50" rx="9"/><text class="glyph">GW</text><text class="label" y="36">Gateway</text>`;
            else {
                h += `<circle class="ring" r="${r + 6}"/><circle class="body" r="${r}"/><text class="glyph">${GLYPH[n.kind] || '•'}</text><text class="label" y="${r + 14}">${esc(n.name)}</text>${sub ? `<text class="sub" y="${r + 26}">${esc(sub)}</text>` : ''}`;
                if (n.singlePointOfFailure) h += `<circle class="spof" cx="12" cy="-12" r="5.5"><title>Single point of failure</title></circle>`;
            }
            h += '</g>';
        }
        svg.innerHTML = h;
    }

    // ═══ Topology tab ══════════════════════════════════════════
    function renderKpis(k) {
        const card = (label, value, sub, cls) => `<div class="mesh-kpi ${cls || ''}"><div class="k-label">${label}</div><div class="k-value">${value}</div><div class="k-sub">${sub}</div></div>`;
        $('mesh-kpis').innerHTML =
            card('Online', k.online, `of ${k.totalLinked} linked nodes`, 'good') +
            card('Offline', k.offline, k.offline ? 'simulated or alert-down' : 'all nodes responding', k.offline ? 'bad' : '') +
            card('Cut off', k.cutOff, k.cutOff ? 'alive but no route' : 'every node has a route', k.cutOff ? 'warn' : '') +
            card('Tree cost', k.treeCost.toFixed(2), 'total power units of the MST') +
            card('Power saved', k.powerSaved.toFixed(2), `${k.powerSavedPct}% vs using all ${k.allLinksCost.toFixed(2)} links`, 'good') +
            card('Longest path', k.longestPathHops + ' hops', k.longestPathNode ? esc(k.longestPathNode) : '—');
        const b = $('mod-mesh-badge'), bad = k.offline + k.cutOff;
        if (b) { b.textContent = bad; b.classList.toggle('hidden', bad === 0); }
    }

    function renderTreeNote(t) {
        $('mesh-tree-note').innerHTML = `<b>${t.algorithm === 'prim' ? 'Prim' : 'Kruskal'}</b> tree: ${t.edgeCount} links, cost <b>${t.cost.toFixed(2)}</b>. Prim = ${t.primCost.toFixed(2)}, Kruskal = ${t.kruskalCost.toFixed(2)} ` +
            (t.agree ? '<span class="ok">✓ identical trees</span>' : '<span>⚠ differ</span>');
    }

    function renderInspector() {
        const el = $('mesh-inspector'), t = M.topo;
        const n = t && M.selected ? t.nodes.find(x => x.id === M.selected) : null;
        if (!n) { el.innerHTML = '<div class="empty-note">Select a node on the map to inspect it.</div>'; return; }
        const st = n.status.toLowerCase();
        const simDown = n.status === 'Down' && /simulated/.test(n.downReason);
        const canFail = n.status === 'Up' || n.status === 'Isolated';
        el.innerHTML = `<div class="mesh-insp">
            <h3>${esc(n.name)} <span class="mesh-badge ${st}">${esc(n.status)}</span></h3>
            <div class="mac">${esc(n.mac || 'GATEWAY')}${n.singlePointOfFailure ? ' · <b style="color:#b07d00">single point of failure</b>' : ''}</div>
            <dl>
                <dt>Kind</dt><dd>${esc(n.kind)}</dd>
                <dt>Cluster</dt><dd>${esc(n.cluster)}</dd>
                <dt>Hops to gateway</dt><dd>${n.hops ?? '—'}</dd>
                <dt>Next hop</dt><dd>${esc(n.nextHopName || '—')}</dd>
                <dt>Route cost</dt><dd>${n.routeCost != null ? n.routeCost.toFixed(2) : '—'}</dd>
                <dt>Links</dt><dd>${n.degree}</dd>
                ${n.downReason ? `<dt>Down because</dt><dd>${esc(n.downReason)}</dd>` : ''}
            </dl>
            <div class="mesh-btns">
                <button class="btn-secondary" data-mesh="fail" ${canFail ? '' : 'disabled'}>Simulate failure</button>
                <button class="btn-secondary" data-mesh="recover" ${simDown ? '' : 'disabled'}>Bring back online</button>
                <button class="btn-primary" data-mesh="track-sel" ${n.status === 'Unlinked' ? 'disabled' : ''}>Track route</button>
                <button class="btn-secondary" data-mesh="hist-sel" ${n.status === 'Gateway' ? 'disabled' : ''}>History</button>
            </div>
            ${n.status === 'Unlinked' ? '<div class="field-hint" style="margin-top:.5rem">Not in the mesh yet — use “Add a link” to connect it.</div>' : ''}
        </div>`;
    }

    function renderConnectivity(c) {
        const lvl = c.levels.map(l => `<li><b>Level ${l.level}</b> <span class="muted">${l.nodes.map(n => esc(n.name)).join(', ')}</span></li>`).join('');
        let h = `<div class="mesh-sec"><h4>BFS levels from the gateway</h4><ul>${lvl || '<li class="muted">—</li>'}</ul></div>`;
        if (c.downNodes.length) {
            h += `<div class="mesh-sec bad"><h4>Down nodes &amp; failover hops</h4><ul>` + c.downNodes.map(d =>
                `<li><b>${esc(d.name)}</b> <span class="muted">(${esc(d.reason)})</span>` +
                (d.affected.length ? '<ul>' + d.affected.map(a => `<li>${esc(a.name)}: ${esc(a.oldNextHop)} <span class="arrow">→</span> ${a.newNextHop ? esc(a.newNextHop) : '<b>no route</b>'}</li>`).join('') + '</ul>' : '<div class="muted">no node relied on it</div>') + '</li>').join('') + '</ul></div>';
        }
        if (c.isolated.length) {
            h += `<div class="mesh-sec warn"><h4>Cut off — suggested relay</h4><ul>` + c.isolated.map(i =>
                `<li><b>${esc(i.name)}</b> <span class="muted">${i.suggestedRelay ? '→ relay via ' + esc(i.suggestedRelay.name) + ` (${i.suggestedRelay.distance} px)` : 'no live node in range'}</span></li>`).join('') + '</ul></div>';
        }
        h += `<div class="mesh-sec"><h4>DFS sub-networks</h4><ul>` + c.subNetworks.map(s =>
            `<li>${s.containsGateway ? '<b>Gateway network</b>' : 'Detached'} · ${s.size} node${s.size === 1 ? '' : 's'}${s.containsGateway ? '' : ` <span class="muted">${s.nodes.map(n => esc(n.name)).join(', ')}</span>`}</li>`).join('') + '</ul></div>';
        h += `<div class="mesh-sec warn"><h4>Single points of failure</h4><ul>` + (c.singlePointsOfFailure.length ? c.singlePointsOfFailure.map(s =>
            `<li><b>${esc(s.name)}</b> <span class="muted">would cut off ${s.wouldCutOff.map(n => esc(n.name)).join(', ') || 'nothing'}</span></li>`).join('') : '<li class="muted">none — every node has a second path</li>') + '</ul></div>';
        $('mesh-connectivity').innerHTML = h;
    }

    function fillNodeList() {
        const dl = $('mesh-node-list'); if (!dl) return;
        dl.innerHTML = M.nodes.map(n => `<option value="${esc(n.name)}">${esc(n.mac || n.id)}</option>`).join('');
    }

    async function pollTopology(force) {
        const [t, c, n] = await Promise.all([api('/topology?algorithm=' + M.algo), api('/connectivity'), api('/nodes')]);
        if (!t.ok) return;
        const tj = JSON.stringify(t.data), cj = JSON.stringify(c.data), nj = JSON.stringify(n.data);
        M.topo = t.data; M.conn = c.data;
        if (n.ok && nj !== M.nodesJson) { M.nodes = n.data; M.nodesJson = nj; fillNodeList(); renderRouteChips(); }
        if (!force && tj === M.topoJson && cj === M.connJson) return;     // nothing changed → skip re-render
        M.topoJson = tj; M.connJson = cj;
        renderKpis(t.data.kpis);
        drawMap($('mesh-map'), t.data, { showTree: M.showTree, selectable: true });
        renderTreeNote(t.data.tree);
        renderInspector();
        if (c.ok) renderConnectivity(c.data);
    }

    // ═══ Routes tab ════════════════════════════════════════════
    function renderRouteChips() {
        const el = $('mesh-route-chips'); if (!el) return;
        const rank = n => n.status === 'Down' ? 0 : n.status === 'Isolated' ? 1 : 2;
        const items = [...M.nodes].filter(n => n.status !== 'Gateway').sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name));
        el.innerHTML = items.map(n => `<button class="chip-btn ${n.status.toLowerCase()} ${M.routeId === n.id ? 'active' : ''}" data-mesh="chip" data-id="${esc(n.id)}" title="${esc(n.status)}">${esc(n.name)}</button>`).join('');
    }

    function pathCell(p) {
        if (!p.found) return '<span class="muted">no route</span>';
        return `<span class="mesh-route-path">${p.names.map(n => `<span class="hop">${esc(n)}</span>`).join(' <span class="arrow">→</span> ')}</span>`;
    }

    async function pollRoute(force) {
        if (!M.routeId) return;
        const r = await api('/routes/' + encodeURIComponent(M.routeId));
        const msg = $('mesh-route-msg');
        if (!r.ok) { msg.textContent = (r.data && r.data.error) || 'Could not load route.'; return; }
        msg.textContent = '';
        const rj = JSON.stringify(r.data) + '|' + (M.topoJson.length);
        if (!force && rj === M.routeJson) return;
        M.routeJson = rj;
        const d = r.data;
        const row = (key, label, p, cls) => `<tr class="${cls || ''}${d.best === key ? ' best' : ''}"><td>${label}${d.best === key ? ' ★' : ''}</td><td>${pathCell(p)}</td><td>${p.found ? p.hops : '—'}</td><td>${p.cost != null ? p.cost.toFixed(2) : '—'}</td></tr>`;
        $('mesh-route-table').querySelector('tbody').innerHTML =
            row('mst', 'MST path (' + (M.algo === 'prim' ? 'Prim' : 'Kruskal') + ')', d.mst) + row('dijkstra', 'Dijkstra (cheapest for this node)', d.dijkstra) +
            row('bfs', 'BFS (fewest hops)', d.bfs) + row('pre', 'MST before any outage', d.preOutage, 'pre');
        $('mesh-failover-note').textContent = d.failoverNote || '';
        if (M.topo) {
            const differ = d.dijkstra.found && d.mst.found && d.dijkstra.path.join() !== d.mst.path.join();
            drawMap($('mesh-map-route'), M.topo, { treePath: d.mst.found ? d.mst.path : (d.preOutage.found ? [] : []), dijPath: differ ? d.dijkstra.path : null, selectable: false });
        }
    }

    function trackRoute(id) {
        M.routeId = id; if (id && !M.recent.includes(id)) M.recent.unshift(id);
        save(); M.routeJson = '';
        $('mesh-route-input').value = (M.nodes.find(n => n.id === id) || {}).name || id;
        renderRouteChips();
        return pollRoute(true);
    }

    // ═══ History tab ═══════════════════════════════════════════
    const pad = n => String(n).padStart(2, '0');
    const toLocalInput = d => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
    const fmtTime = iso => new Date(iso).toLocaleString([], { month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' });

    function fillHistorySelect() {
        const sel = $('mesh-hist-node'); if (!sel) return;
        const cand = M.nodes.filter(n => n.status !== 'Gateway');
        const order = n => (n.category === 'Actuator' ? 1 : 0);                    // sensors & meters first
        cand.sort((a, b) => order(a) - order(b) || a.name.localeCompare(b.name));
        const key = cand.map(n => n.id).join();
        if (sel.dataset.key === key) return;
        sel.dataset.key = key;
        sel.innerHTML = cand.map(n => `<option value="${esc(n.id)}">${esc(n.name)}${n.category === 'Actuator' ? ' (actuator)' : ''}</option>`).join('');
        if (!M.hist.node || !cand.some(n => n.id === M.hist.node)) M.hist.node = cand[0] && cand[0].id;
        sel.value = M.hist.node || '';
    }

    function setPreset(minutes) {
        const to = new Date(), from = new Date(to.getTime() - minutes * 60000);
        $('mesh-hist-from').value = toLocalInput(from); $('mesh-hist-to').value = toLocalInput(to);
        M.hist.from = $('mesh-hist-from').value; M.hist.to = $('mesh-hist-to').value; M.hist.preset = minutes;
        document.querySelectorAll('#mesh-hist-presets .chip-btn').forEach(b => b.classList.toggle('active', +b.dataset.minutes === minutes));
    }

    async function runHistory(page) {
        const strip = $('mesh-hist-strip');
        M.hist.node = $('mesh-hist-node').value; M.hist.from = $('mesh-hist-from').value; M.hist.to = $('mesh-hist-to').value;
        if (page) M.hist.page = page;
        if (!M.hist.node) { strip.className = 'mesh-strip err'; strip.textContent = 'Pick a sensor first.'; return; }
        const f = M.hist.from ? new Date(M.hist.from) : null, t = M.hist.to ? new Date(M.hist.to) : null;
        if ((f && isNaN(f)) || (t && isNaN(t))) { strip.className = 'mesh-strip err'; strip.textContent = 'That date is not valid.'; return; }
        if (f && t && f > t) { strip.className = 'mesh-strip err'; strip.textContent = '“From” must not be later than “To”.'; return; }
        const q = new URLSearchParams({ page: M.hist.page, pageSize: M.hist.pageSize });
        if (f) q.set('from', f.toISOString()); if (t) q.set('to', t.toISOString());
        const r = await api(`/nodes/${encodeURIComponent(M.hist.node)}/history?` + q);
        if (!r.ok) { strip.className = 'mesh-strip err'; strip.textContent = (r.data && r.data.error) || 'Query failed.'; return; }
        M.hist.last = r.data; M.hist.page = r.data.page; save();
        renderHistory(r.data);
    }

    function renderHistory(d) {
        const tr = d.tree, strip = $('mesh-hist-strip');
        strip.className = 'mesh-strip';
        strip.textContent = `matched ${d.matched}, visited ${tr.nodesVisited} of ${tr.size} tree nodes, height ${tr.height} (minimum ${tr.optimalHeight}), ${tr.elapsedMicros < 1000 ? tr.elapsedMicros + ' µs' : (tr.elapsedMicros / 1000).toFixed(2) + ' ms'}`;
        $('mesh-hist-title').textContent = d.name;
        const s = d.stats, u = d.thresholds.unit;
        $('mesh-hist-stats').textContent = s.count ? (d.thresholds.isStateDevice ? `${s.count} reports · on ${(100 * s.avg).toFixed(0)}% of the time` : `min ${s.min}${u} · avg ${s.avg}${u} · max ${s.max}${u}`) : 'no readings in this window';
        drawChart(d);
        const st = d.thresholds.isStateDevice;
        $('mesh-hist-table').querySelector('tbody').innerHTML = d.items.length ? d.items.map(p =>
            `<tr><td>${esc(fmtTime(p.timestamp))}</td><td>${st ? (p.value >= 0.5 ? 'ON' : 'OFF') : p.value + esc(u)}</td><td>${p.signal} dBm</td></tr>`).join('') : '<tr><td colspan="3" class="empty-note">No readings in this window.</td></tr>';
        $('mesh-hist-page').textContent = `Page ${d.page} of ${d.totalPages}`;
        document.querySelector('[data-mesh="page-prev"]').disabled = d.page <= 1;
        document.querySelector('[data-mesh="page-next"]').disabled = d.page >= d.totalPages;
    }

    function drawChart(d) {
        const svg = $('mesh-hist-chart'), W = 720, H = 260, L = 52, R = 16, T = 14, B = 34;
        const pts = d.series;
        if (!pts.length) { svg.innerHTML = `<text x="${W / 2}" y="${H / 2}" text-anchor="middle" class="chart-tick">No data in this window</text>`; return; }
        const st = d.thresholds.isStateDevice, lo = d.thresholds.criticalLow, hi = d.thresholds.criticalHigh;
        const xs = pts.map(p => new Date(p.timestamp).getTime()), ys = pts.map(p => p.value);
        let y0 = st ? 0 : Math.min(...ys), y1 = st ? 1 : Math.max(...ys);
        if (!st) { if (lo != null) y0 = Math.min(y0, lo); if (hi != null) y1 = Math.max(y1, hi); const padY = (y1 - y0 || 1) * 0.08; y0 -= padY; y1 += padY; }
        const x0 = xs[0], x1 = xs[xs.length - 1] === xs[0] ? xs[0] + 1 : xs[xs.length - 1];
        const X = x => L + (x - x0) / (x1 - x0) * (W - L - R), Y = y => T + (1 - (y - y0) / (y1 - y0 || 1)) * (H - T - B);
        let h = '';
        const ticks = st ? [0, 1] : [0, 1, 2, 3].map(i => y0 + (y1 - y0) * i / 3);
        for (const v of ticks) h += `<line class="chart-grid" x1="${L}" x2="${W - R}" y1="${Y(v)}" y2="${Y(v)}"/><text class="chart-tick" x="${L - 6}" y="${Y(v) + 3}" text-anchor="end">${st ? (v ? 'ON' : 'OFF') : v.toFixed(v > 100 ? 0 : 1)}</text>`;
        for (let i = 0; i <= 4; i++) { const x = x0 + (x1 - x0) * i / 4; h += `<text class="chart-tick" x="${X(x)}" y="${H - 12}" text-anchor="${i === 0 ? 'start' : i === 4 ? 'end' : 'middle'}">${new Date(x).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</text>`; }
        h += `<line class="chart-axis" x1="${L}" x2="${L}" y1="${T}" y2="${H - B}"/><line class="chart-axis" x1="${L}" x2="${W - R}" y1="${H - B}" y2="${H - B}"/>`;
        if (!st) {
            if (hi != null) h += `<line class="chart-thr" x1="${L}" x2="${W - R}" y1="${Y(hi)}" y2="${Y(hi)}"/><text class="chart-thr-label" x="${W - R - 4}" y="${Y(hi) - 4}" text-anchor="end">critical high ${hi}${d.thresholds.unit}</text>`;
            if (lo != null) h += `<line class="chart-thr" x1="${L}" x2="${W - R}" y1="${Y(lo)}" y2="${Y(lo)}"/><text class="chart-thr-label" x="${W - R - 4}" y="${Y(lo) - 4}" text-anchor="end">critical low ${lo}${d.thresholds.unit}</text>`;
        }
        let path = '';
        if (st) {                                                  // actuators: a STEP line, state holds until the next report
            pts.forEach((p, i) => { const px = X(xs[i]), py = Y(p.value); path += i === 0 ? `M${px},${py}` : `H${px}V${py}`; });
        } else pts.forEach((p, i) => { path += (i === 0 ? 'M' : 'L') + X(xs[i]).toFixed(1) + ',' + Y(p.value).toFixed(1); });
        h += `<path class="chart-line" d="${path}"/>`;
        svg.innerHTML = h;
    }

    // ═══ Benchmarks ════════════════════════════════════════════
    async function runBench(kind) {
        const out = $('mesh-bench-out'); out.innerHTML = '<div class="empty-note">Running…</div>';
        const r = await api(kind === 'tree' ? '/benchmark/tree' : '/benchmark/graph');
        if (!r.ok) { out.innerHTML = '<div class="empty-note">Benchmark failed.</div>'; return; }
        const d = r.data, ok = v => v ? '<span class="bench-ok">✓ yes</span>' : '✗ no';
        if (kind === 'tree') {
            out.innerHTML = `<table class="data-table bench-table"><caption>AVL tree vs linear scan — ${d.points.toLocaleString()} readings, ${d.queries} random ${d.windowHours}-hour windows</caption>
              <thead><tr><th>Method</th><th>Total</th><th>Per query</th><th>Readings examined / query</th></tr></thead><tbody>
              <tr><td>AVL range query</td><td>${d.avl.totalMs} ms</td><td>${d.avl.msPerQuery} ms</td><td>${d.avl.avgNodesVisited} (≈ ${d.avgMatchesPerQuery} matches + O(log n))</td></tr>
              <tr><td>Linear list scan</td><td>${d.linearScan.totalMs} ms</td><td>${d.linearScan.msPerQuery} ms</td><td>${d.linearScan.readingsExaminedPerQuery.toLocaleString()}</td></tr>
              <tr><td colspan="4"><b>${d.speedup}× faster</b> · results identical: ${ok(d.identical)} · AVL height ${d.avl.height} (minimum ${d.avl.optimalHeight}) · valid: ${ok(d.avl.valid)}</td></tr></tbody></table>
              <table class="data-table bench-table"><caption>Why balancing matters — ${d.degeneration.sortedInserts.toLocaleString()} sorted inserts</caption>
              <thead><tr><th>Plain BST height</th><th>AVL height</th><th>Optimal</th></tr></thead><tbody><tr><td><b>${d.degeneration.plainBstHeight.toLocaleString()}</b></td><td><b>${d.degeneration.avlHeight}</b></td><td>${d.degeneration.avlOptimalHeight}</td></tr></tbody></table>`;
        } else {
            const row = (n, o, extra) => `<tr><td>${n}</td><td>${o.ms} ms</td><td>${extra}</td></tr>`;
            out.innerHTML = `<table class="data-table bench-table"><caption>Graph algorithms — random connected mesh, ${d.nodes.toLocaleString()} nodes, ${d.edges.toLocaleString()} links</caption>
              <thead><tr><th>Algorithm</th><th>Time</th><th>Result</th></tr></thead><tbody>
              ${row('Prim (PriorityQueue)', d.prim, `cost ${d.prim.cost} · ${d.prim.edges} edges`)}${row('Kruskal (union-find)', d.kruskal, `cost ${d.kruskal.cost} · ${d.kruskal.edges} edges`)}
              ${row('BFS (Queue)', d.bfs, `reached ${d.bfs.reached} · max ${d.bfs.maxHops} hops`)}${row('DFS (Stack)', d.dfs, `reached ${d.dfs.reached}`)}${row('Dijkstra', d.dijkstra, `reached ${d.dijkstra.reached}`)}
              ${row('Articulation points (Tarjan)', d.articulationPoints, `${d.articulationPoints.count} single points of failure`)}
              <tr><td colspan="3">Prim and Kruskal costs agree: ${ok(d.costsAgree)} · both spanning trees have n−1 edges: ${ok(d.spanningTreeEdges)}</td></tr></tbody></table>`;
        }
    }

    // ═══ Actions ═══════════════════════════════════════════════
    async function action(name, el) {
        if (name === 'fail' || name === 'recover') {
            if (!M.selected) return;
            const r = await post(`/nodes/${encodeURIComponent(M.selected)}/${name}`);
            if (r.ok) toast(name === 'fail' ? `${r.data.node} failed — mesh re-routed` : `${r.data.node} is back ${r.data.status === 'Up' ? 'online' : '(still down: ' + r.data.stillDownBecause + ')'}`, name === 'fail' ? 'info' : 'success');
            else toast((r.data && r.data.error) || 'Request failed', 'error');
            return pollTopology(true);
        }
        if (name === 'restore') { const r = await post('/outages/reset'); toast(r.ok ? `Restored ${r.data.cleared} node(s)` : 'Could not restore', r.ok ? 'success' : 'error'); return pollTopology(true); }
        if (name === 'algo') { M.algo = el.value; save(); return pollTopology(true); }
        if (name === 'show-tree') { M.showTree = el.checked; save(); if (M.topo) drawMap($('mesh-map'), M.topo, { showTree: M.showTree, selectable: true }); return; }
        if (name === 'add-link') {
            const msg = $('mesh-link-msg'), a = $('mesh-link-a').value.trim(), b = $('mesh-link-b').value.trim(), rssi = parseInt($('mesh-link-rssi').value, 10);
            if (!a || !b || isNaN(rssi)) { msg.textContent = 'Enter both nodes and an RSSI.'; return; }
            const r = await post('/links', { a, b, rssi });
            msg.textContent = r.ok ? `Linked ${r.data.link.aName} ↔ ${r.data.link.bName} (cost ${r.data.link.weight.toFixed(2)})` : ((r.data && r.data.error) || 'Failed');
            if (r.ok) { toast('Link added', 'success'); return pollTopology(true); } else toast(msg.textContent, 'error');
            return;
        }
        if (name === 'track') { const v = $('mesh-route-input').value.trim(); if (!v) { $('mesh-route-msg').textContent = 'Type a node first.'; return; } return trackRoute(v); }
        if (name === 'chip') return trackRoute(el.dataset.id);
        if (name === 'track-sel') { navigate('mesh', 'routes'); return trackRoute(M.selected); }
        if (name === 'hist-sel') { M.hist.node = M.selected; save(); navigate('mesh', 'history'); return; }
        if (name === 'preset') { setPreset(+el.dataset.minutes); M.hist.page = 1; return runHistory(1); }
        if (name === 'hist-run') { M.hist.page = 1; return runHistory(1); }
        if (name === 'page-prev') return runHistory(Math.max(1, M.hist.page - 1));
        if (name === 'page-next') return runHistory(M.hist.page + 1);
        if (name === 'bench-tree') return runBench('tree');
        if (name === 'bench-graph') return runBench('graph');
    }

    function selectNode(id) {
        M.selected = id; save();
        if (M.topo) drawMap($('mesh-map'), M.topo, { showTree: M.showTree, selectable: true });
        renderInspector();
        const n = M.topo && M.topo.nodes.find(x => x.id === id);
        if (n && n.status === 'Unlinked') $('mesh-link-a').value = n.name;
    }

    function wire() {
        const root = document.querySelector('[id^="tab-mesh-"]').parentElement;
        root.addEventListener('click', e => {
            const nodeEl = e.target.closest('#mesh-map .mesh-node'); if (nodeEl) return selectNode(nodeEl.dataset.id);
            const b = e.target.closest('[data-mesh]'); if (!b || b.tagName === 'SELECT' || b.type === 'checkbox') return;
            action(b.dataset.mesh, b);
        });
        root.addEventListener('keydown', e => {
            if (e.key !== 'Enter' && e.key !== ' ') return;
            const nodeEl = e.target.closest('#mesh-map .mesh-node'); if (nodeEl) { e.preventDefault(); selectNode(nodeEl.dataset.id); return; }
            if (e.key === 'Enter' && e.target.id === 'mesh-route-input') action('track');
        });
        root.addEventListener('change', e => { const b = e.target.closest('[data-mesh]'); if (b && (b.tagName === 'SELECT' || b.type === 'checkbox')) action(b.dataset.mesh, b); });
    }

    // ═══ Lifecycle + polling ═══════════════════════════════════
    async function tick() {
        M.timer = null;
        if (!M.running || M.inflight) return;
        M.inflight = true;
        try {
            await pollTopology(false);
            if (M.tab === 'routes') await pollRoute(false);
            M.polls++;
        } catch { /* transient — retry on the next tick */ }
        M.inflight = false;
        if (M.running) M.timer = setTimeout(tick, POLL_MS);        // chained: next poll only after this one finished
    }

    let wired = false;
    async function onShow(tab) {
        if (!wired) { load(); wire(); wired = true; $('mesh-algo').value = M.algo; $('mesh-show-tree').checked = M.showTree; }
        M.tab = tab || 'topology';
        const was = M.running; M.running = true;
        await pollTopology(true);
        if (M.tab === 'routes') { if (M.routeId) { const n = M.nodes.find(x => x.id === M.routeId); $('mesh-route-input').value = n ? n.name : M.routeId; await pollRoute(true); } renderRouteChips(); }
        if (M.tab === 'history') {
            fillHistorySelect();
            if (M.hist.node) $('mesh-hist-node').value = M.hist.node;
            if (M.hist.from && M.hist.to) { $('mesh-hist-from').value = M.hist.from; $('mesh-hist-to').value = M.hist.to; } else setPreset(M.hist.preset || 120);
            await runHistory(M.hist.page || 1);
        }
        if (!was || !M.timer) { clearTimeout(M.timer); M.timer = setTimeout(tick, POLL_MS); }
    }
    function onHide() { M.running = false; clearTimeout(M.timer); M.timer = null; save(); }

    window.MESH = { onShow, onHide, state: M };
})();
