// Search page: run an example query, and expand the clicked result in place.
(function () {
    'use strict';

    var form = document.querySelector('form.search');
    var input = document.getElementById('query');

    document.querySelectorAll('.js-example').forEach(function (chip) {
        chip.addEventListener('click', function () {
            input.value = chip.dataset.query;
            form.submit();
        });
    });

    var detail = document.getElementById('detail');

    if (!detail) {
        return;
    }

    var titleEl = document.getElementById('detail-title');
    var topicEl = document.getElementById('detail-topic');
    var ranksEl = document.getElementById('detail-ranks');
    var bodyEl = document.getElementById('detail-body');
    var current = null;
    var requestId = 0;

    function chip(kind, text) {
        var el = document.createElement('span');
        el.className = 'rank-chip';
        el.dataset.kind = kind;
        el.textContent = text;
        return el;
    }

    function rankChips(data) {
        var chips = [];

        if (data.semantic) {
            chips.push(chip('semantic', 'Semantic #' + data.semantic));
        }

        if (data.keyword) {
            chips.push(chip('keyword', 'Keyword #' + data.keyword));
        }

        if (data.hybrid) {
            chips.push(chip('hybrid', 'Hybrid #' + data.hybrid));
        }

        if (data.score) {
            chips.push(chip('score', 'RRF ' + data.score));
        }

        return chips;
    }

    function clearSelection() {
        document.querySelectorAll('.hit.is-selected').forEach(function (hit) {
            hit.classList.remove('is-selected');
        });
    }

    function close() {
        current = null;
        clearSelection();
        detail.hidden = true;
    }

    function open(button) {
        var id = button.dataset.articleId;

        if (current === id) {
            close();
            return;
        }

        current = id;
        var token = ++requestId;
        clearSelection();

        // The same article can sit in all three lists, so light up every copy of it.
        document.querySelectorAll('.hit[data-article-id="' + id + '"]').forEach(function (hit) {
            hit.classList.add('is-selected');
        });

        titleEl.textContent = button.dataset.title;
        topicEl.textContent = button.dataset.topic;
        topicEl.dataset.topic = button.dataset.topic;
        ranksEl.replaceChildren.apply(ranksEl, rankChips(button.dataset));
        bodyEl.innerHTML = '<p class="detail-loading">Loading…</p>';
        detail.hidden = false;
        detail.scrollIntoView({ behavior: 'smooth', block: 'nearest' });

        fetch(detail.dataset.detailsUrl + '/' + encodeURIComponent(id))
            .then(function (response) {
                if (!response.ok) {
                    throw new Error(response.status);
                }

                return response.text();
            })
            .then(function (html) {
                if (token === requestId) {
                    bodyEl.innerHTML = html;
                }
            })
            .catch(function () {
                if (token === requestId) {
                    bodyEl.innerHTML = '<p class="detail-loading">This article could not be loaded.</p>';
                }
            });
    }

    document.querySelectorAll('.js-hit').forEach(function (hit) {
        hit.addEventListener('click', function () {
            open(hit);
        });
    });

    document.querySelector('.js-detail-close').addEventListener('click', close);

    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape' && !detail.hidden) {
            close();
        }
    });
})();
