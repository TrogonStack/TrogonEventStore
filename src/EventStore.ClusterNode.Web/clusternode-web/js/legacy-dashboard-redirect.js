(function () {
	"use strict";

	var legacyDashboardRoute = /^#\/dashboard(?:\/snapshot)?(?:[/?].*)?$/i;

	function removeLegacyDashboardLink() {
		var links = document.querySelectorAll('a[ui-sref="dashboard.list"]');
		for (var i = 0; i < links.length; i++) {
			if (links[i].textContent.trim() !== "Dashboard")
				continue;

			var item = links[i].closest("li");
			if (item)
				item.remove();
		}
	}

	function redirectLegacyDashboard() {
		var hash = window.location.hash || "";
		if (!legacyDashboardRoute.test(hash))
			return;

		var target = hash.toLowerCase().indexOf("/snapshot") >= 0
			? "/ui/observability#dashboard-snapshot"
			: "/ui/observability";

		window.location.replace(target);
	}

	function watchLegacyShell() {
		removeLegacyDashboardLink();
		if (!window.MutationObserver || !document.body)
			return;

		var observer = new MutationObserver(removeLegacyDashboardLink);
		observer.observe(document.body, { childList: true, subtree: true });
	}

	if (document.readyState === "loading")
		document.addEventListener("DOMContentLoaded", watchLegacyShell);
	else
		watchLegacyShell();

	window.addEventListener("hashchange", redirectLegacyDashboard);
	redirectLegacyDashboard();
})();
