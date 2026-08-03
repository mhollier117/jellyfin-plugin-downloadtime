// Download Time poster badges. Data: GET /DownloadTime/Report (session auth).
(function () {
    'use strict';
    var cfg = window.DownloadTimeConfig || { poster: true, detail: true };
    var counts = null; // itemIdNoDashes -> {gaps,news}

    function load() {
        var client = window.ApiClient;
        if (!client) { setTimeout(load, 2000); return; }
        client.ajax({ type: 'GET', url: client.getUrl('DownloadTime/Report'), dataType: 'json' })
            .then(function (r) {
                counts = {};
                (r.Series || []).forEach(function (s) {
                    var gaps = 0, news = 0, sp = 0;
                    (s.Missing || []).forEach(function (m) { if (m.IsSpecial) sp++; else if (m.Kind === 'Gap') gaps++; else news++; });
                    if (gaps + news + sp > 0) counts[String(s.ItemId).replace(/-/g, '').toLowerCase()] = { gaps: gaps, news: news, sp: sp };
                });
                decorate();
            })
            .catch(function () { setTimeout(load, 30000); });
    }

    function idFromCard(card) {
        var id = card.getAttribute('data-id');
        return id ? id.replace(/-/g, '').toLowerCase() : null;
    }

    function decorate() {
        if (!counts) return;
        if (cfg.poster) {
            document.querySelectorAll('.card[data-id]').forEach(function (card) {
                var id = idFromCard(card);
                var c = id && counts[id];
                var holder = card.querySelector('.cardImageContainer') || card.querySelector('.cardBox');
                if (!c || !holder || holder.querySelector('.dt-badge')) return;
                var b = document.createElement('div');
                b.className = 'dt-badge';
                b.textContent = c.gaps + c.news + c.sp;
                b.title = c.gaps + ' gap(s), ' + c.news + ' new' + (c.sp ? ', ' + c.sp + ' special(s)' : '');
                holder.appendChild(b);
            });
        }
        if (cfg.detail) {
            var page = document.querySelector('.itemDetailPage:not(.hide)');
            if (page && !page.querySelector('.dt-detail-line')) {
                var m = (location.hash.match(/id=([0-9a-fA-F-]{32,36})/) || [])[1];
                var c = m && counts[m.replace(/-/g, '').toLowerCase()];
                var anchor = page.querySelector('.itemName, .nameContainer');
                if (c && anchor) {
                    var line = document.createElement('div');
                    line.className = 'dt-detail-line';
                    line.textContent = (c.gaps + c.news + c.sp) + ' missing — ' + c.gaps + ' gap(s), ' + c.news + ' new' + (c.sp ? ', ' + c.sp + ' special(s)' : '');
                    anchor.parentElement.insertBefore(line, anchor.nextSibling);
                }
            }
        }
    }

    new MutationObserver(function () { decorate(); }).observe(document.body, { childList: true, subtree: true });
    load();
    setInterval(load, 30 * 60 * 1000); // refresh counts every 30 min
})();
