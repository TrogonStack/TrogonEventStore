(function () {
	"use strict";

	var legacyRoutes = [
		{
			pattern: /^#\/dashboard(?:\/snapshot)?(?:[/?].*)?$/i,
			target: function (hash) {
				return hash.toLowerCase().indexOf("/snapshot") >= 0
					? "/ui/observability#dashboard-snapshot"
					: "/ui/observability";
			}
		},
		{
			pattern: /^#\/clusterstatus(?:\/snapshot)?(?:[/?].*)?$/i,
			target: function (hash) {
				return hash.toLowerCase().indexOf("/snapshot") >= 0
					? "/ui/cluster#cluster-snapshot"
					: "/ui/cluster#cluster-status";
			}
		},
		{
			pattern: /^#\/admin(?:[/?].*)?$/i,
			target: function () {
				return "/ui/operations";
			}
		},
		{
			pattern: /^#\/subscriptions(?:[/?].*)?$/i,
			target: function (hash) {
				var path = hash.replace(/^#\/subscriptions\/?/i, "").split(/[?#]/)[0].replace(/\/$/, "");
				if (!path)
					return "/ui/subscriptions";

				var parts = path.split("/");
				if (parts[0].toLowerCase() === "new")
					return "/ui/subscriptions/new";
				if (parts.length < 2)
					return "/ui/subscriptions";

				var streamId = safeDecode(parts[0]);
				var groupName = safeDecode(parts[1]);
				var action = (parts[2] || "").toLowerCase();
				var target = "/ui/subscriptions/" + encodeURIComponent(streamId) + "/" + encodeURIComponent(groupName);
				if (action === "viewparkedmessages")
					return target + "/parked";

				return isSubscriptionAction(action) ? target + "/" + action : target;
			}
		},
		{
			pattern: /^#\/scavenge\/([^/?#]+)(?:\/)?(?:[?](.*))?$/i,
			target: function (hash) {
				var match = /^#\/scavenge\/([^/?#]+)(?:\/)?(?:[?](.*))?$/i.exec(hash);
				if (!match)
					return "/ui/operations";

				var scavengeId = safeDecode(match[1]);
				var target = "/ui/operations/scavenges/" + encodeURIComponent(scavengeId);
				var source = new URLSearchParams(match[2] || "");
				var destination = new URLSearchParams();
				if (source.has("page"))
					destination.set("page", source.get("page"));
				if (source.has("from"))
					destination.set("from", source.get("from"));

				var query = destination.toString();
				return query ? target + "?" + query : target;
			}
		},
		{
			pattern: /^#\/users(?:[/?].*)?$/i,
			target: function (hash) {
				var path = hash.replace(/^#\/users\/?/i, "").split(/[?#]/)[0].replace(/\/$/, "");
				if (!path)
					return "/ui/users";

				var parts = path.split("/");
				if (parts[0].toLowerCase() === "new")
					return "/ui/users/new";

				var loginName = safeDecode(parts[0]);
				var action = (parts[1] || "").toLowerCase();
				var target = "/ui/users/" + encodeURIComponent(loginName);
				return isUserAction(action) ? target + "/" + action : target;
			}
		}
	];

	var legacyNavLinks = [
		{ selector: 'a[ui-sref="dashboard.list"]', text: "Dashboard" },
		{ selector: 'a[ui-sref="clusterstatus.list"]', text: "Cluster Status" },
		{ selector: 'a[ui-sref="admin"]', text: "Admin" },
		{ selector: 'a[ui-sref="users.list"]', text: "Users" },
		{ selector: 'a[ui-sref="subscriptions.list"]', text: "Persistent Subscriptions" }
	];

	function safeDecode(value) {
		try {
			return decodeURIComponent(value);
		} catch (_) {
			return value;
		}
	}

	function isUserAction(action) {
		return action === "edit" ||
			action === "enable" ||
			action === "disable" ||
			action === "delete" ||
			action === "reset";
	}

	function isSubscriptionAction(action) {
		return action === "edit" ||
			action === "delete" ||
			action === "parked";
	}

	function removeLegacyLinks() {
		for (var group = 0; group < legacyNavLinks.length; group++) {
			var links = document.querySelectorAll(legacyNavLinks[group].selector);
			for (var i = 0; i < links.length; i++) {
				if (links[i].textContent.trim() !== legacyNavLinks[group].text)
					continue;

				var item = links[i].closest("li");
				if (item)
					item.remove();
			}
		}
	}

	function redirectLegacyRoutes() {
		var hash = window.location.hash || "";
		for (var i = 0; i < legacyRoutes.length; i++) {
			if (!legacyRoutes[i].pattern.test(hash))
				continue;

			window.location.replace(legacyRoutes[i].target(hash));
			return;
		}
	}

	function watchLegacyShell() {
		removeLegacyLinks();
		if (!window.MutationObserver || !document.body)
			return;

		var observer = new MutationObserver(removeLegacyLinks);
		observer.observe(document.body, { childList: true, subtree: true });
	}

	if (document.readyState === "loading")
		document.addEventListener("DOMContentLoaded", watchLegacyShell);
	else
		watchLegacyShell();

	window.addEventListener("hashchange", redirectLegacyRoutes);
	redirectLegacyRoutes();
})();
