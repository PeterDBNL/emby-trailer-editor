define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading) {
    'use strict';

    var pluginId = '6f1d7a7e-3c1b-4f4e-9b8a-2d5c0e7a91b4';
    var pageSize = 50;

    var kindLabels = {
        Unplayable: "Won't play",
        NoTrailers: 'No trailers',
        InvalidLink: 'Invalid link',
        DuplicateLink: 'Duplicate link'
    };

    var settingNames = ['PinEditedTrailers', 'ScanReportNoTrailers', 'ScanIgnoreItemsWithLocalTrailers',
        'ScanCheckLinks', 'ScanCheckAgeRestriction', 'ScanRequestDelayMs', 'ScanRecheckDays'];

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function api(method, path, query) {
        var url = ApiClient.getUrl(path);
        if (query) {
            var parts = Object.keys(query).filter(function (k) { return query[k] !== undefined && query[k] !== null && query[k] !== ''; })
                .map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(query[k]); });
            if (parts.length) { url += (url.indexOf('?') === -1 ? '?' : '&') + parts.join('&'); }
        }
        return ApiClient.ajax({ type: method, url: url, dataType: 'json' });
    }

    function editorLink(id) {
        return '#!/configurationpage?name=trailereditor&id=' + encodeURIComponent(id);
    }

    function itemMeta(year, type) {
        var parts = [];
        if (year) { parts.push(year); }
        parts.push(type === 'Series' ? 'TV show' : type);
        return parts.join(' · ');
    }

    function formatDate(value) {
        if (!value) { return ''; }
        var d = new Date(value);
        return isNaN(d.getTime()) ? '' : d.toLocaleString();
    }

    function number(n) { return (n || 0).toLocaleString(); }

    function debounce(fn, ms) {
        var timer = null;
        return function () { clearTimeout(timer); timer = setTimeout(fn, ms); };
    }

    // A searchable, paged list that loads when its <details> section is opened.
    function PagedList(view, options) {
        var start = 0;
        var loaded = false;
        var seq = 0;
        var container = view.querySelector(options.list);
        var pager = view.querySelector(options.pager);

        function load() {
            var mySeq = ++seq;
            loaded = true;
            container.innerHTML = '<p class="tl-meta">Loading…</p>';
            api('GET', options.path, Object.assign({ StartIndex: start, Limit: pageSize }, options.query())).then(function (page) {
                if (mySeq !== seq) { return; }
                var items = page.Items || [];
                container.innerHTML = items.length ? items.map(options.render).join('') :
                    '<p class="tl-meta">' + escapeHtml(options.empty()) + '</p>';
                renderPager(page.TotalRecordCount || 0);
            }, function () {
                if (mySeq !== seq) { return; }
                container.innerHTML = '<p class="tl-meta">Couldn\'t load this list.</p>';
                pager.innerHTML = '';
            });
        }

        function renderPager(total) {
            if (total <= pageSize) {
                pager.innerHTML = total ? '<span class="tl-meta">' + number(total) + ' in total</span>' : '';
                return;
            }
            pager.innerHTML =
                '<button is="emby-button" type="button" class="raised tl-prev"' + (start === 0 ? ' disabled' : '') + '><span>Previous</span></button>' +
                '<span class="tl-meta">' + number(start + 1) + '–' + number(Math.min(start + pageSize, total)) + ' of ' + number(total) + '</span>' +
                '<button is="emby-button" type="button" class="raised tl-next"' + (start + pageSize >= total ? ' disabled' : '') + '><span>Next</span></button>';
            pager.querySelector('.tl-prev').addEventListener('click', function () { start = Math.max(0, start - pageSize); load(); });
            pager.querySelector('.tl-next').addEventListener('click', function () { start += pageSize; load(); });
        }

        view.querySelector(options.section).addEventListener('toggle', function () {
            if (this.open && !loaded) { load(); }
        });

        return {
            reset: function () { start = 0; load(); },
            refresh: function () { if (loaded) { load(); } },
            isOpen: function () { return view.querySelector(options.section).open; }
        };
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        var self = this;
        var pollTimer = null;
        var lastState = null;
        var lastIssueCount = null;

        function q(selector) { return view.querySelector(selector); }

        var issues = PagedList(view, {
            section: '.tl-issuesSection',
            list: '.tl-issues',
            pager: '.tl-issuesPager',
            path: 'TrailerEditor/Scan/Issues',
            query: function () { return { Kind: q('.tl-kind').value, SearchTerm: q('.tl-issueSearch').value.trim() }; },
            empty: function () { return lastState && lastState.HasResults ? 'No problems found.' : 'No scan has run yet. Press Scan now.'; },
            render: function (i) {
                return '<a class="tl-row" href="' + editorLink(i.ItemId) + '">' +
                    '<div>' + escapeHtml(i.ItemName) + ' <span class="tl-meta">' + escapeHtml(itemMeta(i.Year, i.Type)) + '</span></div>' +
                    '<div class="tl-issue tl-kind-' + escapeHtml(i.Kind) + '">' + escapeHtml(kindLabels[i.Kind] || i.Kind) + ': ' + escapeHtml(i.Message) + '</div>' +
                    (i.Url ? '<div class="tl-url">' + escapeHtml(i.Url) + '</div>' : '') +
                    '</a>';
            }
        });

        var pinned = PagedList(view, {
            section: '.tl-pinnedSection',
            list: '.tl-pinned',
            pager: '.tl-pinnedPager',
            path: 'TrailerEditor/Pinned',
            query: function () { return { SearchTerm: q('.tl-pinnedSearch').value.trim() }; },
            empty: function () { return q('.tl-pinnedSearch').value.trim() ? 'No pinned items match.' : 'No pinned items yet.'; },
            render: function (item) {
                var count = (item.TrailerUrls || []).length;
                return '<a class="tl-row" href="' + editorLink(item.Id) + '">' +
                    '<div>' + escapeHtml(item.Name) + ' <span class="tl-meta">' + escapeHtml(itemMeta(item.Year, item.Type)) +
                    ' · ' + (count === 1 ? '1 trailer' : count + ' trailers') + '</span></div></a>';
            }
        });

        q('.tl-kind').addEventListener('change', function () { issues.reset(); });
        q('.tl-issueSearch').addEventListener('input', debounce(function () { issues.reset(); }, 400));
        q('.tl-pinnedSearch').addEventListener('input', debounce(function () { pinned.reset(); }, 400));

        function renderStatus(s) {
            lastState = s;
            var running = s.State === 'Running' || s.State === 'Cancelling';
            var status = q('.tl-status');
            var bar = q('.tl-bar');

            if (running) {
                var pct = Math.round(s.Progress || 0);
                status.textContent = s.State === 'Cancelling' ? 'Stopping the scan…' : 'Scanning… ' + pct + '%';
                bar.style.display = '';
                bar.firstElementChild.style.width = pct + '%';
            } else {
                bar.style.display = 'none';
                if (!s.HasResults) {
                    status.textContent = 'No scan has run yet.';
                } else {
                    status.innerHTML =
                        (s.Completed ? 'Last scan finished ' : 'Last scan stopped ') + escapeHtml(formatDate(s.FinishedUtc)) + '.<br>' +
                        '<span class="tl-meta">' + number(s.ItemsScanned) + ' titles, ' + number(s.LinksTotal) + ' trailer links: ' +
                        number(s.LinksChecked) + ' checked with YouTube, ' + number(s.LinksFromCache) + ' from earlier results' +
                        (s.LinksNotChecked ? ', ' + number(s.LinksNotChecked) + ' not checked' : '') + '.</span>';
                }
            }

            var note = q('.tl-note');
            note.textContent = s.Note || '';
            note.style.display = s.Note && !running ? '' : 'none';

            var chips = [];
            if (s.HasResults) {
                chips.push(['', number(s.IssueCount) + ' problems']);
                if (s.UnplayableCount) { chips.push(['Unplayable', number(s.UnplayableCount) + " won't play"]); }
                if (s.NoTrailersCount) { chips.push(['NoTrailers', number(s.NoTrailersCount) + ' without trailers']); }
                if (s.InvalidLinkCount) { chips.push(['InvalidLink', number(s.InvalidLinkCount) + ' invalid links']); }
                if (s.DuplicateLinkCount) { chips.push(['DuplicateLink', number(s.DuplicateLinkCount) + ' duplicate links']); }
            }
            q('.tl-chips').innerHTML = chips.map(function (c) {
                return '<button type="button" class="tl-chip" data-kind="' + c[0] + '">' + escapeHtml(c[1]) + '</button>';
            }).join('');

            q('.tl-issuesCount').textContent = s.HasResults ? '(' + number(s.IssueCount) + ')' : '';
            q('.tl-pinnedCount').textContent = '(' + number(s.PinnedCount) + ')';
            q('.tl-scan').style.display = running ? 'none' : '';
            q('.tl-cancel').style.display = s.State === 'Running' ? '' : 'none';

            // Refresh the open lists when a scan finishes.
            if (lastIssueCount !== null && !running && (s.IssueCount !== lastIssueCount || s.FinishedUtc !== self._lastFinished)) {
                issues.refresh();
            }
            lastIssueCount = s.IssueCount;
            self._lastFinished = s.FinishedUtc;

            schedulePoll(running ? 2000 : 15000);
        }

        function loadStatus() {
            return api('GET', 'TrailerEditor/Scan').then(renderStatus, function () {
                q('.tl-status').textContent = 'Couldn\'t load the scan status.';
                schedulePoll(15000);
            });
        }

        function schedulePoll(ms) {
            clearTimeout(pollTimer);
            if (self._active) { pollTimer = setTimeout(loadStatus, ms); }
        }

        q('.tl-chips').addEventListener('click', function (e) {
            var chip = e.target.closest('.tl-chip');
            if (!chip) { return; }
            q('.tl-kind').value = chip.getAttribute('data-kind');
            q('.tl-issuesSection').open = true;
            issues.reset();
            q('.tl-issuesSection').scrollIntoView({ behavior: 'smooth', block: 'start' });
        });

        q('.tl-scan').addEventListener('click', function () {
            api('POST', 'TrailerEditor/Scan').then(renderStatus, function () {
                q('.tl-status').textContent = 'Couldn\'t start the scan.';
            });
        });

        q('.tl-cancel').addEventListener('click', function () {
            api('POST', 'TrailerEditor/Scan/Cancel').then(renderStatus);
        });

        function loadSettings() {
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                settingNames.forEach(function (name) {
                    var el = q('.tl-' + name);
                    if (el.type === 'checkbox') { el.checked = !!config[name]; } else { el.value = config[name]; }
                });
            });
        }

        q('.tl-saveSettings').addEventListener('click', function () {
            loading.show();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                settingNames.forEach(function (name) {
                    var el = q('.tl-' + name);
                    if (el.type === 'checkbox') {
                        config[name] = el.checked;
                    } else {
                        var n = parseInt(el.value, 10);
                        if (!isNaN(n)) { config[name] = Math.max(0, n); }
                    }
                });
                return ApiClient.updatePluginConfiguration(pluginId, config);
            }).then(function () {
                loading.hide();
                q('.tl-settingsMsg').textContent = 'Settings saved.';
            }, function () {
                loading.hide();
                q('.tl-settingsMsg').textContent = 'Couldn\'t save the settings.';
            });
        });

        this.loadStatus = loadStatus;
        this.loadSettings = loadSettings;
        this.refreshLists = function () { issues.refresh(); pinned.refresh(); };
        this.stopPolling = function () { clearTimeout(pollTimer); };
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        this._active = true;
        this.loadStatus();
        this.loadSettings();
        this.refreshLists();
    };

    View.prototype.onPause = function () {
        if (BaseView.prototype.onPause) { BaseView.prototype.onPause.apply(this, arguments); }
        this._active = false;
        this.stopPolling();
    };

    return View;
});
