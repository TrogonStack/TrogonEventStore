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
		}
	];

	var legacyNavLinks = [
		{ selector: 'a[ui-sref="dashboard.list"]', text: "Dashboard" },
		{ selector: 'a[ui-sref="clusterstatus.list"]', text: "Cluster Status" }
	];

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
