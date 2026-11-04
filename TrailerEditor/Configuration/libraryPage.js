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
        'ScanCheckLinks', 'ScanCheckAgeRestriction', 'ScanBrowserPlaybackTest', 'ScanLearnFromPlayback',
        'ScanRequestDelayMs', 'ScanRecheckDays'];

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

    // ---- Hidden YouTube player (same as on the Trailer Editor page) ----

    // Loads YouTube's IFrame Player API once, reusing it if Emby's own player already did.
    var ytApiPromise = null;
    function loadYouTubeApi() {
        if (window.YT && typeof window.YT.Player === 'function') { return Promise.resolve(window.YT); }
        if (ytApiPromise) { return ytApiPromise; }
        ytApiPromise = new Promise(function (resolve, reject) {
            var previous = window.onYouTubeIframeAPIReady;
            window.onYouTubeIframeAPIReady = function () {
                if (typeof previous === 'function') { try { previous(); } catch (e) { /* ignore */ } }
                resolve(window.YT);
            };
            if (!document.querySelector('script[src*="youtube.com/iframe_api"]')) {
                var script = document.createElement('script');
                script.src = 'https://www.youtube.com/iframe_api';
                script.async = true;
                script.onerror = function () { reject(new Error('load failed')); };
                document.head.appendChild(script);
            }
            var started = Date.now();
            (function poll() {
                if (window.YT && typeof window.YT.Player === 'function') { resolve(window.YT); return; }
                if (Date.now() - started > 10000) { reject(new Error('timeout')); return; }
                setTimeout(poll, 250);
            })();
        });
        ytApiPromise.catch(function () { ytApiPromise = null; });
        return ytApiPromise;
    }

    // YouTube IFrame player error codes.
    function playerError(code) {
        switch (code) {
            case 101:
            case 150:
                return { status: 'error', text: 'Blocked: the uploader or a copyright owner doesn\'t allow this video to play inside other apps, so it won\'t play in Emby.' };
            case 100:
                return { status: 'error', text: 'Video not found. It may be deleted or private.' };
            case 2:
                return { status: 'error', text: 'YouTube says the video id is invalid.' };
            case 5:
                return { status: 'unknown', text: 'The video couldn\'t play in this browser (YouTube player error 5). Try the Trailer button in Emby.' };
            case 153:
                return { status: 'unknown', text: 'The test player couldn\'t start (YouTube error 153). Try the Trailer button in Emby instead.' };
            default:
                return { status: 'unknown', text: 'YouTube player error ' + code + '. Try the Trailer button in Emby.' };
        }
    }

    var PLAYS_OK = { status: 'ok', text: 'It plays in an embedded player, so it should play in Emby.' };

    function makePlayerFrame(id, muted) {
        var iframe = document.createElement('iframe');
        iframe.src = 'https://www.youtube.com/embed/' + id + '?enablejsapi=1&autoplay=1&playsinline=1&rel=0' +
            (muted ? '&mute=1' : '') + '&origin=' + encodeURIComponent(window.location.origin);
        iframe.setAttribute('allow', 'autoplay; encrypted-media; picture-in-picture; fullscreen');
        iframe.setAttribute('allowfullscreen', '');
        iframe.setAttribute('referrerpolicy', 'strict-origin-when-cross-origin');
        return iframe;
    }

    function playHidden(id) {
        return loadYouTubeApi().then(function (YT) {
            return new Promise(function (resolve) {
                var holder = document.createElement('div');
                holder.setAttribute('aria-hidden', 'true');
                holder.style.cssText = 'position:fixed;right:0;bottom:0;width:200px;height:113px;opacity:0.01;pointer-events:none;overflow:hidden;';
                var iframe = makePlayerFrame(id, true);
                iframe.style.cssText = 'width:200px;height:113px;border:0;';
                holder.appendChild(iframe);
                document.body.appendChild(holder);

                var player = null;
                var done = false;
                var timer = null;
                function finish(result) {
                    if (done) { return; }
                    done = true;
                    clearTimeout(timer);
                    try { if (player && player.destroy) { player.destroy(); } } catch (e) { /* ignore */ }
                    if (holder.parentNode) { holder.parentNode.removeChild(holder); }
                    resolve(result);
                }

                player = new YT.Player(iframe, {
                    events: {
                        onReady: function (e) {
                            try { e.target.mute(); e.target.playVideo(); } catch (x) { /* ignore */ }
                        },
                        onStateChange: function (e) { if (e.data === 1) { finish(PLAYS_OK); } },
                        onError: function (e) { finish(playerError(e.data)); }
                    }
                });
                timer = setTimeout(function () {
                    finish({ status: 'unknown', text: 'The automatic playback test didn\'t finish. Press Test to be sure.' });
                }, 15000);
            });
        }, function () {
            return { status: 'unknown', text: 'Couldn\'t load the YouTube player for the automatic test. Press Test to be sure.' };
        });
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

            renderPlayback(s);
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
                loadStatus();
            }, function () {
                loading.hide();
                q('.tl-settingsMsg').textContent = 'Couldn\'t save the settings.';
            });
        });

        // ---- Browser playback tests (copyright blocks) ----
        // Works through the scanned YouTube links in a hidden, muted player while this page
        // is open and visible. Results go to the server, so it picks up where it left off.
        var runner = { running: false, enabled: false, state: '', unknownInARow: 0 };

        function setPlaybackText(text) { q('.tl-playback').textContent = text; }

        function renderPlayback(s) {
            runner.enabled = !!s.BrowserPlaybackTest;
            var box = q('.tl-playback');
            if (!runner.enabled) {
                box.textContent = 'Copyright-block test: off. Turn it on under Settings to test every link in this browser.';
                return;
            }
            if (!s.HasResults) {
                box.textContent = 'Copyright-block test: waiting for the first library scan.';
                return;
            }
            var progress = number(s.PlaybackDone) + ' of ' + number(s.PlaybackTotal) + ' links tested' +
                (s.PlaybackBlocked ? ', ' + number(s.PlaybackBlocked) + ' blocked' : '');
            var state = s.PlaybackDone >= s.PlaybackTotal ? 'all done' : (runner.state || 'starting…');
            box.textContent = 'Copyright-block test: ' + progress + ' · ' + state + '. Keep this page open and visible.';
            startRunner();
        }

        function wait(ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); }

        function startRunner() {
            if (runner.running || !runner.enabled || !self._active) { return; }
            runner.running = true;
            runner.state = 'running';

            function next() {
                if (!self._active || !runner.enabled) {
                    runner.running = false;
                    runner.state = '';
                    return Promise.resolve();
                }
                if (document.hidden) {
                    runner.state = 'paused while this tab is in the background';
                    return wait(5000).then(next);
                }
                return api('GET', 'TrailerEditor/Playback/Queue', { Limit: 10 }).then(function (queue) {
                    var items = queue.Items || [];
                    if (!items.length) {
                        runner.state = 'all done';
                        return wait(60000).then(next);
                    }
                    runner.state = 'running';
                    return items.reduce(function (chain, entry) {
                        return chain.then(function () {
                            if (!self._active || !runner.enabled || document.hidden) { return null; }
                            runner.state = 'testing "' + entry.ItemName + '"';
                            return playHidden(entry.VideoId).then(function (r) {
                                runner.unknownInARow = r.status === 'unknown' ? runner.unknownInARow + 1 : 0;
                                return api('POST', 'TrailerEditor/Playback/Result',
                                    { VideoId: entry.VideoId, Status: r.status, Message: r.text, Source: 'browser' })
                                    .then(function (s) { lastState = s; renderPlayback(s); }, function () { /* keep going */ });
                            });
                        });
                    }, Promise.resolve()).then(function () {
                        if (runner.unknownInARow >= 5) {
                            runner.state = 'the YouTube player isn\'t responding, retrying in a minute';
                            runner.unknownInARow = 0;
                            return wait(60000).then(next);
                        }
                        return next();
                    });
                }, function () {
                    runner.state = 'couldn\'t reach the server, retrying in a minute';
                    return wait(60000).then(next);
                });
            }

            next().catch(function () { runner.running = false; runner.state = ''; });
        }

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
