define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox'], function (BaseView, loading) {
    'use strict';

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function youTubeId(url) {
        var m = /^[A-Za-z0-9_-]{11}$/.exec(url) ||
            /(?:youtube(?:-nocookie)?\.com\/(?:watch\?(?:.*&)?v=|embed\/|shorts\/|v\/|live\/)|youtu\.be\/)([A-Za-z0-9_-]{11})/.exec(url);
        return m ? (m[1] || m[0]) : null;
    }

    function describe(item) {
        var parts = [];
        if (item.Year) { parts.push(item.Year); }
        parts.push(item.Type === 'Series' ? 'TV show' : item.Type);
        var count = (item.TrailerUrls || []).length;
        parts.push(count === 1 ? '1 trailer' : count + ' trailers');
        return parts.join(' · ');
    }

    function formatViews(n) {
        if (n == null || n < 0) { return ''; }
        if (n >= 1e9) { return (n / 1e9).toFixed(1).replace(/\.0$/, '') + 'B views'; }
        if (n >= 1e6) { return (n / 1e6).toFixed(1).replace(/\.0$/, '') + 'M views'; }
        if (n >= 1e3) { return Math.round(n / 1e3) + 'K views'; }
        return n + ' views';
    }

    function formatDate(value) {
        var d = new Date(value);
        return isNaN(d.getTime()) ? '' : d.toLocaleDateString();
    }

    function errorText(err, fallback) {
        if (err && typeof err.text === 'function') {
            return err.text().then(function (t) { return t || fallback; }, function () { return fallback; });
        }
        return Promise.resolve(fallback);
    }

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

    // Playback results per video id, kept for an hour while the dashboard is open.
    var playResults = {};
    function rememberResult(id, result) {
        if (result.status === 'unknown') { return; }
        playResults[id] = { time: Date.now(), result: result };

        // Share the result with the library scan, so this video isn't tested again there.
        try {
            var url = ApiClient.getUrl('TrailerEditor/Playback/Result') + '?VideoId=' + encodeURIComponent(id) +
                '&Status=' + encodeURIComponent(result.status) + '&Message=' + encodeURIComponent(result.text) + '&Source=editor';
            ApiClient.ajax({ type: 'POST', url: url, dataType: 'json' }).catch(function () { /* not important */ });
        } catch (e) { /* not important */ }
    }
    function knownResult(id) {
        var entry = playResults[id];
        return entry && Date.now() - entry.time < 3600000 ? entry.result : null;
    }

    // Plays the video muted in a tiny, invisible embedded player and reports
    // whether YouTube allowed it. Tests run one at a time.
    var autoQueue = Promise.resolve();
    function autoTest(id) {
        var known = knownResult(id);
        if (known) { return Promise.resolve(known); }
        var run = autoQueue.then(function () {
            return knownResult(id) || playHidden(id);
        });
        autoQueue = run.catch(function () { /* keep the queue going */ });
        return run;
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
                    rememberResult(id, result);
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

    // Opens another plugin page through Emby's own router (the same way Emby links to plugin pages).
    // An item id is also left in sessionStorage, so the editor finds it however the page opens.
    function goToPage(name, id) {
        try {
            if (id) { sessionStorage.setItem('trailerEditorOpenId', String(id)); }
        } catch (e) { /* storage may be unavailable */ }
        var path = 'configurationpage?name=' + encodeURIComponent(name) + (id ? '&id=' + encodeURIComponent(id) : '');
        if (window.Dashboard && typeof window.Dashboard.navigate === 'function') {
            window.Dashboard.navigate(path);
        } else {
            window.location.hash = '#!/' + path;
        }
    }

    function View(view, params) {
        BaseView.apply(this, arguments);

        var current = null;

        function q(selector) { return view.querySelector(selector); }

        function showMessage(text, isError) {
            var el = q('.te-msg');
            el.textContent = text || '';
            el.className = 'te-msg ' + (text ? (isError ? 'err' : 'ok') : '');
        }

        // Everything goes in the URL. Emby can consume a plugin's request body
        // before the plugin sees it, so no request body is sent.
        function api(method, path, query) {
            var url = ApiClient.getUrl(path);
            if (query) {
                url += (url.indexOf('?') === -1 ? '?' : '&') + Object.keys(query).map(function (k) {
                    return encodeURIComponent(k) + '=' + encodeURIComponent(query[k]);
                }).join('&');
            }
            return ApiClient.ajax({ type: method, url: url, dataType: 'json' });
        }

        function renderList(container, items, emptyText) {
            if (!items.length) {
                container.innerHTML = '<p class="te-meta">' + escapeHtml(emptyText) + '</p>';
                return;
            }
            container.innerHTML = items.map(function (item) {
                return '<div class="te-result" data-id="' + escapeHtml(item.Id) + '">' +
                    '<div><div>' + escapeHtml(item.Name) + '</div>' +
                    '<div class="te-meta">' + escapeHtml(describe(item)) + '</div></div>' +
                    (item.IsPinned ? '<span class="te-badge">Pinned</span>' : '') +
                    '</div>';
            }).join('');
        }

        function addLinkRow(url) {
            var row = document.createElement('div');
            row.className = 'te-row te-link';
            row.innerHTML =
                '<img class="te-thumb" alt="" />' +
                '<div class="inputContainer"><input is="emby-input" type="text" class="te-url" label="Trailer link" /></div>' +
                '<button is="emby-button" type="button" class="raised te-test"><span>Test</span></button>' +
                '<button is="emby-button" type="button" class="raised te-open"><span>Open</span></button>' +
                '<button is="emby-button" type="button" class="raised te-remove"><span>Remove</span></button>' +
                '<div class="te-check"></div>';
            q('.te-links').appendChild(row);

            var input = row.querySelector('.te-url');
            var thumb = row.querySelector('.te-thumb');
            function updateThumb() {
                var id = youTubeId(input.value.trim());
                if (id) {
                    thumb.src = 'https://img.youtube.com/vi/' + id + '/mqdefault.jpg';
                    thumb.style.visibility = 'visible';
                } else {
                    thumb.removeAttribute('src');
                    thumb.style.visibility = 'hidden';
                }
            }
            var check = row.querySelector('.te-check');
            var checkTimer = null;
            var checkSeq = 0;

            function setCheck(status, text) {
                row.setAttribute('data-check', status || '');
                check.className = 'te-check' + (status ? ' te-check-' + status : '');
                check.textContent = text || '';
            }

            function runCheck() {
                var value = input.value.trim();
                var seq = ++checkSeq;
                if (!value) { setCheck('', ''); return; }
                setCheck('pending', 'Checking whether this plays in Emby…');
                var id = youTubeId(value);
                var title = '';

                // Step 2: play it in an invisible embedded player, like Emby does.
                // This also catches copyright blocks, which the server can't see.
                function playbackTest(serverStatus, serverText) {
                    if (!id || serverStatus === 'error') { return; }
                    setCheck('pending', serverText + ' Testing playback…');
                    autoTest(id).then(function (r) {
                        if (seq !== checkSeq) { return; }
                        if (r.status === 'unknown') {
                            setCheck(serverStatus, serverText + ' ' + r.text);
                        } else {
                            setCheck(r.status, (r.status === 'ok' ? '✓ ' : '✗ ') + title + r.text);
                        }
                    });
                }

                // Step 1: the server asks YouTube about the video.
                api('GET', 'TrailerEditor/Check', { Url: value }).then(function (r) {
                    if (seq !== checkSeq) { return; }
                    title = r.Title ? '"' + r.Title + '"' + (r.Channel ? ' by ' + r.Channel : '') + ': ' : '';
                    var icon = r.Status === 'ok' ? '✓ ' : r.Status === 'error' ? '✗ ' : '? ';
                    var text = icon + title + (r.Message || '');
                    setCheck(r.Status, text);
                    playbackTest(r.Status, text);
                }, function () {
                    if (seq !== checkSeq) { return; }
                    setCheck('unknown', '? Couldn\'t check this link.');
                    playbackTest('unknown', '?');
                });
            }

            input.value = url || '';
            input.addEventListener('input', function () {
                updateThumb();
                clearTimeout(checkTimer);
                setCheck('', '');
                checkTimer = setTimeout(runCheck, 700);
            });
            updateThumb();
            runCheck();

            row._setCheck = setCheck;
            row.querySelector('.te-remove').addEventListener('click', function () {
                if (testRow === row) { stopTest(); }
                row.parentNode.removeChild(row);
            });
            row.querySelector('.te-test').addEventListener('click', function () { runTest(row); });
            row.querySelector('.te-open').addEventListener('click', function () {
                var value = input.value.trim();
                var id = youTubeId(value);
                if (id) { value = 'https://www.youtube.com/watch?v=' + id; }
                else if (value && value.indexOf('://') === -1) { value = 'https://' + value; }
                if (value) { window.open(value, '_blank', 'noopener'); }
            });
            return input;
        }

        var testSeq = 0;
        var testPlayer = null;
        var testTimer = null;
        var testRow = null;

        function stopTest() {
            testSeq++;
            clearTimeout(testTimer);
            if (testPlayer && typeof testPlayer.destroy === 'function') {
                try { testPlayer.destroy(); } catch (e) { /* ignore */ }
            }
            testPlayer = null;
            testRow = null;
            q('.te-testFrame').innerHTML = '';
            q('.te-testPanel').style.display = 'none';
        }

        // Plays the link in YouTube's embedded player on this page. Emby's own
        // trailer player is an embedded player too, so it has the same restrictions.
        function runTest(row) {
            var value = row.querySelector('.te-url').value.trim();
            var id = youTubeId(value);
            stopTest();
            if (!id) {
                row._setCheck('unknown', '? Only YouTube links can be tested here. Use Open to check other links.');
                return;
            }

            var seq = testSeq;
            testRow = row;
            var panel = q('.te-testPanel');
            var result = q('.te-testResult');
            q('.te-testTitle').textContent = 'Testing ' + value;
            result.className = 'te-testResult';
            result.textContent = 'Loading the YouTube player…';
            panel.style.display = '';

            var iframe = makePlayerFrame(id, false);
            iframe.className = 'te-testIframe';
            q('.te-testFrame').appendChild(iframe);
            panel.scrollIntoView({ behavior: 'smooth', block: 'nearest' });

            function finish(status, text) {
                if (seq !== testSeq) { return; }
                rememberResult(id, { status: status, text: text });
                clearTimeout(testTimer);
                result.className = 'te-testResult te-check-' + status;
                result.textContent = text;
                var icon = status === 'ok' ? '✓ ' : status === 'error' ? '✗ ' : '? ';
                row._setCheck(status, icon + 'Test: ' + text);
            }

            loadYouTubeApi().then(function (YT) {
                if (seq !== testSeq) { return; }
                testPlayer = new YT.Player(iframe, {
                    events: {
                        onReady: function (e) { try { e.target.playVideo(); } catch (x) { /* autoplay may be blocked */ } },
                        onStateChange: function (e) {
                            if (e.data === 1) { // playing
                                finish(PLAYS_OK.status, PLAYS_OK.text);
                            }
                        },
                        onError: function (e) {
                            var r = playerError(e.data);
                            finish(r.status, r.text);
                        }
                    }
                });
                testTimer = setTimeout(function () {
                    if (seq === testSeq) {
                        result.textContent = 'Press play in the player above. If it plays, it will play in Emby.';
                    }
                }, 8000);
            }, function () {
                if (seq !== testSeq) { return; }
                result.textContent = 'Couldn\'t load YouTube\'s player controls. Watch the player above: if the video plays there, it will play in Emby.';
            });
        }

        function openEditor(item) {
            stopTest();
            current = item;
            showMessage('');
            q('.te-editorTitle').textContent = item.Name + (item.Year ? ' (' + item.Year + ')' : '');
            q('.te-links').innerHTML = '';
            (item.TrailerUrls || []).forEach(function (url) { addLinkRow(url); });
            if (!(item.TrailerUrls || []).length) { addLinkRow(''); }
            q('.te-pin').checked = true;
            q('.te-unpin').style.display = item.IsPinned ? '' : 'none';
            q('.te-editor').style.display = '';
            q('.te-editor').scrollIntoView({ behavior: 'smooth', block: 'start' });
            closeFind();
            loadRejected();
        }

        // ---- Find trailer ----

        var findSeq = 0;
        var lastFind = null;

        function closeFind() {
            findSeq++;
            lastFind = null;
            q('.te-findPanel').style.display = 'none';
            q('.te-candidates').innerHTML = '';
            q('.te-useBest').style.display = 'none';
            q('.te-find').disabled = false;
        }

        function renderCandidates(r) {
            var list = r.Candidates || [];
            q('.te-findMsg').textContent = r.Message || '';
            q('.te-useBest').style.display = r.Found && r.Chosen ? '' : 'none';
            q('.te-candidates').innerHTML = list.map(function (c, i) {
                var meta = [c.Channel, c.LengthText, formatViews(c.Views), c.Source === 'tmdb' ? 'from TMDb' : 'from YouTube search']
                    .filter(function (p) { return p; }).join(' · ');
                var check = c.Status === 'ok' ? '✓ checked: ' : c.Status === 'unknown' ? '? could not fully check: ' : '';
                return '<div class="te-cand' + (i === 0 && r.Found ? ' te-candBest' : '') + '" data-id="' + escapeHtml(c.VideoId) + '">' +
                    '<img class="te-thumb" alt="" src="https://img.youtube.com/vi/' + encodeURIComponent(c.VideoId) + '/mqdefault.jpg" />' +
                    '<div><div class="te-candTitle">' + escapeHtml(c.Title || c.Url) + '</div>' +
                    '<div class="te-meta">' + escapeHtml(meta) + '</div>' +
                    '<div class="te-reasons">' + escapeHtml(check + 'score ' + Math.round(c.Score) + (c.Reasons && c.Reasons.length ? ' (' + c.Reasons.join(', ') + ')' : '')) + '</div></div>' +
                    '<button is="emby-button" type="button" class="raised te-use" data-url="' + escapeHtml(c.Url) + '"><span>Use this</span></button>' +
                    '</div>';
            }).join('');
        }

        function findTrailer() {
            if (!current) { return; }
            var seq = ++findSeq;
            var button = q('.te-find');
            button.disabled = true;
            q('.te-findPanel').style.display = '';
            q('.te-candidates').innerHTML = '';
            q('.te-useBest').style.display = 'none';
            q('.te-findMsg').textContent = 'Searching for the best trailer… this can take up to a minute.';
            api('POST', 'TrailerEditor/Items/' + encodeURIComponent(current.Id) + '/Find', { Apply: false }).then(function (r) {
                if (seq !== findSeq) { return; }
                button.disabled = false;
                lastFind = r;
                renderCandidates(r);
            }, function (err) {
                if (seq !== findSeq) { return; }
                button.disabled = false;
                errorText(err, 'The search failed.').then(function (t) { q('.te-findMsg').textContent = t; });
            });
        }

        // Puts a link at the top of the list (replacing a row that has the same video); the person presses Save.
        function useLink(url) {
            var id = youTubeId(url);
            Array.prototype.slice.call(view.querySelectorAll('.te-link')).forEach(function (row) {
                var value = row.querySelector('.te-url').value.trim();
                if (!value || (id && youTubeId(value) === id)) {
                    if (testRow === row) { stopTest(); }
                    row.parentNode.removeChild(row);
                }
            });
            var input = addLinkRow(url);
            var row = input.closest('.te-link');
            var links = q('.te-links');
            links.insertBefore(row, links.firstChild);
        }

        function useCandidate(url, thenSave) {
            useLink(url);
            if (thenSave) {
                save();
            } else {
                showMessage('Added the trailer at the top of the list. Press Save to keep it.', false);
            }
        }

        function deleteTrailers() {
            if (!current) { return; }
            if (!window.confirm('Delete all trailer links of "' + current.Name + '"?\n\nThey will not be picked again by Find trailer, and refreshes will not bring them back.')) { return; }
            var id = current.Id;
            loading.show();
            api('POST', 'TrailerEditor/Items/' + encodeURIComponent(id) + '/DeleteTrailers').then(function (r) {
                return api('GET', 'TrailerEditor/Items/' + encodeURIComponent(id)).then(function (item) {
                    loading.hide();
                    openEditor(item);
                    showMessage(r.Message || 'Trailers deleted.', !r.Ok);
                });
            }).then(null, function (err) {
                loading.hide();
                errorText(err, 'Deleting the trailers failed.').then(function (t) { showMessage(t, true); });
            });
        }

        // ---- Rejected links ----

        function loadRejected() {
            var item = current;
            if (!item) { return; }
            api('GET', 'TrailerEditor/Items/' + encodeURIComponent(item.Id) + '/Rejected').then(function (page) {
                if (current !== item) { return; }
                renderRejected(page.Items || []);
            }, function () {
                q('.te-rejectedList').innerHTML = '<p class="te-meta">Couldn\'t load the rejected links.</p>';
            });
        }

        function renderRejected(items) {
            q('.te-rejectedCount').textContent = '(' + items.length + ')';
            q('.te-rejectedList').innerHTML = items.length ? items.map(function (l) {
                return '<div class="te-rej" data-id="' + escapeHtml(l.VideoId) + '"><div>' + escapeHtml(l.Url) +
                    '<div class="te-meta">' + escapeHtml(l.Reason || '') + (l.RejectedUtc ? ' · ' + escapeHtml(formatDate(l.RejectedUtc)) : '') + '</div></div>' +
                    '<button is="emby-button" type="button" class="raised te-allow" data-id="' + escapeHtml(l.VideoId) + '"><span>Allow again</span></button></div>';
            }).join('') : '<p class="te-meta">Nothing rejected for this title.</p>';
        }

        function allowAgain(videoId) {
            if (!current) { return; }
            var item = current;
            api('POST', 'TrailerEditor/Items/' + encodeURIComponent(item.Id) + '/Rejected/Allow', { VideoId: videoId }).then(function (page) {
                if (current !== item) { return; }
                renderRejected(page.Items || []);
            }, function (err) {
                errorText(err, 'Couldn\'t allow that link again.').then(function (t) { showMessage(t, true); });
            });
        }

        function closeEditor() {
            stopTest();
            closeFind();
            current = null;
            q('.te-editor').style.display = 'none';
        }

        function loadItem(id) {
            loading.show();
            api('GET', 'TrailerEditor/Items/' + encodeURIComponent(id)).then(function (item) {
                loading.hide();
                openEditor(item);
            }, function (err) {
                loading.hide();
                errorText(err, 'Could not load that item.').then(function (t) { showMessage(t, true); });
            });
        }

        function search(term) {
            if (!term) { q('.te-results').innerHTML = ''; return; }
            loading.show();
            api('GET', 'TrailerEditor/Search', { SearchTerm: term, Limit: 30 }).then(function (items) {
                loading.hide();
                renderList(q('.te-results'), items, 'No movies or TV shows found.');
            }, function (err) {
                loading.hide();
                errorText(err, 'Search failed.').then(function (t) { showMessage(t, true); });
            });
        }

        function save() {
            if (!current) { return; }
            var urls = Array.prototype.map.call(view.querySelectorAll('.te-url'), function (i) { return i.value.trim(); })
                .filter(function (v) { return v; });
            var bad = Array.prototype.filter.call(view.querySelectorAll('.te-link'), function (row) {
                return row.getAttribute('data-check') === 'error' && row.querySelector('.te-url').value.trim();
            }).length;
            if (bad && !window.confirm(bad === 1
                ? 'One link probably won\'t play in Emby (see the note under it). Save anyway?'
                : bad + ' links probably won\'t play in Emby (see the notes under them). Save anyway?')) {
                return;
            }
            loading.show();
            var query = urls.length ? { Urls: urls.join(' ') } : { Clear: true };
            query.Pin = q('.te-pin').checked;
            api('POST', 'TrailerEditor/Items/' + encodeURIComponent(current.Id), query)
                .then(function (item) {
                    loading.hide();
                    openEditor(item);
                    showMessage('Saved ' + item.TrailerUrls.length + ' trailer link(s) for ' + item.Name + '.', false);
                }, function (err) {
                    loading.hide();
                    errorText(err, 'Saving failed. Check the links and try again.').then(function (t) { showMessage(t, true); });
                });
        }

        function unpin() {
            if (!current) { return; }
            loading.show();
            api('DELETE', 'TrailerEditor/Items/' + encodeURIComponent(current.Id) + '/Pin').then(function (item) {
                loading.hide();
                openEditor(item);
                showMessage('Unpinned ' + item.Name + '. Its links stay as they are, but a metadata refresh may now replace them.', false);
            }, function (err) {
                loading.hide();
                errorText(err, 'Unpin failed.').then(function (t) { showMessage(t, true); });
            });
        }

        q('.te-searchForm').addEventListener('submit', function (e) {
            e.preventDefault();
            search(q('.te-searchTerm').value.trim());
            return false;
        });

        function onListClick(e) {
            var row = e.target.closest('.te-result');
            if (row) { loadItem(row.getAttribute('data-id')); }
        }
        q('.te-results').addEventListener('click', onListClick);

        q('.te-searchYt').addEventListener('click', function () {
            if (!current) { return; }
            var query = [current.Name, current.Year, 'trailer'].filter(function (p) { return p; }).join(' ');
            window.open('https://www.youtube.com/results?search_query=' + encodeURIComponent(query), '_blank', 'noopener');
        });
        q('.te-find').addEventListener('click', findTrailer);
        q('.te-findClose').addEventListener('click', closeFind);
        q('.te-deleteAll').addEventListener('click', deleteTrailers);
        q('.te-useBest').addEventListener('click', function () {
            if (lastFind && lastFind.Chosen) { useCandidate(lastFind.Chosen.Url, true); }
        });
        q('.te-candidates').addEventListener('click', function (e) {
            var button = e.target.closest('.te-use');
            if (button) { useCandidate(button.getAttribute('data-url'), false); }
        });
        q('.te-rejectedList').addEventListener('click', function (e) {
            var button = e.target.closest('.te-allow');
            if (button) { allowAgain(button.getAttribute('data-id')); }
        });
        q('.te-add').addEventListener('click', function () { addLinkRow('').focus(); });
        q('.te-save').addEventListener('click', save);
        q('.te-unpin').addEventListener('click', unpin);
        q('.te-cancel').addEventListener('click', closeEditor);

        q('.te-goLibrary').addEventListener('click', function () { goToPage('trailereditorlibrary'); });

        this.loadItem = loadItem;
        this.stopTest = stopTest;

        view.querySelector('.te-testClose').addEventListener('click', stopTest);
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onPause = function () {
        if (BaseView.prototype.onPause) { BaseView.prototype.onPause.apply(this, arguments); }
        this.stopTest();
    };

    // The item to open: one handed over by the Trailer Library page, or ?id= in the address.
    function idFromAddress(params) {
        try {
            var pending = sessionStorage.getItem('trailerEditorOpenId');
            if (pending) {
                sessionStorage.removeItem('trailerEditorOpenId');
                return pending;
            }
        } catch (e) { /* storage may be unavailable */ }
        var m = /[?&]id=([^&#]+)/.exec(window.location.hash || '') || /[?&]id=([^&#]+)/.exec(window.location.search || '');
        if (m) { return decodeURIComponent(m[1]); }
        return params && params.id ? params.id : null;
    }

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var id = idFromAddress(this.params);
        if (id) { this.loadItem(id); }
    };

    return View;
});
