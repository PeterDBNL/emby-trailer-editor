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
                '<button is="emby-button" type="button" class="raised te-open"><span>Open</span></button>' +
                '<button is="emby-button" type="button" class="raised te-remove"><span>Remove</span></button>';
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
            input.value = url || '';
            input.addEventListener('input', updateThumb);
            updateThumb();

            row.querySelector('.te-remove').addEventListener('click', function () { row.parentNode.removeChild(row); });
            row.querySelector('.te-open').addEventListener('click', function () {
                var value = input.value.trim();
                var id = youTubeId(value);
                if (id) { value = 'https://www.youtube.com/watch?v=' + id; }
                else if (value && value.indexOf('://') === -1) { value = 'https://' + value; }
                if (value) { window.open(value, '_blank', 'noopener'); }
            });
            return input;
        }

        function openEditor(item) {
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

        function loadPinned() {
            api('GET', 'TrailerEditor/Pinned').then(function (items) {
                renderList(q('.te-pinned'), items, 'No pinned items yet.');
            }, function () {
                q('.te-pinned').innerHTML = '<p class="te-meta">Could not load pinned items.</p>';
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
            loading.show();
            api('POST', 'TrailerEditor/Items/' + encodeURIComponent(current.Id), { Urls: urls.join('\n'), Pin: q('.te-pin').checked })
                .then(function (item) {
                    loading.hide();
                    openEditor(item);
                    showMessage('Saved ' + item.TrailerUrls.length + ' trailer link(s) for ' + item.Name + '.', false);
                    loadPinned();
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
                loadPinned();
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
        q('.te-pinned').addEventListener('click', onListClick);

        q('.te-add').addEventListener('click', function () { addLinkRow('').focus(); });
        q('.te-save').addEventListener('click', save);
        q('.te-unpin').addEventListener('click', unpin);
        q('.te-cancel').addEventListener('click', closeEditor);

        q('.te-pinEnabled').addEventListener('change', function () {
            var checked = this.checked;
            loading.show();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                config.PinEditedTrailers = checked;
                return ApiClient.updatePluginConfiguration(pluginId, config);
            }).then(function () {
                loading.hide();
                showMessage(checked ? 'Pinned links will be restored after refreshes.' : 'Pinned links will no longer be restored.', false);
            }, function () {
                loading.hide();
                showMessage('Could not save the setting.', true);
            });
        });

        this.loadPinned = loadPinned;
        this.loadItem = loadItem;
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var view = this.view;

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            view.querySelector('.te-pinEnabled').checked = config.PinEditedTrailers !== false;
        });
        this.loadPinned();

        // Allow deep links such as .../configurationpage?name=trailereditor&id=12345
        var id = this.params && this.params.id;
        if (id) { this.loadItem(id); }
    };

    return View;
});
