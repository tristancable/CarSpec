// /wwwroot/js/carspec.gauges.js
async function ensureLib() {
    if (!window.RadialGauge || !window.LinearGauge) {
        await new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = "https://cdn.jsdelivr.net/npm/canvas-gauges@2.1.7/gauge.min.js";
            s.onload = resolve;
            s.onerror = reject;
            document.head.appendChild(s);
        });
    }

    const family = '"Audiowide","Oxanium",system-ui,sans-serif';
    try {
        if (document.fonts && document.fonts.load) {
            await Promise.all([
                document.fonts.load(`14px ${family}`),
                document.fonts.load(`24px ${family}`)
            ]);
            if (document.fonts.ready) await document.fonts.ready;
        }
    } catch { /* ignore */ }
}

function cssVar(name, fallback) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

function getFontScale() {
    const raw = getComputedStyle(document.documentElement).getPropertyValue('--gauge-font-scale').trim();
    const n = Number(raw);
    return Number.isFinite(n) && n > 0 ? n : 1;
}

function applyGaugeFonts(gauge, sizePx, perGaugeScale = 1) {
    const base = getFontScale() * (perGaugeScale || 1);
    const fam = '"Audiowide","Oxanium",system-ui,sans-serif';

    const fNums = Math.max(18, sizePx * 0.16 * base);
    const fValue = Math.max(30, sizePx * 0.26 * base);
    const fUnits = Math.max(16, sizePx * 0.14 * base);
    const fTitle = Math.max(14, sizePx * 0.12 * base);

    gauge.update({
        fontNumbersSize: Math.round(fNums),
        fontNumbersFamily: fam,
        fontNumbersStyle: "normal",
        fontNumbersWeight: "600",

        fontValueSize: Math.round(fValue),
        fontValueFamily: fam,
        fontValueStyle: "normal",
        fontValueWeight: "700",

        fontUnitsSize: Math.round(fUnits),
        fontUnitsFamily: fam,
        fontUnitsStyle: "normal",
        fontUnitsWeight: "600",

        fontTitleSize: Math.round(fTitle),
        fontTitleFamily: fam,
        fontTitleStyle: "normal",
        fontTitleWeight: "600"
    });
}

const _gauges = new Map();        // id -> gauge
const _resize = new Map();        // id -> ResizeObserver
const _valueOverlays = new Map(); // id -> { el, suffix }
const _els = new Map();           // id -> canvas element

function _ensureOverlay(host, text) {
    let sub = host.querySelector('.gauge-subunits');
    if (!sub) {
        sub = document.createElement('div');
        sub.className = 'gauge-subunits';
        host.appendChild(sub);
    }
    sub.textContent = text ?? '';
}

function _observeResize(canvas, gauge) {
    if (!('ResizeObserver' in window)) return;

    let lastW = 0, lastH = 0, lastAt = 0, raf = 0;
    const ro = new ResizeObserver(entries => {
        const rect = entries[0].contentRect;
        const w = Math.round(rect.width);
        const h = Math.round(rect.height);
        if (Math.abs(w - lastW) < 1 && Math.abs(h - lastH) < 1) return;

        const now = performance.now();
        if (now - lastAt < 60) return;
        lastAt = now; lastW = w; lastH = h;

        cancelAnimationFrame(raf);
        raf = requestAnimationFrame(() => {
            const s = Math.max(120, Math.min(w, h));
            if (!s) return;
            if (gauge.options.width === s && gauge.options.height === s) return;

            const current = Number(gauge.value) || 0;

            const prevDur = gauge.options.animationDuration;
            const prevAnimVal = gauge.options.animatedValue;
            gauge.options.animationDuration = 0;
            gauge.options.animatedValue = false;

            canvas.width = s;
            canvas.height = s;
            gauge.update({ width: s, height: s, value: current });
            applyGaugeFonts(gauge, s, Number(gauge.options.__fontScale) || 1);
            gauge.draw();
            gauge.value = current;

            gauge.options.animationDuration = prevDur;
            gauge.options.animatedValue = prevAnimVal;
        });
    });

    ro.observe(canvas);
    _resize.set(canvas.id, ro);
}

export async function initGauge(canvasId, config = {}) {
    await ensureLib();
    if (_gauges.has(canvasId)) return;

    const el = document.getElementById(canvasId);
    if (!el) return;
    _els.set(canvasId, el);

    const type = (config.type || 'radial').toLowerCase();
    const host = el.parentElement || el;
    host.classList.add(type === 'radial' ? 'gauge--radial' : 'gauge--linear');
    if (type === 'linear') host.classList.add('gauge-host--linear');

    // square based on host
    let s = Math.floor(Math.min(host.clientWidth, host.clientHeight));
    if (!s || s < 100) s = 280;
    el.width = s;
    el.height = s;

    const units = config.units ?? '';
    const value = config.value ?? 0;
    const max = config.maxValue ?? 100;
    const min = config.minValue ?? 0;
    const startAngle = config.startAngle ?? 46;
    const ticksAngle = config.ticksAngle ?? 270;

    const common = {
        renderTo: el,
        width: s, height: s,
        minValue: min, maxValue: max,
        units, value,
        // disable built-in animation (we animate ourselves)
        animation: false,
        animateOnInit: false,
        animateOnInitDuration: 0,
        animateOnInitDelay: 0,
        animatedValue: false,
        animationRule: 'linear',
        animationDuration: 0,
        valueInt: 1,
        valueDec: 0,
        colorPlate: cssVar('--cs-iron', '#151820'),
        colorMajorTicks: 'rgba(255,255,255,0.45)',
        colorMinorTicks: 'rgba(255,255,255,0.30)',
        colorNumbers: cssVar('--muted', '#A6B0C3'),
        colorUnits: cssVar('--cs-mist', '#ccc'),
        borders: true,
        borderOuterWidth: 12,
        colorBorderOuter: 'rgba(255,255,255,0.06)',
        colorBarProgress: 'rgba(0,0,0,0)',
        colorBarStroke: 'rgba(0,0,0,0)',
        colorNeedle: cssVar('--cs-sunrise', '#ff7a1a'),
        colorNeedleEnd: cssVar('--cs-ember', '#ff3b0a'),
        needleCircleSize: 10,
        needleCircleOuter: true,
        needleCircleInner: false
    };

    const perGaugeScale = Number(config.fontScale) || 1;
    let gauge;

    if (type === 'radial') {
        gauge = new RadialGauge({
            ...common,
            valueBox: true,
            startAngle,
            ticksAngle,
            majorTicks: config.majorTicks ?? [],
            minorTicks: config.minorTicks ?? 5,
            highlights: config.highlights ?? []
        });
        gauge.options.__fontScale = perGaugeScale;
        applyGaugeFonts(gauge, s, perGaugeScale);
        gauge.draw();

        if (config.subUnits) _ensureOverlay(host, config.subUnits);
    } else {
        // Allow choosing which side the needle rides on (default = "right")
        const needleSide = config?.linearOverrides?.needleSide ?? "right";

        gauge = new LinearGauge({
            ...common,
            valueBox: false, // use our overlay
            barBeginCircle: false,
            barWidth: Math.max(10, Math.round(s * 0.045)),
            tickSide: "right",
            numberSide: "right",
            needleSide,                               // <-- left or right
            colorBar: "rgba(255,255,255,0.15)",
            ...config.linearOverrides
        });
        gauge.options.__fontScale = perGaugeScale;
        applyGaugeFonts(gauge, s, perGaugeScale);
        gauge.draw();

        // centered value overlay
        const val = document.createElement('div');
        val.className = 'gauge-value';
        val.textContent = `${Math.round(value)}${config.valueSuffix ?? ''}`;
        host.appendChild(val);
        _valueOverlays.set(canvasId, { el: val, suffix: config.valueSuffix ?? '' });
    }

    if (!config.noResize) _observeResize(el, gauge);
    _gauges.set(canvasId, gauge);
}

export function setValue(canvasId, value) {
    const g = _gauges.get(canvasId);
    if (g) g.value = value ?? 0;
    const vo = _valueOverlays.get(canvasId);
    if (vo) vo.el.textContent = `${Math.round(value ?? 0)}${vo.suffix}`;
}

export function setMany(valuesById) {
    if (!valuesById) return;
    for (const [id, v] of Object.entries(valuesById)) {
        const g = _gauges.get(id);
        if (g) g.value = v ?? 0;
        const vo = _valueOverlays.get(id);
        if (vo) vo.el.textContent = `${Math.round(v ?? 0)}${vo.suffix}`;
    }
}

// ---- smooth animation helpers ----
const _anims = new Map();
const _lasts = new Map();
function easeOutCubic(t) { return 1 - Math.pow(1 - t, 3); }

function _animateTo(id, target, opts = {}) {
    const g = _gauges.get(id);
    if (!g) return;

    const now = performance.now();
    const last = _lasts.get(id) ?? { v: Number(g.value) || 0, t: now };
    const startVal = Number(g.value) || 0;
    const endVal = Number(target) || 0;
    const delta = Math.abs(endVal - startVal);
    const dtMs = Math.max(1, now - last.t);

    const min = Number(g.options.minValue ?? 0);
    const max = Number(g.options.maxValue ?? 100);
    const range = Math.max(1, max - min);
    const nd = delta / range;

    const idOpts = (opts && opts[id]) || {};
    const durSmall = idOpts.durSmall ?? 180;
    const durMed = idOpts.durMed ?? 360;
    const durBig = idOpts.durBig ?? 700;
    const durMax = idOpts.durMax ?? 900;

    let ms = nd < 0.01 ? durSmall : nd < 0.05 ? durMed : durBig;
    if (dtMs > 1500) ms = Math.max(140, ms * 0.6);
    else if (dtMs < 120) ms = Math.min(durMax, ms + 120);
    ms = Math.min(ms, durMax);

    const stop = _anims.get(id);
    if (typeof stop === 'function') stop();

    const vo = _valueOverlays.get(id);
    if (delta === 0) {
        if (vo) vo.el.textContent = `${Math.round(endVal)}${vo.suffix}`;
        _lasts.set(id, { v: endVal, t: now });
        _anims.delete(id);
        return;
    }

    let raf = 0;
    const t0 = now;
    function frame(tNow) {
        const t = Math.min(1, (tNow - t0) / ms);
        const v = startVal + (endVal - startVal) * easeOutCubic(t);
        g.value = v;
        if (vo) vo.el.textContent = `${Math.round(v)}${vo.suffix}`;
        if (t < 1) {
            raf = requestAnimationFrame(frame);
        } else {
            _lasts.set(id, { v: endVal, t: performance.now() });
            _anims.delete(id);
        }
    }
    raf = requestAnimationFrame(frame);
    _anims.set(id, () => cancelAnimationFrame(raf));
}

export function setSmooth(id, value, options) {
    // options can be { durSmall, durMed, durBig, durMax, curve } or omitted
    // _animateTo expects an options map keyed by id; normalize here:
    const perId = {};
    if (options && typeof options === 'object') perId[id] = options;
    _animateTo(id, value, perId);
}

export function setManySmooth(valuesById, options) {
    if (!valuesById) return;
    for (const [id, v] of Object.entries(valuesById)) {
        _animateTo(id, v, options);
    }
}

export function setStates(stateById) {
    for (const [id, state] of Object.entries(stateById || {})) {
        const host = document.getElementById(id)?.parentElement;
        if (!host) continue;
        host.classList.remove('is-good', 'is-warn', 'is-bad');
        if (state) host.classList.add(state);
    }
}

export function disposeGauge(canvasId) {
    const ro = _resize.get(canvasId);
    try { ro?.disconnect?.(); } catch { }
    _resize.delete(canvasId);
    _valueOverlays.delete(canvasId);
    _gauges.delete(canvasId);
    _els.delete(canvasId);
}

export function disposeAll() {
    for (const id of Array.from(_gauges.keys())) disposeGauge(id);
}

function _host(el) { return el.parentElement || el; }

function _fitOne(id, gauge) {
    const el = _els.get(id) || gauge?.options?.renderTo;
    if (!el) return;
    const host = _host(el);
    const s = Math.max(140, Math.floor(Math.min(host.clientWidth, host.clientHeight)));
    el.width = s;
    el.height = s;
    if (gauge) {
        gauge.update({ width: s, height: s });
        try { gauge.draw(); } catch { }
    }
}

export function refreshOnShow() {
    requestAnimationFrame(() => {
        for (const [id, g] of _gauges) _fitOne(id, g);
        window.dispatchEvent(new Event('resize'));
    });
}