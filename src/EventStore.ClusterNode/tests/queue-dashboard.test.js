import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const dashboardScript = readFileSync(new URL("../ui-assets/js/queue-dashboard.js", import.meta.url), "utf8");

function dashboardWith(payload, fetch) {
	const tableBody = element("tbody");
	const root = {
		getAttribute(name) {
			return name === "data-queue-dashboard" ? "observability" : "";
		},
		querySelector(selector) {
			if (selector === "[data-queue-dashboard-payload]")
				return { textContent: JSON.stringify(payload) };
			if (selector === "[data-replication-table-body]")
				return tableBody;
			return null;
		},
		addEventListener() {}
	};
	const document = {
		readyState: "complete",
		querySelectorAll: () => [root],
		createElement: element,
		getElementById: () => null
	};
	const window = {
		location: { hash: "", pathname: "/ui/observability", search: "" },
		setInterval() {}
	};

	runInNewContext(dashboardScript, { document, window, fetch, URLSearchParams, Date, Map, Set });
	return tableBody;
}

function element(tagName) {
	const node = {
		tagName,
		children: [],
		textContent: "",
		appendChild(child) {
			this.children.push(child);
			return child;
		},
		removeChild(child) {
			this.children.splice(this.children.indexOf(child), 1);
		},
		get firstChild() {
			return this.children[0] || null;
		}
	};
	return node;
}

test("replication errors from the payload replace the empty-connections message", () => {
	const tableBody = dashboardWith({
		queues: [],
		nodeConnections: [],
		replicationConnections: [],
		networkAvailable: true,
		replicationMessage: "Replication statistics access was denied."
	}, () => new Promise(() => {}));

	assert.equal(tableBody.children[0].children[0].textContent,
		"Replication statistics access was denied.");
});

test("a failed refresh does not retain a stale replication error", async () => {
	const tableBody = dashboardWith({
		queues: [],
		nodeConnections: [],
		replicationConnections: [],
		networkAvailable: true,
		replicationMessage: "Replication statistics access was denied."
	}, async () => { throw new Error("Refresh failed"); });

	await new Promise(resolve => setImmediate(resolve));
	assert.equal(tableBody.children[0].children[0].textContent,
		"No active gRPC replication connections.");
});
