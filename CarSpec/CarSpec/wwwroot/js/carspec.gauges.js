async function ensureLib() {
    if (window.RadialGauge) return;
    await new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = "https://cdn.jsdelivr.net/npm/canvas-gauges@2.1.7/gauge.min.js";
        s.onload = resolve;
        s.onerror = reject;
        document.head.appendChild(s);
    });
}

function sizeOf(el) {
    const w = el.clientWidth || 0, h = el.clientHeight || 0;
    return Math.max(120, Math.min(w || h || 0, h || w || 0) || 280);
}

let gauge, ro;

export async function initRpmGauge(canvasId, value, maxRpm, options = {}) {
    await ensureLib();
    if (gauge) return;

    const start = () => {
        const el = document.getElementById(canvasId);
        if (!el) return;

        let s = sizeOf(el); if (s === 0) s = 280;
        el.width = s; el.height = s;

        gauge = new RadialGauge({
            renderTo: el,
            width: s, height: s,

            minValue: 0,
            maxValue: maxRpm ?? 8000,

            startAngle: options.startAngle ?? 46,
            ticksAngle: options.ticksAngle ?? 270,

            majorTicks: options.majorTicks ?? ['0', '1', '2', '3', '4', '5', '6', '7', '8'],
            minorTicks: options.minorTicks ?? 5,
            highlights: options.highlights ?? [{ from: 7000, to: (maxRpm ?? 8000), color: 'rgba(255,0,0,.28)' }],

            units: options.units ?? 'rpm',
            valueBox: true,
            animatedValue: true,
            valueInt: 1,
            valueDec: 0,

            colorPlate: '#151820',
            colorMajorTicks: 'rgba(255,255,255,0.45)',
            colorMinorTicks: 'rgba(255,255,255,0.30)',
            colorNumbers: '#A6B0C3',
            colorUnits: '#ccc',
            borders: true,
            borderOuterWidth: 12,
            colorBorderOuter: 'rgba(255,255,255,0.06)',
            colorBarProgress: 'rgba(0,0,0,0)',
            colorBarStroke: 'rgba(0,0,0,0)',
            colorNeedle: '#ff7a1a',
            colorNeedleEnd: '#ff3b0a',
            needleCircleSize: 10,
            needleCircleOuter: true,
            needleCircleInner: false,
            animationRule: 'linear',
            animationDuration: 120
        }).draw();

        gauge.value = value ?? 0;

        // === ADD OVERLAY ===
        const host = el.parentElement || el;
        let sub = host.querySelector('.gauge-subunits');
        if (!sub) {
            sub = document.createElement('div');
            sub.className = 'gauge-subunits';
            host.appendChild(sub);
        }
        sub.textContent = options.subUnits ?? 'x1000';

        if ('ResizeObserver' in window) {
            ro = new ResizeObserver(entries => {
                const rect = entries[0].contentRect;
                const s2 = Math.max(120, Math.min(rect.width, rect.height));
                if (s2 && s2 !== el.width) {
                    el.width = s2; el.height = s2;
                    gauge.update({ width: s2, height: s2 });
                    gauge.draw();
                }
            });
            ro.observe(el);
        }
    };

    requestAnimationFrame(() => requestAnimationFrame(start));
}

export function setRpm(value) {
    if (gauge) gauge.value = value ?? 0;
}

export function disposeRpmGauge() {
    try { ro?.disconnect?.(); } catch { }
    gauge = null;
}