(function () {
	"use strict";

	var pollIntervalMs = 2000;
	var requestTimeoutMs = 8000;

	function initialize(root) {
		if (!root)
			return;

		var live = root.dataset.streamLive === "true";
		if (!live) {
			cleanup(root);
			return;
		}

		if (root.dataset.streamBrowserInitialized === "true" && root._streamBrowserInterval)
			return;

		root.dataset.streamBrowserInitialized = "true";
		var state = root._streamBrowserState || { paused: false, inFlight: false, toggleBound: false };
		state.paused = false;
		state.inFlight = false;
		root._streamBrowserState = state;
		var toggle = root.querySelector("[data-stream-toggle]");
		var status = root.querySelector("[data-stream-status]");

		if (toggle && !state.toggleBound) {
			state.toggleBound = true;
			toggle.addEventListener("click", function () {
				var current = root._streamBrowserState;
				if (!current)
					return;

				current.paused = !current.paused;
				toggle.textContent = current.paused ? "Resume" : "Pause";
				setStatus(status, current.paused ? "Live refresh paused." : "Refreshing the latest page every 2s.");
			});
		}

		root._streamBrowserInterval = window.setInterval(function () {
			if (!root.isConnected) {
				cleanup(root);
				return;
			}

			if (state.paused || state.inFlight)
				return;

			state.inFlight = true;
			refresh(root, status).finally(function () {
				state.inFlight = false;
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

		delete root.dataset.streamBrowserInitialized;
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

				if (mutations[i].type === "attributes" && mutations[i].target.matches("[data-stream-browser]"))
					initialize(mutations[i].target);
			}
		});
		observer.observe(document.body, {
			childList: true,
			subtree: true,
			attributes: true,
			attributeFilter: ["data-stream-live"]
		});
	}

	if (document.readyState === "loading")
		document.addEventListener("DOMContentLoaded", start);
	else
		start();
})();
