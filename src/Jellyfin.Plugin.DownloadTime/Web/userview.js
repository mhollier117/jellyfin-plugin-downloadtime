// Download Time — user-facing "Missing Media" drawer entry + overlay.
// Injected into every web session via File Transformation (gated by ShowUserPage).
// Read-only for regular users; admins additionally get scan + mute controls.
(function () {
    'use strict';
    var GUID = '4d557ba6-d562-4209-9a04-b782775dc2ff';
    var state = { report: null, isAdmin: false, filter: 'all', search: '', sort: 'most', muteOverride: {}, scanning: false, pollTimer: null, loaded: false };
    var overlay = null;

    /* ---------- utils (mirrors reportPage; duplication preferred over coupling) ---------- */
    function esc(s) { var d = document.createElement('div'); d.textContent = (s == null ? '' : String(s)); return d.innerHTML; }
    function idN(id) { return String(id || '').replace(/-/g, '').toLowerCase(); }
    function num(n) { return (n || 0).toLocaleString(); }
    function fmtDate(d) {
        if (!d) return '';
        try { return new Date(d).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }); } catch (e) { return ''; }
    }
    function relTime(d) {
        var ms = Date.now() - new Date(d).getTime();
        if (!(ms >= 0)) return '';
        var m = Math.floor(ms / 60000);
        if (m < 1) return 'just now';
        if (m < 60) return m + 'm ago';
        var h = Math.floor(m / 60);
        if (h < 24) return h + 'h ago';
        return Math.floor(h / 24) + 'd ago';
    }
    function hasScan(r) { return r && r.StartedAt && r.StartedAt.indexOf('0001-') !== 0; }
    function isMuted(s) {
        var o = state.muteOverride[idN(s.ItemId)];
        return o === undefined ? !!s.Muted : o;
    }
    function counts(s) {
        var g = 0, n = 0, sp = 0, ex = 0;
        (s.Missing || []).forEach(function (m) {
            if (m.Classification === 'Extra') { ex++; }
            else if (m.Classification === 'Special' || (m.Classification == null && m.IsSpecial)) { sp++; }
            else if (m.Kind === 'New') { n++; } else { g++; }
        });
        return { g: g, n: n, sp: sp, ex: ex, t: g + n + sp + ex };
    }
    function newestAir(s) {
        var best = 0;
        (s.Missing || []).forEach(function (m) { if (m.AiredAt) { var t = new Date(m.AiredAt).getTime(); if (t > best) best = t; } });
        return best;
    }

    /* ---------- overlay shell ---------- */
    function buildOverlay() {
        if (overlay) return overlay;
        overlay = document.createElement('div');
        overlay.className = 'dt-uv-overlay';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-label', 'Missing Media');
        overlay.innerHTML =
            '<div class="dt-uv-shell">'
            + '<button type="button" class="dt-uv-close" data-uvact="close" title="Close (Esc)" aria-label="Close">&#10005;</button>'
            + '<div class="dt-uv-head"><h1>Missing Media</h1><span class="dt-uv-fresh" id="dtUvFresh"></span>'
            + '<span class="dt-uv-scanstate" id="dtUvScanState"><span class="dt-uv-spin"></span>Scanning&#8230;</span>'
            + '<div class="dt-uv-headbtns" id="dtUvHeadBtns"></div></div>'
            + '<div class="dt-uv-tiles" id="dtUvTiles"></div>'
            + '<div class="dt-uv-bar">'
            + '<input type="search" class="dt-uv-search" id="dtUvSearch" placeholder="Search shows and movies&#8230;" autocomplete="off" />'
            + '<div class="dt-uv-chips" id="dtUvChips"></div>'
            + '<select class="dt-uv-sort" id="dtUvSort" aria-label="Sort">'
            + '<option value="most">Most missing</option><option value="recent">Recently aired</option><option value="az">A&#8211;Z</option>'
            + '</select></div>'
            + '<div id="dtUvBody"></div>'
            + '</div>';
        document.body.appendChild(overlay);
        wireOverlay();
        return overlay;
    }

    function openOverlay() {
        buildOverlay().classList.add('dt-uv-open');
        document.addEventListener('keydown', onKey);
        if (!state.loaded) { refresh(); } else { renderAll(); }
    }

    function closeOverlay() {
        if (overlay) overlay.classList.remove('dt-uv-open');
        document.removeEventListener('keydown', onKey);
    }

    function onKey(e) { if (e.key === 'Escape') closeOverlay(); }

    /* ---------- data ---------- */
    function client() { return window.ApiClient; }

    function refresh() {
        var api = client();
        if (!api) return;
        Promise.all([
            api.ajax({ type: 'GET', url: api.getUrl('DownloadTime/Report'), dataType: 'json' }),
            api.getCurrentUser().catch(function () { return null; })
        ]).then(function (res) {
            state.report = res[0];
            state.isAdmin = !!(res[1] && res[1].Policy && res[1].Policy.IsAdministrator);
            state.loaded = true;
            renderAll();
        }).catch(function () {
            var body = overlay && overlay.querySelector('#dtUvBody');
            if (body) body.innerHTML = '<div class="dt-uv-note">Failed to load the missing-media report.</div>';
        });
    }

    /* ---------- render ---------- */
    function totals() {
        var t = { shows: 0, gaps: 0, news: 0, specials: 0, extras: 0, movies: 0 };
        var r = state.report || {};
        (r.Series || []).forEach(function (s) {
            if (isMuted(s)) return;
            var c = counts(s);
            if (c.t > 0) t.shows++;
            t.gaps += c.g; t.news += c.n; t.specials += c.sp; t.extras += c.ex;
        });
        (r.Collections || []).forEach(function (c) { t.movies += (c.Missing || []).length; });
        return t;
    }

    function renderHead() {
        var fresh = overlay.querySelector('#dtUvFresh');
        var r = state.report;
        if (!hasScan(r)) { fresh.textContent = 'No scan yet'; fresh.title = ''; }
        else { fresh.textContent = 'Scanned ' + relTime(r.FinishedAt); fresh.title = new Date(r.FinishedAt).toLocaleString(); }
        overlay.querySelector('#dtUvHeadBtns').innerHTML = state.isAdmin
            ? '<button type="button" class="dt-uv-btn" data-uvact="scan"' + (state.scanning ? ' disabled' : '') + '>Scan now</button>'
            + '<button type="button" class="dt-uv-btn" data-uvact="scanfull" title="Ignore cached source data"' + (state.scanning ? ' disabled' : '') + '>Full refresh</button>'
            : '';
    }

    function renderTiles() {
        var t = totals();
        var defs = [
            { k: 'all', cls: '', n: t.shows, l: 'Shows with missing' },
            { k: 'gaps', cls: 'dt-uv-t-gap', n: t.gaps, l: 'Gaps' },
            { k: 'new', cls: 'dt-uv-t-new', n: t.news, l: 'New episodes' },
            { k: 'specials', cls: '', n: t.specials, l: 'Missing specials' },
            { k: 'movies', cls: '', n: t.movies, l: 'Missing movies' }
        ];
        if (t.extras > 0) { defs.splice(4, 0, { k: 'extras', cls: '', n: t.extras, l: 'Extras' }); }
        overlay.querySelector('#dtUvTiles').innerHTML = defs.map(function (d) {
            return '<button type="button" class="dt-uv-tile ' + d.cls + (state.filter === d.k ? ' dt-uv-sel' : '') + '" data-uvfilter="' + d.k + '">'
                + '<span class="dt-uv-n">' + num(d.n) + '</span><span class="dt-uv-l">' + d.l + '</span></button>';
        }).join('');
    }

    function renderChips() {
        var defs = [
            { k: 'all', l: 'All' }, { k: 'gaps', l: 'Gaps only' }, { k: 'new', l: 'New only' },
            { k: 'specials', l: 'Specials' }, { k: 'extras', l: 'Extras' },
            { k: 'movies', l: 'Movies' }, { k: 'errors', l: 'Errors' }
        ];
        if (state.isAdmin) defs.push({ k: 'muted', l: 'Muted' });
        overlay.querySelector('#dtUvChips').innerHTML = defs.map(function (d) {
            return '<button type="button" class="dt-uv-chip' + (state.filter === d.k ? ' dt-uv-sel' : '') + '" data-uvfilter="' + d.k + '">' + d.l + '</button>';
        }).join('');
    }

    var EP_PREVIEW = 50;

    function epRow(m) {
        var code = m.EntryName
            ? (m.IsSpecial ? m.EntryName : m.EntryName + ' E' + String(m.Number == null ? '?' : m.Number).padStart(2, '0')
                + (m.AbsoluteNumber != null ? ' \u00b7 abs ' + m.AbsoluteNumber : ''))
            : 'S' + String(m.Season == null ? '?' : m.Season).padStart(2, '0') + 'E' + String(m.Number == null ? '?' : m.Number).padStart(2, '0');
        var kind = m.Classification === 'Extra' ? '<span class="dt-uv-k dt-uv-x">Extra</span>'
            : m.IsSpecial ? '<span class="dt-uv-k dt-uv-s">Special</span>'
            : '<span class="dt-uv-k ' + (m.Kind === 'New' ? 'dt-uv-n' : 'dt-uv-g') + '">' + (m.Kind === 'New' ? 'New' : 'Gap') + '</span>';
        var rt = m.RuntimeMinutes != null ? '<span class="dt-uv-epdate">' + m.RuntimeMinutes + ' min</span>' : '';
        return '<div class="dt-uv-ep"><span class="dt-uv-code">' + esc(code) + '</span>'
            + '<span class="dt-uv-eptitle">' + esc(m.Title || '') + '</span>'
            + (m.AiredAt ? '<span class="dt-uv-epdate">aired ' + esc(fmtDate(m.AiredAt)) + '</span>' : '')
            + rt + kind + '</div>';
    }

    function epList(list) {
        var html = '', bySeason = {}, order = [];
        list.forEach(function (m) {
            var k = m.EntryName ? 'entry:' + m.EntryName : (m.Season == null ? '?' : m.Season);
            if (!bySeason[k]) { bySeason[k] = []; order.push(k); }
            bySeason[k].push(m);
        });
        order.forEach(function (k) {
            var label = (typeof k === 'string' && k.indexOf('entry:') === 0) ? esc(k.slice(6))
                : (k === '?' ? 'Unknown season' : (k === 0 || k === '0' ? 'Specials' : 'Season ' + k));
            html += '<div class="dt-uv-season">' + label + '</div>';
            html += bySeason[k].map(epRow).join('');
        });
        return html;
    }

    function filteredEps(s) {
        var eps = s.Missing || [];
        if (state.filter === 'gaps') eps = eps.filter(function (m) { return m.Kind !== 'New' && !m.IsSpecial && m.Classification !== 'Extra'; });
        if (state.filter === 'new') eps = eps.filter(function (m) { return m.Kind === 'New' && !m.IsSpecial && m.Classification !== 'Extra'; });
        if (state.filter === 'specials') eps = eps.filter(function (m) { return m.Classification === 'Extra' ? false : !!m.IsSpecial; });
        if (state.filter === 'extras') eps = eps.filter(function (m) { return m.Classification === 'Extra'; });
        return eps;
    }

    function seriesCard(s) {
        var c = counts(s);
        var muted = isMuted(s);
        var eps = filteredEps(s);
        var nId = idN(s.ItemId);
        var api = client();
        var poster = '<div class="dt-uv-poster"><img loading="lazy" src="' + api.getScaledImageUrl(s.ItemId, { type: 'Primary', maxHeight: 300 }) + '" alt="" />'
            + '<div class="dt-uv-mono" style="display:none">' + esc((s.Name || '?').charAt(0).toUpperCase()) + '</div></div>';
        var pills = '<span class="dt-uv-pill">' + esc(s.Lane || '?') + (s.UsedFallback ? ' &#183; fallback' : '') + '</span>'
            + (s.Error ? '<span class="dt-uv-pill dt-uv-warn">error</span>' : '');
        var chips = (c.g ? '<span class="dt-uv-cg">' + num(c.g) + ' gap' + (c.g === 1 ? '' : 's') + '</span>' : '')
            + (c.n ? '<span class="dt-uv-cn">' + num(c.n) + ' new</span>' : '')
            + (c.sp ? '<span class="dt-uv-cs">' + num(c.sp) + ' special' + (c.sp === 1 ? '' : 's') + '</span>' : '')
            + (c.ex ? '<span class="dt-uv-cx">' + num(c.ex) + ' extra' + (c.ex === 1 ? '' : 's') + '</span>' : '');
        var muteBtn = state.isAdmin
            ? '<button type="button" class="dt-uv-ibtn" data-uvact="' + (muted ? 'unmute' : 'mute') + '" data-uvid="' + nId + '" title="' + (muted ? 'Unmute' : 'Mute') + ' ' + esc(s.Name) + '">' + (muted ? '&#128266;' : '&#128263;') + '</button>'
            : '';
        var preview = eps.slice(0, EP_PREVIEW);
        var detail = epList(preview)
            + (eps.length > EP_PREVIEW ? '<button type="button" class="dt-uv-more" data-uvact="showall" data-uvid="' + nId + '">Show all ' + num(eps.length) + ' episodes</button>' : '')
            + (s.Notes || []).map(function (n) { return '<div class="dt-uv-note">' + esc(n) + '</div>'; }).join('');
        return '<div class="dt-uv-card" data-uvid="' + nId + '">'
            + '<div class="dt-uv-row" tabindex="0" role="button" data-uvact="toggle">'
            + poster
            + '<div class="dt-uv-main"><div class="dt-uv-name">' + esc(s.Name) + '</div><div class="dt-uv-meta">' + pills + chips + '</div></div>'
            + '<div class="dt-uv-ctl">' + muteBtn + '<span class="dt-uv-ibtn dt-uv-chev">&#8250;</span></div>'
            + '</div><div class="dt-uv-detail">' + detail + '</div></div>';
    }

    function collectionCard(c) {
        var rows = (c.Missing || []).map(function (m) {
            return '<div class="dt-uv-ep"><span class="dt-uv-eptitle">' + esc(m.Title) + '</span>'
                + (m.ReleasedAt ? '<span class="dt-uv-epdate">released ' + esc(fmtDate(m.ReleasedAt)) + '</span>' : '<span class="dt-uv-epdate">unreleased</span>')
                + '</div>';
        }).join('');
        return '<div class="dt-uv-card dt-uv-openc"><div class="dt-uv-row" style="cursor:default">'
            + '<div class="dt-uv-main"><div class="dt-uv-name">' + esc(c.Name) + '</div>'
            + '<div class="dt-uv-meta"><span class="dt-uv-via">via ' + esc(c.ViaMovie) + '</span>'
            + '<span class="dt-uv-cn">' + num((c.Missing || []).length) + ' missing</span></div></div>'
            + '</div><div class="dt-uv-detail" style="display:block">' + rows + '</div></div>';
    }

    function matchesSearch(name) { return !state.search || (name || '').toLowerCase().indexOf(state.search) !== -1; }

    function sortSeries(list) {
        var s = state.sort;
        return list.sort(function (a, b) {
            if (s === 'az') return (a.Name || '').localeCompare(b.Name || '');
            if (s === 'recent') return newestAir(b) - newestAir(a);
            return counts(b).t - counts(a).t;
        });
    }

    function renderBody() {
        var r = state.report || {};
        var body = overlay.querySelector('#dtUvBody');
        if (!hasScan(r)) {
            body.innerHTML = '<div class="dt-uv-hero"><div class="dt-uv-big">&#128269;</div><h2>No scan yet</h2><p>'
                + (state.isAdmin ? 'Run your first scan to find missing episodes and movies.' : 'The server has not scanned for missing media yet.') + '</p></div>';
            return;
        }
        var f = state.filter;
        var all = r.Series || [];
        // Non-admins never see muted shows at all.
        var visible = state.isAdmin ? all : all.filter(function (s) { return !isMuted(s); });
        var active = visible.filter(function (s) { return !isMuted(s); });
        var mutedList = state.isAdmin ? all.filter(isMuted) : [];
        var errored = active.filter(function (s) { return s.Error; });
        var html = '';

        if (f !== 'muted' && f !== 'errors' && f !== 'movies') {
            var shows = active.filter(function (s) {
                var c = counts(s);
                if (!matchesSearch(s.Name)) return false;
                if (f === 'gaps') return c.g > 0;
                if (f === 'new') return c.n > 0;
                if (f === 'specials') return c.sp > 0;
                if (f === 'extras') return c.ex > 0;
                return c.t > 0;
            });
            sortSeries(shows);
            if (shows.length) {
                html += '<div class="dt-uv-sect">Shows &#183; ' + num(shows.length) + '</div>' + shows.map(seriesCard).join('');
            }
        }

        if (f === 'all' || f === 'movies') {
            var cols = (r.Collections || []).filter(function (c) {
                return matchesSearch(c.Name) || (c.Missing || []).some(function (m) { return matchesSearch(m.Title); });
            });
            if (cols.length) {
                html += '<div class="dt-uv-sect">Movie collections &#183; ' + num(cols.length) + '</div>' + cols.map(collectionCard).join('');
            }
        }

        if (f === 'errors') {
            html += '<div class="dt-uv-sect">Errors &#183; ' + num(errored.length) + '</div>';
            html += errored.length
                ? errored.map(function (s) { return '<div class="dt-uv-err"><span class="dt-uv-errname">' + esc(s.Name) + '</span><span class="dt-uv-errmsg">' + esc(s.Error) + '</span></div>'; }).join('')
                : '<div class="dt-uv-note">No source errors. &#127881;</div>';
            (r.GlobalNotes || []).forEach(function (n) { html += '<div class="dt-uv-err"><span class="dt-uv-errmsg">' + esc(n) + '</span></div>'; });
        }

        if (f === 'muted' && state.isAdmin) {
            html += '<div class="dt-uv-sect">Muted &#183; ' + num(mutedList.length) + '</div>';
            html += mutedList.length
                ? mutedList.filter(function (s) { return matchesSearch(s.Name); }).map(seriesCard).join('')
                : '<div class="dt-uv-note">Nothing muted.</div>';
        }

        if (f === 'all' && !html) {
            html = '<div class="dt-uv-hero"><div class="dt-uv-big">&#127881;</div><h2>All caught up</h2><p>No missing episodes or movies anywhere in the library.</p></div>';
        }
        body.innerHTML = html;
    }

    function renderAll() {
        if (!overlay) return;
        renderHead();
        renderTiles();
        renderChips();
        renderBody();
    }

    /* ---------- admin actions ---------- */
    function setMuted(nId, muted) {
        if (!state.isAdmin) return;
        state.muteOverride[nId] = muted;
        renderAll();
        var api = client();
        api.getPluginConfiguration(GUID).then(function (c) {
            var list = (c.ExcludedItemIds || []).filter(function (x) { return idN(x) !== nId; });
            if (muted) list.push(nId);
            c.ExcludedItemIds = list;
            return api.updatePluginConfiguration(GUID, c);
        }).catch(function () {
            delete state.muteOverride[nId];
            renderAll();
        });
    }

    function setScanning(on) {
        state.scanning = on;
        overlay.querySelector('#dtUvScanState').classList.toggle('dt-uv-on', on);
        renderHead();
    }

    function pollUntilDone(prevFinished) {
        clearTimeout(state.pollTimer);
        state.pollTimer = setTimeout(function () {
            var api = client();
            api.ajax({ type: 'GET', url: api.getUrl('DownloadTime/Report'), dataType: 'json' }).then(function (r) {
                if (r && r.FinishedAt !== prevFinished) {
                    state.report = r;
                    state.muteOverride = {};
                    setScanning(false);
                    renderAll();
                } else { pollUntilDone(prevFinished); }
            }).catch(function () { pollUntilDone(prevFinished); });
        }, 10000);
    }

    function startScan(full) {
        if (!state.isAdmin || state.scanning) return;
        var prev = state.report ? state.report.FinishedAt : null;
        setScanning(true);
        var api = client();
        api.ajax({ type: 'POST', url: api.getUrl('DownloadTime/Scan', { fullRefresh: !!full }) })
            .then(function () { pollUntilDone(prev); })
            .catch(function (xhr) {
                if (xhr && xhr.status === 409) { pollUntilDone(prev); } else { setScanning(false); }
            });
    }

    /* ---------- overlay events ---------- */
    function wireOverlay() {
        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) { closeOverlay(); return; }
            var t = e.target.closest ? e.target.closest('[data-uvact],[data-uvfilter]') : null;
            if (!t) return;
            var filter = t.getAttribute('data-uvfilter');
            if (filter) {
                state.filter = (state.filter === filter && filter !== 'all') ? 'all' : filter;
                renderAll();
                return;
            }
            var act = t.getAttribute('data-uvact');
            var nId = t.getAttribute('data-uvid');
            if (act === 'close') { closeOverlay(); }
            else if (act === 'scan') { startScan(false); }
            else if (act === 'scanfull') { startScan(true); }
            else if (act === 'mute' || act === 'unmute') { e.stopPropagation(); setMuted(nId, act === 'mute'); }
            else if (act === 'showall') {
                e.stopPropagation();
                var series = (state.report.Series || []).find(function (s) { return idN(s.ItemId) === nId; });
                if (series) {
                    t.parentElement.innerHTML = epList(filteredEps(series))
                        + (series.Notes || []).map(function (n) { return '<div class="dt-uv-note">' + esc(n) + '</div>'; }).join('');
                }
            } else if (act === 'toggle') {
                var card = t.closest('.dt-uv-card');
                if (card) card.classList.toggle('dt-uv-openc');
            }
        });
        overlay.addEventListener('keydown', function (e) {
            if ((e.key === 'Enter' || e.key === ' ') && e.target.getAttribute && e.target.getAttribute('data-uvact') === 'toggle') {
                e.preventDefault();
                e.target.closest('.dt-uv-card').classList.toggle('dt-uv-openc');
            }
        });
        overlay.addEventListener('error', function (e) {
            var img = e.target;
            if (img.tagName === 'IMG' && img.closest('.dt-uv-poster')) {
                img.style.display = 'none';
                var mono = img.parentElement.querySelector('.dt-uv-mono');
                if (mono) mono.style.display = 'flex';
            }
        }, true);
        overlay.querySelector('#dtUvSearch').addEventListener('input', function () {
            state.search = this.value.trim().toLowerCase();
            renderBody();
        });
        overlay.querySelector('#dtUvSort').addEventListener('change', function () {
            state.sort = this.value;
            renderBody();
        });
    }

    /* ---------- drawer entry ---------- */
    function injectNavItem() {
        var drawer = document.querySelector('.mainDrawer-scrollContainer');
        if (!drawer || drawer.querySelector('.dt-uv-nav-item')) return;
        var item = document.createElement('a');
        item.className = 'navMenuOption emby-button dt-uv-nav-item';
        item.href = '#';
        item.innerHTML = '<span class="material-icons navMenuOptionIcon" aria-hidden="true">event_busy</span>'
            + '<span class="navMenuOptionText">Missing Media</span>';
        item.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            openOverlay();
            // close the drawer the way jellyfin-web expects: click the backdrop if present
            var backdrop = document.querySelector('.mainDrawer-backdrop, .backdropContainer + .mainDrawerHandle');
            document.body.classList.remove('bodyWithPopupOpen');
            var scrim = document.querySelector('.mainDrawer-backdrop');
            if (scrim) scrim.click();
            void backdrop;
        });
        // place after the Home entry when we can find it, else append at the end
        var home = drawer.querySelector('a.navMenuOption[href*="home"]');
        if (home && home.parentElement) {
            home.parentElement.insertBefore(item, home.nextSibling);
        } else {
            drawer.appendChild(item);
        }
    }

    function boot() {
        if (!window.ApiClient || !document.body) { setTimeout(boot, 2000); return; }
        injectNavItem();
        new MutationObserver(function () { injectNavItem(); })
            .observe(document.body, { childList: true, subtree: true });
    }
    boot();
})();
