// ═══════════════════════════════════════════════════════
//  SMART-X GATEWAY DASHBOARD — Client Logic
// ═══════════════════════════════════════════════════════

const API_BASE = '/api';

// ─── State ───
let registeredSensors = [];
let sensorDataCache = [];
let deploymentNodesById = {};
let selectedNodeId = null;
let selectedOperator = 'add';
let recentOperations = [];
let sessionPacketCount = 0;
let pulseTimeout = null;

// ═══════════════════════════════════════════════
//  STARTUP / PLATFORM SELECTION
// ═══════════════════════════════════════════════

function activatePillar(pillar) {
    if (pillar !== 'telemetry') return; // other pillars are disabled for this build
    document.getElementById('startup-screen').classList.add('hidden');
    document.getElementById('app-shell').classList.remove('hidden');
    loadOverview();
    loadSensors();
    loadTelemetryFeed();
}

function showStartup() {
    document.getElementById('app-shell').classList.add('hidden');
    document.getElementById('startup-screen').classList.remove('hidden');
}

// Startup screen pulse-ring animation
(function initStartupPulse() {
    const canvas = document.getElementById('pulse-canvas');
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    let rings = [];
    let frame = 0;
    const accentColor = '63, 77, 33'; // matches --accent (olive)

    function drawStartup() {
        const w = canvas.width, h = canvas.height;
        const cx = w / 2, cy = h / 2;
        ctx.clearRect(0, 0, w, h);

        if (frame % 40 === 0) {
            rings.push({ r: 20, alpha: 0.55, speed: 0.5 + Math.random() * 0.5 });
        }

        rings = rings.filter(ring => ring.alpha > 0.01);
        rings.forEach(ring => {
            ring.r += ring.speed;
            ring.alpha *= 0.98;
            ctx.beginPath();
            ctx.arc(cx, cy, ring.r, 0, Math.PI * 2);
            ctx.strokeStyle = `rgba(${accentColor}, ${ring.alpha})`;
            ctx.lineWidth = 1.5;
            ctx.stroke();
        });

        const pulse = 0.5 + 0.5 * Math.sin(frame * 0.05);
        ctx.beginPath();
        ctx.arc(cx, cy, 6 + pulse * 4, 0, Math.PI * 2);
        ctx.fillStyle = `rgba(${accentColor}, ${0.55 + pulse * 0.35})`;
        ctx.fill();

        frame++;
        requestAnimationFrame(drawStartup);
    }

    drawStartup();
})();

// ═══════════════════════════════════════════════
//  NAVIGATION
// ═══════════════════════════════════════════════

function switchTab(tabName) {
    document.querySelectorAll('.side-nav .nav-btn').forEach(b => b.classList.remove('active'));
    document.querySelectorAll('.tab-panel').forEach(p => p.classList.remove('active'));
    document.querySelector(`.side-nav .nav-btn[data-tab="${tabName}"]`).classList.add('active');
    document.getElementById(`tab-${tabName}`).classList.add('active');

    if (tabName === 'overview') loadOverview();
    if (tabName === 'sensors') loadSensors();
    if (tabName === 'telemetry') loadTelemetryFeed();
    if (tabName === 'deployment') loadDeploymentTree();
    if (tabName === 'operator') refreshOperatorDropdowns();
    if (tabName === 'history') loadHistory();
}

function init() {
    updateSyncLabel();
    setInterval(updateSyncLabel, 30000);
}
document.addEventListener('DOMContentLoaded', init);

function updateSyncLabel() {
    const el = document.getElementById('sidebar-sync');
    if (el) el.textContent = 'SYNC ' + new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

// ═══════════════════════════════════════════════
//  NETWORK ACTIVITY INDICATOR
// ═══════════════════════════════════════════════

function triggerPulse() {
    sessionPacketCount++;
    const status = document.getElementById('packets-status');
    const sub = document.getElementById('packets-sub');
    const sessionLabel = document.getElementById('session-packets');
    if (status) status.textContent = 'ACTIVE';
    if (sub) sub.textContent = `${registeredSensors.length ? Math.min(registeredSensors.length, 6) : 0} sensors active across the gateway`;
    if (sessionLabel) sessionLabel.textContent = `Current session · ${sessionPacketCount} packets`;

    const chipSession = document.getElementById('chip-session-activity');
    if (chipSession) chipSession.textContent = 'Receiving';

    clearTimeout(pulseTimeout);
    pulseTimeout = setTimeout(() => {
        if (status) status.textContent = 'IDLE';
        if (sub) sub.textContent = 'Waiting for activity';
    }, 4000);
}

// ═══════════════════════════════════════════════
//  OVERVIEW
// ═══════════════════════════════════════════════

async function loadOverview() {
    await Promise.all([loadSensorsQuiet(), loadTelemetryQuiet()]);
    renderOverviewStats();
    renderOverviewTelemetry();
    renderOverviewAttention();
    renderOverviewActivity();
}

function renderOverviewStats() {
    const total = registeredSensors.length;
    const reporting = registeredSensors.filter(s => sensorState(s) === 'Reporting').length;

    const netEl = document.getElementById('stat-network');
    const netSub = document.getElementById('stat-network-sub');
    if (netEl) netEl.textContent = 'Online';
    if (netSub) netSub.textContent = 'Last sync ' + new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

    const sensorsEl = document.getElementById('stat-sensors');
    const sensorsSub = document.getElementById('stat-sensors-sub');
    if (sensorsEl) sensorsEl.textContent = `${reporting} / ${total}`;
    if (sensorsSub) sensorsSub.textContent = (total - reporting) > 0 ? `${total - reporting} need attention` : 'All sensors healthy';

    const telEl = document.getElementById('stat-telemetry');
    const telSub = document.getElementById('stat-telemetry-sub');
    if (telEl) telEl.textContent = sensorDataCache.length.toLocaleString();
    if (telSub) telSub.textContent = sessionPacketCount ? `+${sessionPacketCount} this session` : 'No new packets this session';

    const depEl = document.getElementById('stat-deployment');
    const depSub = document.getElementById('stat-deployment-sub');
    if (depEl) depEl.textContent = 'Valid';
    if (depSub) depSub.textContent = 'Last checked today';

    const telLabel = document.getElementById('telemetry-count-label');
    if (telLabel) telLabel.textContent = `${sensorDataCache.length.toLocaleString()} readings today`;
}

function renderOverviewTelemetry() {
    const body = document.getElementById('overview-telemetry-body');
    if (!body) return;
    const rows = sensorDataCache.slice(0, 3);
    if (!rows.length) {
        body.innerHTML = '<tr><td colspan="6"><p class="placeholder-text">No telemetry data yet.</p></td></tr>';
        return;
    }
    body.innerHTML = rows.map(d => `
        <tr>
            <td class="id-link">${d.sensorId}</td>
            <td class="mono">${d.value}${d.unit || ''}</td>
            <td>${telemetryTypeLabel(d)}</td>
            <td>${d.location}</td>
            <td class="mono">${d.signalStrength ?? '—'} dBm</td>
            <td>${new Date(d.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</td>
        </tr>
    `).join('');
}

function renderOverviewAttention() {
    const list = document.getElementById('attention-list');
    const countLabel = document.getElementById('attention-count');
    if (!list) return;

    const items = [];
    const staleThreshold = 15 * 60 * 1000;
    registeredSensors.forEach(s => {
        const last = sensorDataCache.find(d => d.sensorId === (s.macAddress || s.id));
        if (!last) return;
        const age = Date.now() - new Date(last.timestamp).getTime();
        if (age > staleThreshold) {
            items.push({
                title: `${s.macAddress || s.id} has gone quiet`,
                desc: `No reading for ${Math.round(age / 60000)} minutes · ${s.location}`,
                tag: 'Stale',
                cls: 'warn'
            });
        }
        if (typeof last.signalStrength === 'number' && last.signalStrength < -75) {
            items.push({
                title: 'Weak signal detected',
                desc: `${s.macAddress || s.id} at ${last.signalStrength} dBm`,
                tag: '',
                cls: 'warn'
            });
        }
    });

    if (!items.length) {
        list.innerHTML = '<p class="placeholder-text">Nothing needs attention right now.</p>';
    } else {
        list.innerHTML = items.slice(0, 4).map(it => `
            <div class="attention-item">
                <div class="row-top"><span class="title">${it.title}</span><span class="tag">${it.tag}</span></div>
                <div class="desc ${it.cls}">${it.desc}</div>
            </div>
        `).join('');
    }
    if (countLabel) countLabel.textContent = `${items.length} item${items.length === 1 ? '' : 's'}`;
}

function renderOverviewActivity() {
    const list = document.getElementById('activity-list');
    const countLabel = document.getElementById('activity-count');
    if (!list) return;

    const events = sensorDataCache.slice(0, 5).map(d => ({
        title: `Telemetry received from ${d.sensorId}`,
        desc: `${d.value}${d.unit || ''} · ${d.location}`,
        time: new Date(d.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
    }));

    if (!events.length) {
        list.innerHTML = '<p class="placeholder-text">No recent activity yet.</p>';
    } else {
        list.innerHTML = events.map(e => `
            <div class="activity-item">
                <div class="row-top">
                    <span><span class="dot">●</span><span class="title">${e.title}</span></span>
                    <span class="time">${e.time}</span>
                </div>
                <div class="desc">${e.desc}</div>
            </div>
        `).join('');
    }
    if (countLabel) countLabel.textContent = `Today · ${events.length} events`;
}

// ═══════════════════════════════════════════════
//  SENSOR REGISTRATION
// ═══════════════════════════════════════════════

async function registerSensor(e) {
    e.preventDefault();

    const mac = document.getElementById('mac-address').value.trim();
    const location = document.getElementById('sensor-location-text').value.trim();
    const category = document.getElementById('sensor-category').value;

    try {
        const res = await fetch(`${API_BASE}/sensors/register`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ macAddress: mac, location, category })
        });

        const data = await res.json();
        if (!res.ok) throw new Error(data.error || 'Registration failed');

        triggerPulse();
        showToast('Sensor registered: ' + data.sensor.macAddress, 'success');
        document.getElementById('sensor-register-result').innerHTML = `
            <div class="info-box">
                <strong>Sensor registered</strong>
                ${data.sensor.macAddress} is ready in ${data.sensor.location}.
                <div class="sub-line">
                    <a href="#" onclick="switchTab('sensors');return false;">Attach file</a>
                    <a href="#" onclick="switchTab('telemetry');return false;">Push first telemetry</a>
                </div>
            </div>`;
        loadSensors();
        document.getElementById('sensor-form').reset();
    } catch (err) {
        showToast('Error: ' + err.message, 'error');
    }
}

async function loadSensors() {
    await loadSensorsQuiet();
    renderSensorList(registeredSensors);
    updateSensorDropdowns(registeredSensors);
    renderSensorStats();
}

async function loadSensorsQuiet() {
    try {
        const res = await fetch(`${API_BASE}/sensors`);
        registeredSensors = await res.json();
    } catch {
        // Silently ignore on first load
    }
}

function sensorState(s) {
    const last = sensorDataCache.find(d => d.sensorId === (s.macAddress || s.id));
    if (!last) return 'Never reported';
    const age = Date.now() - new Date(last.timestamp).getTime();
    return age > 15 * 60 * 1000 ? 'Stale' : 'Reporting';
}

function renderSensorStats() {
    const total = registeredSensors.length;
    const reporting = registeredSensors.filter(s => sensorState(s) === 'Reporting').length;
    const stale = registeredSensors.filter(s => sensorState(s) === 'Stale').length;
    const never = registeredSensors.filter(s => sensorState(s) === 'Never reported').length;

    const set = (id, val) => { const el = document.getElementById(id); if (el) el.textContent = val; };
    set('sensors-total', total);
    set('sensors-reporting', reporting);
    set('sensors-stale', stale);
    set('sensors-never', never);
}

function renderSensorList(sensors) {
    const body = document.getElementById('sensor-list-body');
    if (!body) return;

    const search = (document.getElementById('sensor-search')?.value || '').toLowerCase();
    const catFilter = document.getElementById('sensor-filter-category')?.value || '';
    const stateFilter = document.getElementById('sensor-filter-state')?.value || '';

    let filtered = sensors.filter(s => {
        const state = sensorState(s);
        const matchesSearch = !search ||
            (s.macAddress || '').toLowerCase().includes(search) ||
            (s.id || '').toLowerCase().includes(search) ||
            (s.location || '').toLowerCase().includes(search);
        const matchesCat = !catFilter || s.category === catFilter;
        const matchesState = !stateFilter || state === stateFilter;
        return matchesSearch && matchesCat && matchesState;
    });

    if (!filtered.length) {
        body.innerHTML = '<tr><td colspan="8"><p class="placeholder-text">No sensors registered yet.</p></td></tr>';
        return;
    }

    body.innerHTML = filtered.map(s => {
        const last = sensorDataCache.find(d => d.sensorId === (s.macAddress || s.id));
        const state = sensorState(s);
        const stateCls = state === 'Reporting' ? 'reporting' : (state === 'Stale' ? 'stale' : 'never');
        return `
        <tr>
            <td><span class="id-link">${s.macAddress}</span><span class="sub-id">${s.id || ''}</span></td>
            <td><span class="cat-tag ${s.category}">${categoryLabel(s.category)}</span></td>
            <td>${s.location}</td>
            <td class="mono">${last ? last.value + (last.unit || '') : 'No reading'}</td>
            <td class="mono">${last ? (last.signalStrength ?? '—') + ' dBm' : '—'}</td>
            <td>${last ? timeAgo(last.timestamp) : 'Never'}</td>
            <td>${s.attachments?.length || 0}</td>
            <td><span class="state-pill ${stateCls}">${state}</span></td>
        </tr>`;
    }).join('');
}

function categoryLabel(cat) {
    if (cat === 'PowerConsumption') return 'Power consumption';
    return cat || '—';
}

function timeAgo(timestamp) {
    const diffMs = Date.now() - new Date(timestamp).getTime();
    const sec = Math.round(diffMs / 1000);
    if (sec < 60) return `${sec} sec ago`;
    const min = Math.round(sec / 60);
    if (min < 60) return `${min} min ago`;
    const hr = Math.round(min / 60);
    return `${hr} hr ago`;
}

function updateSensorDropdowns(sensors) {
    const uploadSel = document.getElementById('upload-sensor-id');
    if (uploadSel) {
        uploadSel.innerHTML = '<option value="">— Select a sensor —</option>' +
            sensors.map(s => `<option value="${s.id}">${s.macAddress} (${categoryLabel(s.category)})</option>`).join('');
    }
    refreshOperatorDropdowns();
}

function exportSensorList() {
    if (!registeredSensors.length) return showToast('No sensors to export', 'error');
    const rows = registeredSensors.map(s => `${s.macAddress},${s.category},${s.location}`).join('\n');
    const blob = new Blob([`MAC,Category,Location\n${rows}`], { type: 'text/csv' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'sensors.csv';
    a.click();
    URL.revokeObjectURL(url);
}

// ═══════════════════════════════════════════════
//  FILE UPLOAD
// ═══════════════════════════════════════════════

async function uploadFile() {
    const sensorId = document.getElementById('upload-sensor-id').value;
    const fileInput = document.getElementById('sensor-file');

    if (!sensorId) return showToast('Select a sensor first', 'error');
    if (!fileInput.files.length) return showToast('Select a file to upload', 'error');

    const file = fileInput.files[0];
    const formData = new FormData();
    formData.append('file', file);

    try {
        const res = await fetch(`${API_BASE}/sensors/${sensorId}/attachments`, {
            method: 'POST',
            body: formData
        });

        const data = await res.json();
        if (!res.ok) throw new Error(data.error || 'Upload failed');

        triggerPulse();
        showToast(`File attached: ${data.attachment.fileName}`, 'success');
        document.getElementById('upload-result').innerHTML =
            `<span class="success">${data.attachment.fileName} (${(data.attachment.fileSize / 1024).toFixed(1)} KB)</span>`;
        loadSensors();
        fileInput.value = '';
    } catch (err) {
        showToast('Upload error: ' + err.message, 'error');
    }
}

// ═══════════════════════════════════════════════
//  TELEMETRY
// ═══════════════════════════════════════════════

function updateValueHint() {
    const type = document.getElementById('tel-type').value;
    const hint = document.getElementById('tel-value-hint');
    if (!hint) return;
    if (type === 'environmental') hint.textContent = 'Environmental readings are recorded in °C';
    else if (type === 'power') hint.textContent = 'Power readings are recorded in W';
    else hint.textContent = 'Actuator readings accept true / false or 1 / 0';
}

async function pushTelemetry(e) {
    e.preventDefault();

    const sensorId = document.getElementById('tel-sensor-id').value.trim();
    const type = document.getElementById('tel-type').value;
    const location = document.getElementById('tel-location').value.trim() || 'Unknown';
    const valueStr = document.getElementById('tel-value').value.trim();
    const signal = parseInt(document.getElementById('tel-signal').value) || -60;

    let payload;
    switch (type) {
        case 'environmental':
            payload = { sensorId, location, signalStrength: signal, value: parseFloat(valueStr) };
            break;
        case 'power':
            payload = { sensorId, location, signalStrength: signal, value: parseInt(valueStr) };
            break;
        case 'actuator':
            payload = { sensorId, location, signalStrength: signal, value: valueStr.toLowerCase() === 'true' || valueStr === '1' };
            break;
    }

    try {
        const res = await fetch(`${API_BASE}/telemetry/push/${type}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });

        if (!res.ok) {
            const err = await res.json();
            throw new Error(err.error || 'Push failed');
        }

        const data = await res.json();
        triggerPulse();
        showToast(`Telemetry pushed: ${data.packet}`, 'success');
        document.getElementById('telemetry-push-result').innerHTML = `
            <div class="info-box">
                <strong>Telemetry received</strong>
                Packet ${data.packet} from ${sensorId}<br>
                ${valueStr} · ${signal} dBm · ${new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}
            </div>`;
        loadTelemetryFeed();
        refreshOperatorDropdowns();
        document.getElementById('telemetry-form').reset();
        document.getElementById('tel-signal').value = -60;
    } catch (err) {
        showToast('Error: ' + err.message, 'error');
    }
}

async function loadTelemetryFeed() {
    try {
        const res = await fetch(`${API_BASE}/telemetry/latest`);
        sensorDataCache = await res.json();
        renderTelemetryFeed(sensorDataCache);
        refreshOperatorDropdowns();
        renderOverviewStats();
    } catch {
        // Ignore
    }
}

async function loadTelemetryQuiet() {
    try {
        const res = await fetch(`${API_BASE}/telemetry/latest`);
        sensorDataCache = await res.json();
    } catch {
        // Ignore
    }
}

function telemetryTypeLabel(d) {
    if (d.unit === '°C' || d.unit === '°F') return 'Environmental';
    if (d.unit === 'W') return 'Power';
    if (typeof d.value === 'boolean' || d.value === 'true' || d.value === 'false' || d.value === 'ON' || d.value === 'OFF') return 'Actuator';
    return d.type || '—';
}

function renderTelemetryFeed(data) {
    const body = document.getElementById('telemetry-feed-body');
    if (!body) return;

    const search = (document.getElementById('telemetry-search')?.value || '').toLowerCase();
    const typeFilter = document.getElementById('telemetry-filter-type')?.value || '';

    let filtered = data.filter(d => {
        const matchesSearch = !search ||
            d.sensorId.toLowerCase().includes(search) ||
            (d.location || '').toLowerCase().includes(search);
        const matchesType = !typeFilter || telemetryTypeLabel(d).toLowerCase() === typeFilter;
        return matchesSearch && matchesType;
    });

    if (!filtered.length) {
        body.innerHTML = '<tr><td colspan="6"><p class="placeholder-text">No telemetry data yet.</p></td></tr>';
    } else {
        body.innerHTML = filtered.map(d => `
            <tr>
                <td class="id-link">${d.sensorId}</td>
                <td class="mono">${d.value}${d.unit || ''}</td>
                <td>${telemetryTypeLabel(d)}</td>
                <td>${d.location}</td>
                <td class="mono">${d.signalStrength ?? '—'} dBm</td>
                <td>${new Date(d.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</td>
            </tr>
        `).join('');
    }

    const countLabel = document.getElementById('telemetry-count-label');
    if (countLabel) countLabel.textContent = `${data.length.toLocaleString()} readings today`;

    const latest = data[0];
    const latestChip = document.getElementById('chip-latest-packet');
    if (latestChip) latestChip.textContent = latest ? latest.sensorId : '—';

    const activeChip = document.getElementById('chip-active-sensors');
    if (activeChip) {
        const activeCount = new Set(data.map(d => d.sensorId)).size;
        activeChip.textContent = `${Math.min(activeCount, 6)} / ${registeredSensors.length || activeCount}`;
    }
}

async function simulateTelemetry() {
    try {
        const res = await fetch(`${API_BASE}/telemetry/simulate`, { method: 'POST' });
        const data = await res.json();
        triggerPulse();
        showToast(`Simulated activity — total data points: ${data.dataCount}`, 'info');
        loadTelemetryFeed();
        loadOverview();
    } catch (err) {
        showToast('Simulation error: ' + err.message, 'error');
    }
}

// ═══════════════════════════════════════════════
//  DEPLOYMENT TREE & RECURSIVE VALIDATION
// ═══════════════════════════════════════════════

async function loadDeploymentTree() {
    const container = document.getElementById('deployment-tree');
    try {
        const res = await fetch(`${API_BASE}/deployment/tree`);
        const data = await res.json();

        if (data.nodes) {
            deploymentNodesById = {};
            data.nodes.forEach(n => deploymentNodesById[n.nodeId] = n);
            renderTreeNodes(data.nodes);
        } else if (data.tree) {
            container.textContent = data.tree;
        }

        const countLabel = document.getElementById('tree-count');
        if (countLabel && data.nodes) countLabel.textContent = `${data.nodes.length} / ${data.capacity || data.nodes.length}`;
    } catch (err) {
        if (container) container.innerHTML = '<p class="placeholder-text">Error loading tree.</p>';
    }
}

function renderTreeNodes(nodes) {
    const container = document.getElementById('deployment-tree');
    if (!container) return;
    container.innerHTML = nodes.map(n => `
        <div class="tree-node l${n.depth || 1} ${n.nodeId === selectedNodeId ? 'selected' : ''}" onclick="selectDeploymentNode('${n.nodeId}')">
            <span class="node-load">${n.currentLoad ?? ''}${n.currentLoad != null ? ' / ' + n.maxCapacity : ''}</span>
            <span class="node-state ${n.needsReview ? 'review' : ''}">${n.needsReview ? 'Review' : (n.isConfigured ? 'Configured' : '')}</span>
            <div class="node-name">${n.name}</div>
        </div>
    `).join('');
}

function selectDeploymentNode(nodeId) {
    selectedNodeId = nodeId;
    const node = deploymentNodesById[nodeId];
    renderTreeNodes(Object.values(deploymentNodesById));
    if (!node) return;

    document.getElementById('selected-node-name').textContent = node.name;
    document.getElementById('selected-node-meta').textContent = `${node.nodeType || 'Node'} · ${node.parentName || ''}`.trim();
    document.getElementById('selected-node-details').innerHTML = `
        <div class="detail-row"><span class="k">Node ID</span><span class="v">${node.nodeId}</span></div>
        <div class="detail-row"><span class="k">Current load</span><span class="v">${node.currentLoad ?? '—'} / ${node.maxCapacity ?? '—'}</span></div>
        <div class="detail-row"><span class="k">Configuration</span><span class="v">${node.isConfigured ? 'Configured' : 'Unconfigured'}</span></div>
        <div class="detail-row"><span class="k">Last heartbeat</span><span class="v">${node.lastHeartbeat ? new Date(node.lastHeartbeat).toLocaleTimeString() : '—'}</span></div>
    `;
    document.getElementById('selected-node-links').innerHTML = `
        <a href="#" onclick="return false;">Reveal in tree</a>
        <a href="#" onclick="return false;">View raw node</a>
    `;
}

async function runValidation() {
    try {
        const res = await fetch(`${API_BASE}/deployment/validate`, { method: 'POST' });
        const result = await res.json();
        triggerPulse();
        renderValidationResult(result);
    } catch (err) {
        showToast('Validation error: ' + err.message, 'error');
    }
}

function renderValidationResult(r) {
    const container = document.getElementById('validation-result');
    let html = '';

    const bannerCls = r.isValid ? 'valid' : 'invalid';
    const bannerTitle = r.isValid
        ? (r.warnings?.length ? 'Valid with warnings' : 'Valid')
        : 'Invalid';
    html += `
        <div class="validation-banner ${bannerCls}">
            <strong>${bannerTitle}</strong>
            <div class="sub">${r.nodesValidated} nodes validated · Depth ${r.depth}</div>
            <div class="sub">${r.errors?.length || 0} errors · ${r.warnings?.length || 0} warnings</div>
        </div>`;

    if (r.errors?.length) {
        html += r.errors.map(e => `<div class="validation-item err"><strong>${e}</strong></div>`).join('');
    }
    if (r.warnings?.length) {
        html += r.warnings.map(w => `<div class="validation-item warn"><strong>${w}</strong></div>`).join('');
    }
    if (r.isValid && !r.errors?.length && !r.warnings?.length) {
        html += '<div class="validation-item"><strong>All nodes validated successfully</strong></div>';
    }

    container.innerHTML = html;
}

async function findNode() {
    const name = document.getElementById('find-node-name').value.trim();
    if (!name) return;

    try {
        const res = await fetch(`${API_BASE}/deployment/find/${encodeURIComponent(name)}`);
        const result = document.getElementById('find-node-result');

        if (!res.ok) {
            result.innerHTML = '<span class="error">Node not found.</span>';
            return;
        }

        const node = await res.json();
        triggerPulse();
        selectDeploymentNode(node.nodeId);
        result.innerHTML = `<span class="success">Found ${node.nodeType}: ${node.name}</span>`;
    } catch (err) {
        showToast('Search error: ' + err.message, 'error');
    }
}

// ═══════════════════════════════════════════════
//  OPERATOR OVERLOADING DEMO
// ═══════════════════════════════════════════════

function refreshOperatorDropdowns() {
    const op1 = document.getElementById('op-sensor-1');
    const op2 = document.getElementById('op-sensor-2');
    if (!op1 || !op2) return;

    const opts = sensorDataCache.map(d =>
        `<option value="${d.sensorId}">${d.sensorId}: ${d.value}${d.unit || ''}</option>`
    ).join('');

    op1.innerHTML = '<option value="">— Sensor 1 —</option>' + opts;
    op2.innerHTML = '<option value="">— Sensor 2 —</option>' + opts;
}

function selectOperator(op) {
    selectedOperator = op;
    document.getElementById('op-choice-add').classList.toggle('selected', op === 'add');
    document.getElementById('op-choice-sub').classList.toggle('selected', op === 'sub');
    document.getElementById('op-choice-cmp').classList.toggle('selected', op === 'cmp');
}

async function runOperation() {
    if (selectedOperator === 'add') return aggregateSensors();
    if (selectedOperator === 'sub') return deltaSensors();
    return compareSensors();
}

async function aggregateSensors() {
    const id1 = document.getElementById('op-sensor-1').value;
    const id2 = document.getElementById('op-sensor-2').value;
    if (!id1 || !id2) return showToast('Select both sensors', 'error');

    try {
        const res = await fetch(`${API_BASE}/telemetry/aggregate`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ sensorId1: id1, sensorId2: id2 })
        });
        const data = await res.json();
        triggerPulse();
        renderOperatorResult('Aggregation', id1, id2, data, res.ok);
    } catch (err) {
        renderOperatorResult('Aggregation', id1, id2, { error: err.message }, false);
    }
}

async function deltaSensors() {
    const id1 = document.getElementById('op-sensor-1').value;
    const id2 = document.getElementById('op-sensor-2').value;
    if (!id1 || !id2) return showToast('Select both sensors', 'error');

    try {
        const res = await fetch(`${API_BASE}/telemetry/delta`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ sensorId1: id1, sensorId2: id2 })
        });
        const data = await res.json();
        triggerPulse();
        renderOperatorResult('Delta', id1, id2, data, res.ok);
    } catch (err) {
        renderOperatorResult('Delta', id1, id2, { error: err.message }, false);
    }
}

async function compareSensors() {
    const id1 = document.getElementById('op-sensor-1').value;
    const id2 = document.getElementById('op-sensor-2').value;
    if (!id1 || !id2) return showToast('Select both sensors', 'error');

    try {
        const res = await fetch(`${API_BASE}/telemetry/compare`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ sensorId1: id1, sensorId2: id2 })
        });
        const data = await res.json();
        triggerPulse();
        renderOperatorResult('Compare', id1, id2, data, res.ok);
    } catch (err) {
        renderOperatorResult('Compare', id1, id2, { error: err.message }, false);
    }
}

function renderOperatorResult(title, id1, id2, data, success) {
    const container = document.getElementById('operator-result');
    const cls = success ? '' : 'error';
    const content = data.result || data.error || data.message || JSON.stringify(data);
    const now = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });

    container.innerHTML = `
        <div class="op-result-box ${cls}">
            <div class="op-title">${success ? title + ' complete' : title + ' failed'}</div>
            <div class="op-detail">${id1} ${opSymbol(title)} ${id2}</div>
            <div class="op-result">${content}</div>
            <div class="op-time">Completed today at ${now}</div>
        </div>`;

    if (success) {
        recentOperations.unshift({ title, id1, id2, result: content, time: now });
        recentOperations = recentOperations.slice(0, 5);
        renderRecentOps();
    }
}

function opSymbol(title) {
    if (title === 'Aggregation') return '+';
    if (title === 'Delta') return '−';
    return 'vs';
}

function renderRecentOps() {
    const list = document.getElementById('recent-ops-list');
    if (!list) return;
    if (!recentOperations.length) {
        list.innerHTML = '<p class="placeholder-text">No operations run yet.</p>';
        return;
    }
    list.innerHTML = recentOperations.map(op => `
        <div class="ops-item">
            <div class="row-top"><span class="title">${op.title}</span><span class="tag">${op.time}</span></div>
            <div class="desc">${op.id1} ${opSymbol(op.title)} ${op.id2} → ${op.result}</div>
        </div>
    `).join('');
}

// ═══════════════════════════════════════════════
//  BATCH INFO & HISTORY (Advanced Arrays)
// ═══════════════════════════════════════════════

async function loadHistory() {
    try {
        const res = await fetch(`${API_BASE}/telemetry/history`);
        const data = await res.json();
        triggerPulse();

        const setChip = (id, val) => { const el = document.getElementById(id); if (el) el.textContent = val; };
        setChip('chip-batches', data.batchCount);
        setChip('chip-total-readings', data.totalReadings);
        setChip('history-chip-batches', data.batchCount);
        setChip('history-chip-total', data.totalReadings);

        const body = document.getElementById('history-body');
        if (!body) return;
        if (!data.history.length) {
            body.innerHTML = '<tr><td colspan="2"><p class="placeholder-text">No history yet.</p></td></tr>';
            return;
        }
        body.innerHTML = data.history.map((v, i) => `<tr><td>${i + 1}</td><td class="mono">${v.toFixed(4)}</td></tr>`).join('');
    } catch (err) {
        showToast('Error loading history: ' + err.message, 'error');
    }
}

// ═══════════════════════════════════════════════
//  TOAST NOTIFICATIONS
// ═══════════════════════════════════════════════

function showToast(message, type = 'info') {
    const container = document.getElementById('toast-container');
    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.textContent = message;
    container.appendChild(toast);

    setTimeout(() => toast.remove(), 4000);
}