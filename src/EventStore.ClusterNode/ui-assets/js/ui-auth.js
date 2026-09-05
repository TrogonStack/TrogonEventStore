(function () {
	"use strict";

	function setStatus(message) {
		var status = document.querySelector("[data-ui-oauth-status]");
		if (!status)
			return;

		status.textContent = message;
		status.classList.remove("hidden");
	}

	function readJsonAttribute(element, name) {
		try {
			return JSON.parse(element.getAttribute(name) || "{}");
		} catch (_) {
			return {};
		}
	}

	async function beginOAuthSignIn(button) {
		try {
			var properties = readJsonAttribute(button, "data-ui-oauth-properties");
			if (!properties.authorization_endpoint ||
				!properties.client_id ||
				!properties.code_challenge_uri ||
				!properties.redirect_uri ||
				!properties.response_type ||
				!properties.scope)
				throw new Error("The configured provider does not advertise an OAuth browser flow.");

			var baseUrl = window.location.protocol + "//" + window.location.host;
			var challengeResponse = await window.fetch(baseUrl + properties.code_challenge_uri);
			if (!challengeResponse.ok)
				throw new Error("Code challenge endpoint returned " + challengeResponse.status + " " + challengeResponse.statusText);

			var challenge = await challengeResponse.json();
			var returnUrl = button.getAttribute("data-ui-oauth-return") || "";
			var redirectUri = baseUrl + properties.redirect_uri;
			var state = btoa(JSON.stringify({
				code_challenge_correlation_id: challenge.code_challenge_correlation_id,
				return_url: returnUrl,
				redirect_uri: redirectUri
			}));
			var target = properties.authorization_endpoint +
				"?response_type=" + encodeURIComponent(properties.response_type) +
				"&client_id=" + encodeURIComponent(properties.client_id) +
				"&redirect_uri=" + encodeURIComponent(redirectUri) +
				"&scope=" + encodeURIComponent(properties.scope) +
				"&code_challenge=" + encodeURIComponent(challenge.code_challenge) +
				"&code_challenge_method=" + encodeURIComponent(challenge.code_challenge_method) +
				"&state=" + encodeURIComponent(state);

			window.location.href = target;
		} catch (error) {
			setStatus(error && error.message ? error.message : "Unable to start the browser sign-in flow.");
		}
	}

	document.addEventListener("click", function (event) {
		var button = event.target.closest("[data-ui-oauth-signin]");
		if (!button)
			return;

		event.preventDefault();
		beginOAuthSignIn(button);
	});
}());
