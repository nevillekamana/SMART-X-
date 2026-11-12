// Smart-X final PoE — UI smoke test.
// Loads the REAL dashboard (index.html + dashboard.js + commandStream.js + mesh.js) in jsdom and drives it against a
// running API. Usage:  npm install && node ui.js [base_url]      (default http://localhost:59185)
// Nothing is mocked except what jsdom lacks (fetch → Node's fetch, canvas 2D context → no-op).
const { JSDOM, VirtualConsole } = require('jsdom');
const BASE = process.argv[2] || 'http://localhost:59185';
let pass = 0, fail = 0;
const ok = (name, cond, extra) => { if (cond) { pass++; console.log('  PASS  ' + name); } else { fail++; console.log('  FAIL  ' + name + (extra !== undefined ? '   (got: ' + extra + ')' : '')); } };
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function main() {
    const errors = [];
    const vc = new VirtualConsole();
    vc.on('jsdomError', e => errors.push(String(e.message || e).slice(0, 160)));
    const dom = await JSDOM.fromURL(BASE + '/', {
        runScripts: 'dangerously', resources: 'usable', pretendToBeVisual: true, virtualConsole: vc,
        beforeParse(w) {
            w.fetch = (u, o) => fetch(new URL(u, BASE + '/'), o);
            const noop = new Proxy(function () { }, { get: () => noop, apply: () => noop });
            w.HTMLCanvasElement.prototype.getContext = () => noop;
            w.scrollTo = () => { };
        }
    });
    const w = dom.window, d = w.document;
    const $ = s => d.querySelector(s), $$ = s => Array.from(d.querySelectorAll(s));
    const waitFor = async (fn, ms = 8000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { try { const v = fn(); if (v) return v; } catch { } await sleep(60); } return null; };
    const click = el => el.dispatchEvent(new w.MouseEvent('click', { bubbles: true }));
    const nodeEl = name => $$('#mesh-map .mesh-node').find(g => (g.getAttribute('aria-label') || '').startsWith(name + ','));
    const api = async p => (await fetch(BASE + '/api/mesh' + p)).json();
    await fetch(BASE + '/api/mesh/outages/reset', { method: 'POST' });

    await waitFor(() => d.readyState === 'complete' && typeof w.navigate === 'function' && w.MESH);

    console.log('== 1. Start screen + navigation');
    const cards = $$('.pillar-card');
    ok('three pillar cards on the start screen', cards.length === 3, cards.length);
    ok('third pillar is active (not disabled)', cards[2].classList.contains('active') && !cards[2].classList.contains('disabled'));
    ok('sidebar module button for the mesh is enabled', !$('.module-btn[data-module="mesh"]').disabled);
    click(cards[2]);
    ok('clicking it opens the app shell', await waitFor(() => !$('#app-shell').classList.contains('hidden')));
    ok('hash route is #/mesh/topology', w.location.hash === '#/mesh/topology', w.location.hash);
    ok('nav-mesh is the visible sidebar nav', !$('#nav-mesh').classList.contains('hidden') && $('#nav-telemetry').classList.contains('hidden'));
    ok('three mesh tabs in the sidebar', $$('#nav-mesh button').length === 3);

    console.log('== 2. Topology tab');
    ok('map renders 14 nodes', await waitFor(() => $$('#mesh-map .mesh-node').length === 14), $$('#mesh-map .mesh-node').length);
    ok('six KPI cards', $$('#mesh-kpis .mesh-kpi').length === 6, $$('#mesh-kpis .mesh-kpi').length);
    ok('13 bold tree links', $$('#mesh-map line.mesh-link.tree').length === 13, $$('#mesh-map line.mesh-link.tree').length);
    ok('weight labels in background pills', $$('#mesh-map .mesh-pill rect').length === 13);
    ok('nodes are keyboard-focusable', nodeEl('Valve 1').getAttribute('tabindex') === '0');
    ok('Fan 1 carries the amber single-point-of-failure dot', !!nodeEl('Fan 1').querySelector('.spof'));
    ok('tree note says Prim and Kruskal are identical', /identical/.test($('#mesh-tree-note').textContent), $('#mesh-tree-note').textContent);
    ok('connectivity card shows BFS levels', /Level 1/.test($('#mesh-connectivity').textContent));

    console.log('== 3. Selection + failure simulation');
    click(nodeEl('Valve 1'));
    ok('inspector shows the selected node', /Valve 1/.test($('#mesh-inspector').textContent) && /Up/.test($('#mesh-inspector').textContent));
    ok('Bring back online is disabled while the node is up', $('[data-mesh="recover"]').disabled);
    click($('[data-mesh="fail"]'));
    ok('Valve 1 turns Down on the map', await waitFor(() => nodeEl('Valve 1').classList.contains('down')));
    ok('Offline KPI shows 1', await waitFor(() => /Offline\s*1/.test($('#mesh-kpis').textContent.replace(/\s+/g, ' ').replace(/OFFLINE/i, 'Offline'))), $('#mesh-kpis').textContent);
    ok('connectivity lists the failover hop for Pump B', await waitFor(() => /Pump B: Valve 1 → Fan 1/.test($('#mesh-connectivity').textContent)), $('#mesh-connectivity').textContent.slice(0, 200));
    ok('Bring back online is now enabled', !$('[data-mesh="recover"]').disabled);
    const live = await api('/topology');
    ok('server agrees: Valve 1 is Down', live.nodes.find(n => n.name === 'Valve 1').status === 'Down');

    console.log('== 4. Prim / Kruskal switch');
    const costBefore = /cost ([\d.]+)/.exec($('#mesh-tree-note').textContent)[1];
    const sel = $('#mesh-algo'); sel.value = 'kruskal'; sel.dispatchEvent(new w.Event('change', { bubbles: true }));
    ok('tree note switches to Kruskal', await waitFor(() => /^Kruskal/.test($('#mesh-tree-note').textContent.trim())), $('#mesh-tree-note').textContent);
    ok('same total cost under Kruskal', /cost ([\d.]+)/.exec($('#mesh-tree-note').textContent)[1] === costBefore);
    const tg = $('#mesh-show-tree'); tg.checked = false; tg.dispatchEvent(new w.Event('change', { bubbles: true }));
    ok('show-tree toggle hides bold links', await waitFor(() => $$('#mesh-map line.mesh-link.tree').length === 0));
    tg.checked = true; tg.dispatchEvent(new w.Event('change', { bubbles: true }));
    ok('and shows them again', await waitFor(() => $$('#mesh-map line.mesh-link.tree').length > 0));

    console.log('== 5. Module round trip preserves state');
    w.navigate('commands', 'live');
    ok('Part 2 module opens', await waitFor(() => !$('#nav-commands').classList.contains('hidden') && $('#tab-cs-live').classList.contains('active')));
    ok('mesh polling stopped while hidden', w.MESH.state.running === false && w.MESH.state.timer === null);
    w.navigate('telemetry', 'overview');
    ok('Part 1 module opens', await waitFor(() => $('#tab-overview').classList.contains('active')));
    w.navigate('mesh');
    ok('back on the mesh topology tab', await waitFor(() => $('#tab-mesh-topology').classList.contains('active')));
    ok('algorithm still Kruskal', $('#mesh-algo').value === 'kruskal' && /^Kruskal/.test($('#mesh-tree-note').textContent.trim()));
    ok('selected node still Valve 1', /Valve 1/.test($('#mesh-inspector').textContent));
    ok('outage still shown after the round trip', await waitFor(() => nodeEl('Valve 1').classList.contains('down')));
    ok('polling resumed', w.MESH.state.running === true);
    const saved = JSON.parse(w.sessionStorage.getItem('smartx.mesh') || '{}');
    ok('state mirrored to sessionStorage', saved.algo === 'kruskal' && !!saved.selected, JSON.stringify(saved));
    const p0 = w.MESH.state.polls; await waitFor(() => w.MESH.state.polls > p0, 8000);
    ok('chained polling keeps ticking', w.MESH.state.polls > p0);

    console.log('== 6. Isolation via Fan 1 (UI)');
    await fetch(BASE + '/api/mesh/outages/reset', { method: 'POST' });
    click(nodeEl('Fan 1')); await waitFor(() => /Fan 1/.test($('#mesh-inspector').textContent));
    click($('[data-mesh="fail"]'));
    ok('Greenhouse Temp 1 is cut off', await waitFor(() => nodeEl('Greenhouse Temp 1').classList.contains('isolated')));
    ok('relay suggestion shown', await waitFor(() => /relay via/i.test($('#mesh-connectivity').textContent)), $('#mesh-connectivity').textContent.slice(0, 300));
    click($('#tab-mesh-topology [data-mesh="restore"]'));
    ok('Restore all nodes heals the mesh', await waitFor(() => !nodeEl('Fan 1').classList.contains('down') && !nodeEl('Greenhouse Temp 1').classList.contains('isolated')));

    console.log('== 7. Route tracking');
    click(nodeEl('Valve 1')); click($('[data-mesh="fail"]')); await waitFor(() => nodeEl('Valve 1').classList.contains('down'));
    w.navigate('mesh', 'routes');
    ok('routes tab visible', await waitFor(() => $('#tab-mesh-routes').classList.contains('active')));
    ok('datalist has every node', await waitFor(() => $$('#mesh-node-list option').length >= 14), $$('#mesh-node-list option').length);
    ok('chips for each node', $$('#mesh-route-chips .chip-btn').length >= 13);
    $('#mesh-route-input').value = 'Moisture Sensor A'; click($('[data-mesh="track"]'));
    ok('comparison table fills (4 rows)', await waitFor(() => $$('#mesh-route-table tbody tr').length === 4 && /Moisture Sensor A/.test($('#mesh-route-table').textContent)));
    ok('best-cost row is marked', $$('#mesh-route-table tr.best').length >= 1 && /★/.test($('#mesh-route-table').textContent));
    ok('failover note mentions the energy delta', /Rerouted around Valve 1.*Energy \+/.test($('#mesh-failover-note').textContent), $('#mesh-failover-note').textContent);
    ok('route highlighted on the second map', $$('#mesh-map-route line.mesh-link.hl').length >= 3, $$('#mesh-map-route line.mesh-link.hl').length);
    click($$('#mesh-route-chips .chip-btn').find(b => b.textContent === 'Pump B'));
    ok('a chip tracks another node', await waitFor(() => /Pump B/.test($('#mesh-route-table').textContent)));
    await fetch(BASE + '/api/mesh/outages/reset', { method: 'POST' });

    console.log('== 8. History query + paging');
    w.navigate('mesh', 'history');
    ok('history tab visible', await waitFor(() => $('#tab-mesh-history').classList.contains('active')));
    await waitFor(() => $$('#mesh-hist-node option').length >= 13);
    const opts = $$('#mesh-hist-node option').map(o => o.textContent);
    ok('sensors and meters are listed before actuators', opts.findIndex(o => /actuator/.test(o)) > opts.findIndex(o => /Meter/.test(o)) && opts.findIndex(o => /Meter/.test(o)) >= 0, opts.join('|'));
    $('#mesh-hist-node').value = $$('#mesh-hist-node option').find(o => /Smart Meter 1/.test(o.textContent)).value;
    click($('[data-minutes="120"]'));
    ok('strip shows matched / visited / height', await waitFor(() => /matched \d+, visited \d+ of \d+ tree nodes, height \d+ \(minimum \d+\)/.test($('#mesh-hist-strip').textContent)), $('#mesh-hist-strip').textContent);
    ok('chart drawn with a line and a threshold line', !!$('#mesh-hist-chart path.chart-line') && !!$('#mesh-hist-chart line.chart-thr'));
    ok('log shows 15 rows (page size)', $$('#mesh-hist-table tbody tr').length === 15, $$('#mesh-hist-table tbody tr').length);
    const firstRow = $('#mesh-hist-table tbody tr').textContent;
    click($('[data-mesh="page-next"]'));
    ok('next page loads', await waitFor(() => /Page 2 of/.test($('#mesh-hist-page').textContent)));
    ok('page 2 differs from page 1', $('#mesh-hist-table tbody tr').textContent !== firstRow);
    $('#mesh-hist-node').value = $$('#mesh-hist-node option').find(o => /Fan 1/.test(o.textContent)).value;
    click($('[data-mesh="hist-run"]'));
    ok('actuator history renders (step line, ON/OFF axis)', await waitFor(() => /OFF/.test($('#mesh-hist-chart').textContent) && /H[\d.]+V/.test($('#mesh-hist-chart path.chart-line').getAttribute('d'))));
    $('#mesh-hist-from').value = '2030-01-02T00:00'; $('#mesh-hist-to').value = '2030-01-01T00:00'; click($('[data-mesh="hist-run"]'));
    ok('reversed dates are rejected in the UI', await waitFor(() => $('#mesh-hist-strip').classList.contains('err')), $('#mesh-hist-strip').textContent);

    console.log('== 9. Benchmarks');
    click($('[data-mesh="bench-tree"]'));
    ok('tree benchmark table appears', await waitFor(() => /faster/.test($('#mesh-bench-out').textContent) && /identical/.test($('#mesh-bench-out').textContent), 20000), $('#mesh-bench-out').textContent.slice(0, 120));
    ok('degeneration demo shows 3,000 vs 12', /3,000/.test($('#mesh-bench-out').textContent) && />12</.test($('#mesh-bench-out').innerHTML));
    click($('[data-mesh="bench-graph"]'));
    ok('graph benchmark table appears', await waitFor(() => /Prim \(PriorityQueue\)/.test($('#mesh-bench-out').textContent) && /Kruskal/.test($('#mesh-bench-out').textContent), 20000));
    ok('Prim and Kruskal costs agree in the table', /costs agree: ✓ yes/.test($('#mesh-bench-out').textContent));

    console.log('== 10. Page health');
    ok('no script errors in the page', errors.length === 0, errors.slice(0, 3).join(' | '));

    await fetch(BASE + '/api/mesh/outages/reset', { method: 'POST' });
    console.log(`\nResult: ${pass} passed, ${fail} failed`);
    w.close();
    process.exit(fail ? 1 : 0);
}
main().catch(e => { console.error('UI smoke crashed:', e); process.exit(2); });
