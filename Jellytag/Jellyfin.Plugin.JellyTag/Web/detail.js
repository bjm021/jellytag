// JellyTag: renders media badges on item detail pages of the Jellyfin web client.
(function () {
    'use strict';

    if (window.__jellytagDetail) {
        return;
    }
    window.__jellytagDetail = true;

    var inflightId = null;
    var timer = null;

    function currentItemId() {
        var m = /[?&]id=([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f-]{27})/i.exec(window.location.hash);
        return m ? m[1] : null;
    }

    function pick(obj, name) {
        if (!obj) {
            return undefined;
        }
        return obj[name] !== undefined ? obj[name] : obj[name.charAt(0).toLowerCase() + name.slice(1)];
    }

    // <img> can't send auth headers, so badge images are fetched with the token and shown from blob URLs
    var badgeUrls = {};

    function loadBadgeUrl(name) {
        if (!badgeUrls[name]) {
            var url = ApiClient.getUrl('JellyTag/BadgeImage/' + encodeURIComponent(name));
            badgeUrls[name] = fetch(url, { headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' } })
                .then(function (res) {
                    if (!res.ok) {
                        throw new Error('HTTP ' + res.status);
                    }
                    return res.blob();
                })
                .then(function (blob) { return URL.createObjectURL(blob); });
            badgeUrls[name].catch(function () { delete badgeUrls[name]; });
        }
        return badgeUrls[name];
    }

    // The ribbon has a fixed height and the poster overlaps it, so the row goes at the top of the
    // section holding the Video/Audio/Subtitles selectors, which already has the right left offset
    function findAnchor(page) {
        return page.querySelector('.detailPagePrimaryContent .detailSection');
    }

    function render(page, id, data) {
        var old = page.querySelector('.jellytag-detail-badges');
        if (old) {
            old.remove();
        }

        var anchor = findAnchor(page);
        if (!anchor) {
            return;
        }

        var height = parseInt(pick(data, 'Height'), 10) || 28;
        var badges = pick(data, 'Badges') || [];

        var box = document.createElement('div');
        box.className = 'jellytag-detail-badges';
        box.setAttribute('data-item-id', id);
        box.style.cssText = 'display:flex;flex-wrap:wrap;align-items:center;gap:.6em;margin:.4em 0 1em;';

        badges.forEach(function (b) {
            var image = pick(b, 'Image');
            if (!image) {
                return;
            }
            var img = document.createElement('img');
            img.alt = pick(b, 'Key') || '';
            img.title = pick(b, 'Key') || '';
            img.style.cssText = 'height:' + height + 'px;width:auto;display:block;';
            loadBadgeUrl(image).then(function (url) { img.src = url; }, function () { img.remove(); });
            box.appendChild(img);
        });

        anchor.insertBefore(box, anchor.firstChild);
    }

    function update() {
        if (typeof ApiClient === 'undefined' || !ApiClient.accessToken || !ApiClient.accessToken()) {
            return;
        }

        var page = document.querySelector('#itemDetailPage:not(.hide)');
        var id = currentItemId();
        if (!page || !id || !findAnchor(page)) {
            return;
        }

        var existing = page.querySelector('.jellytag-detail-badges');
        if (existing && existing.getAttribute('data-item-id') === id) {
            return;
        }
        if (inflightId === id) {
            return;
        }

        inflightId = id;
        ApiClient.fetch({ url: ApiClient.getUrl('JellyTag/ItemBadges/' + id), type: 'GET', dataType: 'json' })
            .then(function (data) {
                inflightId = null;
                var current = document.querySelector('#itemDetailPage:not(.hide)');
                if (current && currentItemId() === id) {
                    render(current, id, data);
                }
            })
            .catch(function () {
                inflightId = null;
            });
    }

    function schedule() {
        clearTimeout(timer);
        timer = setTimeout(update, 150);
    }

    new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
    window.addEventListener('hashchange', schedule);
    schedule();
})();
