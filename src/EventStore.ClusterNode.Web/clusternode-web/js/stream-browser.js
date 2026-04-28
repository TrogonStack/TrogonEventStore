(function () {
	"use strict";

	var pollIntervalMs = 2000;
	var requestTimeoutMs = 8000;

	function initialize(root) {
		if (!root || root.dataset.streamBrowserInitialized === "true")
			return;

		root.dataset.streamBrowserInitialized = "true";
		var live = root.dataset.streamLive === "true";
		var paused = !live;
		var toggle = root.querySelector("[data-stream-toggle]");
		var status = root.querySelector("[data-stream-status]");
		var inFlight = false;

		if (!live)
			return;

		if (toggle) {
			toggle.addEventListener("click", function () {
				paused = !paused;
				toggle.textContent = paused ? "Resume" : "Pause";
				setStatus(status, paused ? "Live refresh paused." : "Refreshing the latest page every 2s.");
			});
		}

		root._streamBrowserInterval = window.setInterval(function () {
			if (!root.isConnected) {
				cleanup(root);
				return;
			}

			if (paused || inFlight)
				return;

			inFlight = true;
			refresh(root, status).finally(function () {
				inFlight = false;
			});
		}, pollIntervalMs);
	}

	async function refresh(root, status) {
		var controller = new AbortController();
		var timeout = window.setTimeout(function () {
			controller.abort();
		}, requestTimeoutMs);

		try {
			var response = await fetch(window.location.href, {
				cache: "no-store",
				headers: { "Accept": "text/html" },
				signal: controller.signal
			});

			if (!response.ok)
				throw new Error(response.status + " " + response.statusText);

			var html = await response.text();
			var parsed = new DOMParser().parseFromString(html, "text/html");
			var next = parsed.querySelector("[data-stream-events-fragment]");
			var current = root.querySelector("[data-stream-events-fragment]");
			if (!next || !current || !root.isConnected)
				return;

			current.replaceWith(next);
			setStatus(status, "Updated " + new Date().toLocaleTimeString() + ".");
		} catch (error) {
			setStatus(status, "Refresh unavailable: " + friendlyMessage(error));
		} finally {
			window.clearTimeout(timeout);
		}
	}

	function cleanup(root) {
		if (root._streamBrowserInterval) {
			window.clearInterval(root._streamBrowserInterval);
			root._streamBrowserInterval = null;
		}
	}

	function setStatus(status, message) {
		if (status)
			status.textContent = message;
	}

	function friendlyMessage(error) {
		if (error && error.name === "AbortError")
			return "request timed out";

		return error && error.message ? error.message : "request failed";
	}

	function start() {
		var roots = document.querySelectorAll("[data-stream-browser]");
		for (var i = 0; i < roots.length; i++)
			initialize(roots[i]);

		if (!window.MutationObserver || !document.body)
			return;

		var observer = new MutationObserver(function (mutations) {
			for (var i = 0; i < mutations.length; i++) {
				for (var j = 0; j < mutations[i].addedNodes.length; j++) {
					var node = mutations[i].addedNodes[j];
					if (node.nodeType !== 1)
						continue;

					if (node.matches && node.matches("[data-stream-browser]"))
						initialize(node);

					if (node.querySelectorAll) {
						var found = node.querySelectorAll("[data-stream-browser]");
						for (var k = 0; k < found.length; k++)
							initialize(found[k]);
					}
				}
			}
		});
		observer.observe(document.body, { childList: true, subtree: true });
	}

	if (document.readyState === "loading")
		document.addEventListener("DOMContentLoaded", start);
	else
		start();
})();
