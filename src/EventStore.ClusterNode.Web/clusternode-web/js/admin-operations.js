(function () {
	"use strict";

	var commandInFlight = 0;
	var commandRefreshScheduled = false;

	function readForm(form) {
		var payload = {};
		var data = new FormData(form);
		data.forEach(function (value, key) {
			if (value === "")
				return;
			if (value === "on") {
				payload[key] = true;
				return;
			}

			var number = Number(value);
			payload[key] = Number.isFinite(number) && String(number) === String(value)
				? number
				: value;
		});

		form.querySelectorAll('input[type="checkbox"]').forEach(function (input) {
			payload[input.name] = input.checked;
		});

		return payload;
	}

	function statusRoot(form) {
		var scoped = form.closest("[data-admin-command-scope]");
		return document.querySelector("[data-admin-command-status]") ||
			(scoped ? scoped.querySelector("[data-admin-command-status]") : null);
	}

	function showStatus(form, message, success) {
		var root = statusRoot(form);
		if (!root)
			return;

		root.hidden = false;
		root.textContent = message;
		root.className = success
			? "mt-5 rounded-2xl border border-es-green/25 bg-es-green/10 px-4 py-3 text-sm font-bold text-es-forest"
			: "mt-5 rounded-2xl border border-red-200 bg-red-50 px-4 py-3 text-sm font-bold text-red-800";
	}

	async function submitCommand(event) {
		var form = event.target.closest("[data-admin-command]");
		if (!form)
			return;

		event.preventDefault();

		var confirmation = form.getAttribute("data-confirm");
		if (confirmation && !window.confirm(confirmation))
			return;

		var button = event.submitter || form.querySelector("button[type='submit']");
		if (button)
			button.disabled = true;

		commandInFlight += 1;
		try {
			showStatus(form, "Command in progress...", true);
			var response = await fetch(form.action, {
				method: "POST",
				credentials: "same-origin",
				headers: {
					"Accept": "application/json",
					"Content-Type": "application/json"
				},
				body: JSON.stringify(readForm(form))
			});
			var result = await response.json().catch(function () {
				return { success: false, message: response.status + " " + response.statusText };
			});
			var success = response.ok && result.success !== false;
			showStatus(form, result.message || response.statusText, success);

			if (success && form.getAttribute("data-admin-no-refresh") !== "true") {
				commandRefreshScheduled = true;
				window.setTimeout(function () { window.location.reload(); }, 900);
			}
		} catch (error) {
			showStatus(form, error && error.message ? error.message : "Command failed.", false);
		} finally {
			commandInFlight -= 1;
			if (button)
				button.disabled = false;
		}
	}

	function isEditing() {
		var element = document.activeElement;
		return !!element && (
			element.isContentEditable ||
			element.matches("input, select, textarea"));
	}

	function canAutoRefresh() {
		return !document.hidden &&
			commandInFlight === 0 &&
			!commandRefreshScheduled &&
			!isEditing();
	}

	function initializeAutoRefresh(root) {
		if (!root || root._adminAutoRefreshInterval)
			return;

		var interval = Number(root.getAttribute("data-admin-auto-refresh")) || 2000;
		root._adminAutoRefreshInterval = window.setInterval(function () {
			if (!root.isConnected) {
				window.clearInterval(root._adminAutoRefreshInterval);
				root._adminAutoRefreshInterval = null;
				return;
			}

			if (canAutoRefresh())
				window.location.reload();
		}, interval);
	}

	function startAutoRefresh(root) {
		(root || document).querySelectorAll("[data-admin-auto-refresh]").forEach(initializeAutoRefresh);
	}

	document.addEventListener("submit", submitCommand);
	startAutoRefresh();
	if (window.MutationObserver) {
		new MutationObserver(function (mutations) {
			mutations.forEach(function (mutation) {
				mutation.addedNodes.forEach(function (node) {
					if (node.nodeType !== Node.ELEMENT_NODE)
						return;

					if (node.matches("[data-admin-auto-refresh]"))
						initializeAutoRefresh(node);

					startAutoRefresh(node);
				});
			});
		}).observe(document.body, { childList: true, subtree: true });
	}
})();
