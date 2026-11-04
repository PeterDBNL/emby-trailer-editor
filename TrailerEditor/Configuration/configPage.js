define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox'], function (BaseView, loading) {
    'use strict';

    var pluginId = '6f1d7a7e-3c1b-4f4e-9b8a-2d5c0e7a91b4';

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
                '<button is="emby-button" type="button" class="raised te-download" style="display:none;"><span>Download</span></button>' +
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
                // A link that won't play can be downloaded and saved with the media instead.
                row.querySelector('.te-download').style.display = status === 'error' && youTubeId(input.value.trim()) ? '' : 'none';
                if (status !== 'error' && row._closeDownload) { row._closeDownload(); }
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
            row.querySelector('.te-download').addEventListener('click', function () { toggleDownload(row); });
            row.querySelector('.te-open').addEventListener('click', function () {
                var value = input.value.trim();
                var id = youTubeId(value);
                if (id) { value = 'https://www.youtube.com/watch?v=' + id; }
                else if (value && value.indexOf('://') === -1) { value = 'https://' + value; }
                if (value) { window.open(value, '_blank', 'noopener'); }
            });
            return input;
        }


        // ---- Download a trailer that won't play ----

        var downloadDefaults = null;
        function removeLinkDefault() {
            if (downloadDefaults !== null) { return Promise.resolve(downloadDefaults); }
            try {
                return Promise.resolve(ApiClient.getPluginConfiguration(pluginId)).then(function (c) {
                    downloadDefaults = !c || c.DownloadRemoveLinkAfter !== false;
                    return downloadDefaults;
                }, function () { return true; });
            } catch (e) { return Promise.resolve(true); }
        }

        function toggleDownload(row) {
            if (row._dlPanel) { row._closeDownload(); return; }
            if (!current) { return; }
            var itemId = current.Id;
            var panel = document.createElement('div');
            panel.className = 'te-dl';
            panel.innerHTML =
                '<p>This link won\'t play in Emby. You can download the trailer and save it with the movie or show instead, so it plays from your server.</p>' +
                '<div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" class="te-dlRemove" checked />' +
                '<span>Remove this YouTube link after downloading</span></label></div>' +
                '<div class="te-dlStatus"></div>' +
                '<div class="te-dlButtons">' +
                '<button is="emby-button" type="button" class="raised te-dlInstall" style="display:none;"><span>Install yt-dlp</span></button>' +
                '<button is="emby-button" type="button" class="raised button-submit te-dlStart"><span>Start download</span></button>' +
                '<button is="emby-button" type="button" class="raised te-dlClose"><span>Close</span></button></div>' +
                '<p class="te-dlNotice te-meta" style="margin:.8em 0 0;">Downloading from YouTube may be against YouTube\'s terms, and trailers are copyrighted. ' +
                'Only download for your own personal use where that\'s allowed.</p>';
            row.appendChild(panel);
            row._dlPanel = panel;

            var status = panel.querySelector('.te-dlStatus');
            var startBtn = panel.querySelector('.te-dlStart');
            var installBtn = panel.querySelector('.te-dlInstall');
            var pollTimer = null;
            var closed = false;

            function setStatus(text, kind) {
                status.textContent = text || '';
                status.className = 'te-dlStatus' + (kind ? ' te-check-' + kind : '');
            }

            row._closeDownload = function () {
                closed = true;
                clearTimeout(pollTimer);
                if (panel.parentNode) { panel.parentNode.removeChild(panel); }
                row._dlPanel = null;
                row._closeDownload = null;
            };
            panel.querySelector('.te-dlClose').addEventListener('click', row._closeDownload);

            function poll() {
                if (closed) { return; }
                api('GET', 'TrailerEditor/Items/' + encodeURIComponent(itemId) + '/Download').then(function (job) {
                    if (closed) { return; }
                    if (job.State === 'running') {
                        var pct = Math.round(job.Percent || 0);
                        setStatus(job.Message && job.Message.indexOf('Downloading') !== 0 ? job.Message : 'Downloading… ' + pct + '%');
                        startBtn.disabled = true;
                        pollTimer = setTimeout(poll, 1500);
                    } else if (job.State === 'done') {
                        setStatus('✓ ' + job.Message, 'ok');
                        startBtn.style.display = 'none';
                        // The server already removed the link; take it out here too so Save doesn't put it back.
                        if (panel.querySelector('.te-dlRemove').checked && job.Url && row._removeAfter) { row._removeAfter(); }
                    } else if (job.State === 'failed') {
                        setStatus('✗ ' + job.Message, 'error');
                        startBtn.disabled = false;
                    }
                }, function () {
                    if (closed) { return; }
                    setStatus('Couldn\'t get the download progress. It may still be running on the server.', 'error');
                    startBtn.disabled = false;
                });
            }

            function checkInstalled() {
                setStatus('Checking for yt-dlp…');
                startBtn.disabled = true;
                return api('GET', 'TrailerEditor/Downloads/Status').then(function (s) {
                    if (closed) { return false; }
                    if (s.Installed) {
                        setStatus('');
                        installBtn.style.display = 'none';
                        startBtn.style.display = '';
                        startBtn.disabled = false;
                        return true;
                    }
                    setStatus('yt-dlp, the free tool that does the downloading, isn\'t installed on this server yet.' + (s.Error && s.Error.indexOf('isn\'t installed') < 0 ? ' ' + s.Error : ''), 'error');
                    installBtn.style.display = '';
                    startBtn.style.display = 'none';
                    return false;
                }, function (err) {
                    errorText(err, 'Couldn\'t check for yt-dlp.').then(function (t) { if (!closed) { setStatus(t, 'error'); } });
                    return false;
                });
            }

            function start() {
                var value = row.querySelector('.te-url').value.trim();
                startBtn.disabled = true;
                setStatus('Starting…');
                api('POST', 'TrailerEditor/Items/' + encodeURIComponent(itemId) + '/Download',
                    { Url: value, RemoveLink: panel.querySelector('.te-dlRemove').checked }).then(function () {
                    poll();
                }, function (err) {
                    errorText(err, 'Couldn\'t start the download.').then(function (t) { if (!closed) { setStatus('✗ ' + t, 'error'); startBtn.disabled = false; } });
                });
            }

            startBtn.addEventListener('click', start);
            installBtn.addEventListener('click', function () {
                installBtn.disabled = true;
                setStatus('Installing yt-dlp… this can take a minute.');
                api('POST', 'TrailerEditor/Downloads/InstallYtDlp').then(function (s) {
                    installBtn.disabled = false;
                    if (closed) { return; }
                    if (s.Installed) {
                        setStatus('yt-dlp ' + s.YtDlpVersion + ' installed. Starting the download…', 'ok');
                        installBtn.style.display = 'none';
                        startBtn.style.display = '';
                        start();
                    } else {
                        setStatus('✗ ' + (s.Error || 'yt-dlp was downloaded but can\'t be started.'), 'error');
                    }
                }, function (err) {
                    installBtn.disabled = false;
                    errorText(err, 'Couldn\'t install yt-dlp.').then(function (t) { if (!closed) { setStatus('✗ ' + t, 'error'); } });
                });
            });

            // The row removes itself after a successful download when the link was taken off the item.
            row._removeAfter = function () {
                setTimeout(function () {
                    if (!row.parentNode) { return; }
                    row.parentNode.removeChild(row);
                    if (!view.querySelector('.te-link')) { addLinkRow(''); }
                    showMessage('Downloaded the trailer and removed its YouTube link. Emby is refreshing the title so the trailer shows up.', false);
                }, 4000);
            };

            removeLinkDefault().then(function (def) {
                if (closed) { return; }
                panel.querySelector('.te-dlRemove').checked = def;
                // Resume a download that is already running for this title.
                api('GET', 'TrailerEditor/Items/' + encodeURIComponent(itemId) + '/Download').then(function (job) {
                    if (closed) { return; }
                    if (job && job.State === 'running') { startBtn.disabled = true; poll(); } else { checkInstalled(); }
                }, function () { checkInstalled(); });
            });
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
        }

        function closeEditor() {
            stopTest();
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
