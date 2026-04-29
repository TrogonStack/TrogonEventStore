(function () {
	"use strict";

	var originalFetch = window.fetch;

	function readCookie(name) {
		var prefix = name + "=";
		var parts = document.cookie ? document.cookie.split(";") : [];
		for (var i = 0; i < parts.length; i++) {
			var part = parts[i].trim();
			if (part.indexOf(prefix) !== 0)
				continue;

			return part.substring(prefix.length);
		}

		return "";
	}

	function safeDecode(value) {
		try {
			return decodeURIComponent(value);
		} catch (_) {
			return value;
		}
	}

	function isHeaderSafe(value) {
		return value && !/[\r\n]/.test(value);
	}

	function readAuthorization() {
		var token = safeDecode(readCookie("oauth_id_token"));
		if (isHeaderSafe(token))
			return "Bearer " + token;

		var raw = safeDecode(readCookie("es-creds"));
		if (!raw)
			return "";

		try {
			var parsed = JSON.parse(raw.indexOf("j:") === 0 ? raw.substring(2) : raw);
			var credentials = parsed && typeof parsed.credentials === "string" ? parsed.credentials : "";
			if (!isHeaderSafe(credentials))
				return "";

			return /^Basic\s+/i.test(credentials) ? credentials : "Basic " + credentials;
		} catch (_) {
			return "";
		}
	}

	function sameOrigin(input) {
		var url = new URL(input instanceof Request ? input.url : input, window.location.href);
		return url.origin === window.location.origin;
	}

	function addAuthorization(input, init) {
		var authorization = readAuthorization();
		if (!authorization || !sameOrigin(input))
			return init;

		var options = init ? Object.assign({}, init) : {};
		var headers = new Headers(options.headers || (input instanceof Request ? input.headers : undefined));
		if (!headers.has("Authorization"))
			headers.set("Authorization", authorization);
		options.headers = headers;
		return options;
	}

	if (originalFetch) {
		window.fetch = function (input, init) {
			return originalFetch(input, addAuthorization(input, init));
		};
	}

	function clearCookie(name) {
		var secure = window.location.protocol === "https:" ? "; secure" : "";
		document.cookie = name + "=; max-age=0; path=/; SameSite=Lax" + secure;
	}

	function clearReadableAuthCookies() {
		clearCookie("es-creds");
		clearCookie("oauth_id_token");
	}

	function setStatus(message) {
		var status = document.querySelector("[data-ui-oauth-status]");
		if (!status)
			return;

		status.textContent = message;
		status.classList.remove("hidden");
	}

	async function beginOAuthSignIn(button) {
		try {
			var infoResponse = await originalFetch("/info?format=json", {
				headers: { "Accept": "application/json" }
			});
			if (!infoResponse.ok)
				throw new Error("Info endpoint returned " + infoResponse.status + " " + infoResponse.statusText);

			var info = await infoResponse.json();
			var authentication = info.authentication || info.Authentication || {};
			var properties = authentication.properties || authentication.Properties || {};
			if ((authentication.type || authentication.Type || "").toLowerCase() !== "oauth")
				throw new Error("The configured provider is not an OAuth browser flow.");

			var baseUrl = window.location.protocol + "//" + window.location.host;
			var challengeResponse = await originalFetch(baseUrl + properties.code_challenge_uri);
			if (!challengeResponse.ok)
				throw new Error("Code challenge endpoint returned " + challengeResponse.status + " " + challengeResponse.statusText);

			var challenge = await challengeResponse.json();
			var state = btoa(JSON.stringify({
				code_challenge_correlation_id: challenge.code_challenge_correlation_id
			}));
			var redirectUri = encodeURIComponent(baseUrl + properties.redirect_uri);
			var target = properties.authorization_endpoint +
				"?response_type=" + encodeURIComponent(properties.response_type) +
				"&client_id=" + encodeURIComponent(properties.client_id) +
				"&redirect_uri=" + redirectUri +
				"&scope=" + encodeURIComponent(properties.scope) +
				"&code_challenge=" + encodeURIComponent(challenge.code_challenge) +
				"&code_challenge_method=" + encodeURIComponent(challenge.code_challenge_method) +
				"&state=" + encodeURIComponent(state);

			var returnUrl = button.getAttribute("data-ui-oauth-return");
			if (returnUrl)
				sessionStorage.setItem("eventstore-ui-return-url", returnUrl);

			window.location.href = target;
		} catch (error) {
			setStatus(error && error.message ? error.message : "Unable to start the browser sign-in flow.");
		}
	}

	document.addEventListener("DOMContentLoaded", function () {
		if (document.querySelector("[data-ui-clear-auth]"))
			clearReadableAuthCookies();

		if (window.location.pathname !== "/ui/signin")
			return;

		var returnUrl = sessionStorage.getItem("eventstore-ui-return-url");
		if (!readAuthorization())
			return;

		if (returnUrl) {
			sessionStorage.removeItem("eventstore-ui-return-url");
			window.location.href = returnUrl;
			return;
		}

		var query = new URLSearchParams(window.location.search);
		if (query.has("code") || query.has("state"))
			window.location.href = "/ui";
	});

	document.addEventListener("click", function (event) {
		var button = event.target.closest("[data-ui-oauth-signin]");
		if (!button)
			return;

		event.preventDefault();
		beginOAuthSignIn(button);
	});

	window.EventStoreUiAuth = {
		readAuthorization: readAuthorization
	};
}());
