// Smart-X HTTPS/HTTP cache diagnostics using Playwright
// Tests both https://localhost:59184 and http://localhost:59185 origins
// Captures console errors, network headers, MODULES state, and click behavior
// Usage: node browser-diagnostics.js [http_url] [https_url]

const { chromium } = require('@playwright/test');

// Default ports
const HTTP_URL = process.argv[2] || 'http://localhost:59185';
const HTTPS_URL = process.argv[3] || 'https://localhost:59184';

async function captureOriginDiagnostics(browser, origin, label) {
    console.log(`\n========== ${label} (${origin}) ==========`);
    const context = await browser.newContext({
        ignoreHTTPSErrors: true, // Allow self-signed certs
    });
    const page = await context.newPage();

    // Capture console and network
    const consoleLogs = [];
    const networkErrors = [];

    page.on('console', msg => {
        consoleLogs.push(`${msg.type()}: ${msg.text()}`);
    });

    page.on('response', response => {
        if (response.status() >= 400) {
            networkErrors.push(`${response.status()} ${response.url()}`);
        }
    });

    // Navigate to origin
    console.log(`Loading ${origin}...`);
    await page.goto(origin, { waitUntil: 'load', timeout: 10000 }).catch(e => console.log(`Load error: ${e.message}`));

    // Wait for scripts to load
    await page.waitForTimeout(1000);

    // Capture diagnostics
    const diagnostics = await page.evaluate(() => {
        return {
            activatePillar: typeof activatePillar,
            MODULES: typeof MODULES,
            modulesKeys: MODULES ? Object.keys(MODULES) : [],
            meshModule: MODULES ? typeof MODULES.mesh : 'MODULES undefined',
            windowMESH: typeof window.MESH,
            meshStatus: window.MESH ? Object.keys(window.MESH) : 'window.MESH undefined',
            documentReady: document.readyState,
            appShellHidden: document.querySelector('#app-shell')?.classList.contains('hidden'),
            locationHash: location.hash,
        };
    });

    // Capture response headers for key resources
    const headerCapture = await page.evaluate(async () => {
        const resources = ['/', '/index.html', '/js/dashboard.js', '/js/mesh.js'];
        const headers = {};
        for (const res of resources) {
            try {
                const resp = await fetch(res);
                headers[res] = {
                    status: resp.status,
                    cacheControl: resp.headers.get('cache-control'),
                    etag: resp.headers.get('etag'),
                    contentType: resp.headers.get('content-type'),
                };
            } catch (e) {
                headers[res] = { error: e.message };
            }
        }
        return headers;
    });

    // Attempt to click third pillar
    console.log('\nAttempting to click third pillar...');
    const pillarCards = await page.$$('.pillar-card');
    if (pillarCards.length >= 3) {
        await pillarCards[2].click();
        await page.waitForTimeout(500);
    }

    // Check state after click
    const stateAfterClick = await page.evaluate(() => {
        return {
            appShellHidden: document.querySelector('#app-shell')?.classList.contains('hidden'),
            locationHash: location.hash,
            topologyTabActive: document.querySelector('#tab-mesh-topology')?.classList.contains('active'),
            meshMapNodes: document.querySelectorAll('#mesh-map .mesh-node').length,
            consoleErrors: window.__errors || [],
        };
    });

    // Log diagnostics
    console.log('\n--- Type Checks ---');
    console.log(`activatePillar: ${diagnostics.activatePillar}`);
    console.log(`MODULES: ${diagnostics.MODULES}`);
    console.log(`MODULES keys: [${diagnostics.modulesKeys.join(', ')}]`);
    console.log(`MODULES.mesh: ${diagnostics.meshModule}`);
    console.log(`window.MESH: ${diagnostics.windowMESH}`);
    console.log(`window.MESH keys: ${typeof diagnostics.meshStatus === 'string' ? diagnostics.meshStatus : diagnostics.meshStatus.join(', ')}`);

    console.log('\n--- Response Headers ---');
    Object.entries(headerCapture).forEach(([resource, data]) => {
        if (data.error) {
            console.log(`${resource}: ERROR - ${data.error}`);
        } else {
            console.log(`${resource}: ${data.status} | Cache-Control: ${data.cacheControl || '(none)'} | ETag: ${data.etag ? 'present' : '(none)'}`);
        }
    });

    console.log('\n--- Before Click ---');
    console.log(`app-shell hidden: ${diagnostics.appShellHidden}`);
    console.log(`location.hash: ${diagnostics.locationHash}`);

    console.log('\n--- After Pillar Click ---');
    console.log(`app-shell hidden: ${stateAfterClick.appShellHidden}`);
    console.log(`location.hash: ${stateAfterClick.locationHash}`);
    console.log(`#tab-mesh-topology active: ${stateAfterClick.topologyTabActive}`);
    console.log(`#mesh-map nodes: ${stateAfterClick.meshMapNodes}`);

    if (consoleLogs.length > 0) {
        console.log('\n--- Console Messages ---');
        consoleLogs.forEach(log => console.log(`  ${log}`));
    }

    if (networkErrors.length > 0) {
        console.log('\n--- Network Errors ---');
        networkErrors.forEach(err => console.log(`  ${err}`));
    }

    await context.close();

    return {
        origin,
        diagnostics,
        headerCapture,
        stateAfterClick,
        consoleLogs,
        networkErrors,
    };
}

async function main() {
    console.log('Smart-X HTTPS/HTTP Cache Diagnostics');
    console.log('=====================================\n');
    console.log(`HTTP origin:  ${HTTP_URL}`);
    console.log(`HTTPS origin: ${HTTPS_URL}\n`);

    const browser = await chromium.launch();

    try {
        // Test both origins
        const httpResults = await captureOriginDiagnostics(browser, HTTP_URL, 'HTTP Test');
        const httpsResults = await captureOriginDiagnostics(browser, HTTPS_URL, 'HTTPS Test');

        // Summary comparison
        console.log('\n\n========== COMPARISON SUMMARY ==========');
        console.log(`\nHTTP:  MODULES.mesh = ${httpResults.diagnostics.meshModule}, app-shell hidden after click = ${httpResults.stateAfterClick.appShellHidden}, nodes rendered = ${httpResults.stateAfterClick.meshMapNodes}`);
        console.log(`HTTPS: MODULES.mesh = ${httpsResults.diagnostics.meshModule}, app-shell hidden after click = ${httpsResults.stateAfterClick.appShellHidden}, nodes rendered = ${httpsResults.stateAfterClick.meshMapNodes}`);

        // Check for differences
        const httpCacheControl = httpResults.headerCapture['/']?.cacheControl;
        const httpsCacheControl = httpsResults.headerCapture['/']?.cacheControl;

        console.log(`\nCache-Control on /:`);
        console.log(`  HTTP:  ${httpCacheControl || '(none)'}`);
        console.log(`  HTTPS: ${httpsCacheControl || '(none)'}`);

        if (httpsCacheControl === undefined || httpsCacheControl === null || !httpsCacheControl.includes('no-cache')) {
            console.log('\n*** ISSUE FOUND: HTTPS origin has no cache-control or missing "no-cache" header ***');
        }

        if (httpsResults.diagnostics.meshModule === 'undefined' && httpResults.diagnostics.meshModule !== 'undefined') {
            console.log('\n*** ISSUE FOUND: MODULES.mesh undefined on HTTPS but defined on HTTP ***');
            console.log('*** This indicates stale dashboard.js cached on HTTPS origin ***');
        }

        if (httpsResults.stateAfterClick.meshMapNodes === 0 && httpResults.stateAfterClick.meshMapNodes > 0) {
            console.log('\n*** ISSUE FOUND: Mesh map does not render on HTTPS after pillar click ***');
        }

    } finally {
        await browser.close();
    }
}

main().catch(console.error);
