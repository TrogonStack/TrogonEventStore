(function () {
	"use strict";

	var pollIntervalMs = 1000;
	var tcpPageSize = 5;
	var dashboards = new WeakMap();

	function start() {
		var roots = document.querySelectorAll("[data-queue-dashboard]");
		for (var i = 0; i < roots.length; i++)
			initialize(roots[i]);
	}

	function initialize(root) {
		if (dashboards.has(root))
			return;

		var state = {
			root: root,
			expanded: parseExpanded(root),
			blocks: [],
			queues: [],
			tcpConnections: [],
			tcpLastSeen: null,
			tcpPage: 0,
			timer: null,
			inFlight: false,
			tcpInFlight: false
		};

		dashboards.set(root, state);
		openDashboardSnapshot();
		root.addEventListener("click", function (event) {
			var tcpPager = event.target.closest("[data-tcp-page]");
			if (tcpPager && root.contains(tcpPager)) {
				event.preventDefault();
				changeTcpPage(state, tcpPager.getAttribute("data-tcp-page"));
				return;
			}

			var toggle = event.target.closest("[data-queue-toggle]");
			if (!toggle || !root.contains(toggle))
				return;

			event.preventDefault();
			var group = toggle.getAttribute("data-queue-toggle");
			if (state.expanded.has(group))
				state.expanded.delete(group);
			else
				state.expanded.add(group);

			syncExpandedUrl(root, state);
			render(state);
		});

		refresh(state);
		refreshTcp(state);
		state.timer = window.setInterval(function () {
			if (document.hidden)
				return;

			refresh(state);
			refreshTcp(state);
		}, pollIntervalMs);
	}

	function parseExpanded(root) {
		var value = root.getAttribute("data-queue-expanded");
		if (!value) {
			var params = new URLSearchParams(window.location.search);
			value = params.get("expanded") || "";
		}

		return new Set(value.split("|").map(function (x) {
			return x.trim();
		}).filter(Boolean));
	}

	function syncExpandedUrl(root, state) {
		if (root.getAttribute("data-queue-dashboard") !== "observability")
			return;

		var url = new URL(window.location.href);
		if (state.expanded.size === 0)
			url.searchParams.delete("expanded");
		else
			url.searchParams.set("expanded", Array.from(state.expanded).sort().join("|"));

		window.history.replaceState(null, "", url.toString());
		root.setAttribute("data-queue-expanded", Array.from(state.expanded).join("|"));
	}

	function openDashboardSnapshot() {
		if (window.location.hash !== "#dashboard-snapshot")
			return;

		var snapshot = document.getElementById("dashboard-snapshot");
		if (snapshot)
			snapshot.open = true;
	}

	async function refresh(state) {
		if (state.inFlight)
			return;

		state.inFlight = true;
		try {
			var response = await fetch("/stats?format=json", {
				credentials: "same-origin",
				headers: { "Accept": "application/json" }
			});

			if (!response.ok)
				throw new Error("Stats endpoint returned " + response.status + " " + response.statusText);

			var payload = await response.json();
			var parsed = parseQueues(payload);
			state.queues = parsed.queues;
			state.blocks = parsed.blocks;
			setStatus(state.root, "Live stats", "Updated " + formatTime(new Date()));
			render(state);
		} catch (error) {
			setStatus(state.root, "Live stats unavailable", friendlyMessage(error));
		} finally {
			state.inFlight = false;
		}
	}

	async function refreshTcp(state) {
		if (state.tcpInFlight || !state.root.querySelector("[data-tcp-table-body]"))
			return;

		state.tcpInFlight = true;
		try {
			var response = await fetch("/stats/tcp?format=json", {
				credentials: "same-origin",
				headers: { "Accept": "application/json" }
			});

			if (!response.ok)
				throw new Error("TCP stats endpoint returned " + response.status + " " + response.statusText);

			var payload = await response.json();
			var now = Date.now();
			var elapsedSeconds = state.tcpLastSeen
				? Math.max(1, (now - state.tcpLastSeen) / 1000)
				: 1;

			state.tcpConnections = parseTcpConnections(payload, state.tcpConnections, elapsedSeconds);
			state.tcpLastSeen = now;
			setTcpStatus(state.root, "TCP live", state.tcpConnections.length + " connection" + (state.tcpConnections.length === 1 ? "" : "s"));
			renderTcpTable(state.root, state.tcpConnections, state);
		} catch (error) {
			setTcpStatus(state.root, "TCP unavailable", friendlyMessage(error));
		} finally {
			state.tcpInFlight = false;
		}
	}

	function parseQueues(payload) {
		var stats = payload && payload.es && payload.es.queue;
		var queues = [];
		if (stats && typeof stats === "object") {
			Object.keys(stats).forEach(function (key) {
				var value = stats[key];
				if (!value || typeof value !== "object")
					return;

				queues.push({
					kind: "queue",
					name: readString(value.queueName, key),
					groupName: readString(value.groupName, ""),
					length: readNumber(value.length),
					lengthCurrentTryPeak: readNumber(value.lengthCurrentTryPeak),
					lengthLifetimePeak: readNumber(value.lengthLifetimePeak),
					avgItemsPerSecond: readNumber(value.avgItemsPerSecond),
					avgProcessingTime: readNumber(value.avgProcessingTime),
					totalItemsProcessed: readNumber(value.totalItemsProcessed),
					inProgressMessage: readString(value.inProgressMessage, "<none>"),
					lastProcessedMessage: readString(value.lastProcessedMessage, "<none>")
				});
			});
		}

		return {
			queues: queues,
			blocks: buildBlocks(queues)
		};
	}

	function parseTcpConnections(payload, previousConnections, elapsedSeconds) {
		var rows = Array.isArray(payload) ? payload : [];
		var previous = new Map();
		previousConnections.forEach(function (connection) {
			previous.set(connection.id, connection);
		});

		return rows.map(function (row) {
			var id = readFieldString(row, ["connectionId", "ConnectionId"], "");
			var totalBytesSent = readFieldNumber(row, ["totalBytesSent", "TotalBytesSent"]);
			var totalBytesReceived = readFieldNumber(row, ["totalBytesReceived", "TotalBytesReceived"]);
			var previousRow = previous.get(id);
			var sentRate = previousRow
				? Math.max(0, (totalBytesSent - previousRow.totalBytesSent) / elapsedSeconds)
				: 0;
			var receivedRate = previousRow
				? Math.max(0, (totalBytesReceived - previousRow.totalBytesReceived) / elapsedSeconds)
				: 0;

			return {
				id: id,
				clientConnectionName: readFieldString(row, ["clientConnectionName", "ClientConnectionName"], "<none>"),
				remoteEndPoint: readFieldString(row, ["remoteEndPoint", "RemoteEndPoint"], "<none>"),
				localEndPoint: readFieldString(row, ["localEndPoint", "LocalEndPoint"], "<none>"),
				totalBytesSent: totalBytesSent,
				totalBytesReceived: totalBytesReceived,
				pendingSendBytes: readFieldNumber(row, ["pendingSendBytes", "PendingSendBytes"]),
				pendingReceivedBytes: readFieldNumber(row, ["pendingReceivedBytes", "PendingReceivedBytes"]),
				sentRate: sentRate,
				receivedRate: receivedRate,
				isExternalConnection: readFieldBoolean(row, ["isExternalConnection", "IsExternalConnection"]),
				isSslConnection: readFieldBoolean(row, ["isSslConnection", "IsSslConnection"])
			};
		}).sort(function (left, right) {
			return left.clientConnectionName.localeCompare(right.clientConnectionName, undefined, { sensitivity: "base" }) ||
				left.id.localeCompare(right.id, undefined, { sensitivity: "base" });
		});
	}

	function buildBlocks(queues) {
		var blocks = [];
		var groups = new Map();

		queues.forEach(function (queue) {
			if (!queue.groupName) {
				blocks.push({ name: queue.name, summary: queue, children: [] });
				return;
			}

			if (!groups.has(queue.groupName))
				groups.set(queue.groupName, []);

			groups.get(queue.groupName).push(queue);
		});

		groups.forEach(function (children, name) {
			children.sort(compareByName);
			blocks.push({
				name: name,
				summary: groupSummary(name, children),
				children: children.map(function (child) {
					return Object.assign({}, child, { kind: "member" });
				})
			});
		});

		return blocks.sort(function (left, right) {
			return left.name.localeCompare(right.name, undefined, { sensitivity: "base" });
		});
	}

	function groupSummary(name, children) {
		var totalRate = sum(children, "avgItemsPerSecond");
		var totalProcessed = sum(children, "totalItemsProcessed");
		return {
			kind: "group",
			name: name,
			groupName: name,
			length: sum(children, "length"),
			lengthCurrentTryPeak: max(children, "lengthCurrentTryPeak"),
			lengthLifetimePeak: max(children, "lengthLifetimePeak"),
			avgItemsPerSecond: totalRate,
			avgProcessingTime: weightedAverage(children, totalRate, totalProcessed),
			totalItemsProcessed: totalProcessed,
			inProgressMessage: "n/a",
			lastProcessedMessage: "n/a"
		};
	}

	function weightedAverage(rows, totalRate, totalProcessed) {
		if (rows.length === 0)
			return 0;

		if (totalRate > 0)
			return rows.reduce(function (total, row) {
				return total + row.avgProcessingTime * row.avgItemsPerSecond;
			}, 0) / totalRate;

		if (totalProcessed > 0)
			return rows.reduce(function (total, row) {
				return total + row.avgProcessingTime * row.totalItemsProcessed;
			}, 0) / totalProcessed;

		return rows.reduce(function (total, row) {
			return total + row.avgProcessingTime;
		}, 0) / rows.length;
	}

	function render(state) {
		updateMetrics(state.root, state.queues);
		renderPressureList(state.root, state.queues);
		renderSpotlightTable(state.root, state.queues);
		renderQueueTable(state);
		renderDashboardSnapshot(state.root, state.blocks);
	}

	function updateMetrics(root, queues) {
		var metrics = {
			"queue-count": queues.length,
			"busy-count": queues.filter(isBusy).length,
			"backlog": sum(queues, "length"),
			"rate": sum(queues, "avgItemsPerSecond")
		};

		Object.keys(metrics).forEach(function (key) {
			var metric = root.querySelector('[data-queue-metric="' + key + '"]');
			if (!metric)
				return;

			var value = metric.querySelector("p:nth-of-type(2)");
			if (value)
				value.textContent = formatInteger(metrics[key]);
		});
	}

	function renderPressureList(root, queues) {
		var container = root.querySelector("[data-queue-pressure-list]");
		if (!container)
			return;

		replaceChildren(container);
		var spotlight = spotlightQueues(queues, 8);
		if (spotlight.length === 0) {
			appendTextBlock(container, "No queue statistics are available yet.", "text-sm leading-6 text-white/70");
			return;
		}

		var maxPressure = Math.max.apply(null, spotlight.map(pressure));
		spotlight.forEach(function (queue) {
			var row = element("div");
			var header = element("div", "flex items-center justify-between gap-3 text-xs font-bold");
			appendText(header, "span", queue.name, "truncate text-white/80");
			appendText(header, "span", formatInteger(queue.avgItemsPerSecond) + "/s", "font-mono text-white");

			var track = element("div", "mt-2 h-2 overflow-hidden rounded-full bg-white/10");
			var bar = element("div", "h-full rounded-full bg-es-green");
			bar.style.width = Math.max(6, pressure(queue) * 100 / Math.max(1, maxPressure)).toFixed(2) + "%";
			track.appendChild(bar);

			row.appendChild(header);
			row.appendChild(track);
			container.appendChild(row);
		});
	}

	function renderSpotlightTable(root, queues) {
		var tbody = root.querySelector("[data-queue-spotlight-body]");
		if (!tbody)
			return;

		replaceChildren(tbody);
		var rows = spotlightQueues(queues, 6);
		if (rows.length === 0) {
			var empty = element("tr");
			var cell = element("td", "px-4 py-4 text-es-muted");
			cell.colSpan = 3;
			cell.textContent = "Queue data is not available.";
			empty.appendChild(cell);
			tbody.appendChild(empty);
			return;
		}

		rows.forEach(function (queue) {
			var row = element("tr", "bg-white/70");
			var nameCell = element("td", "max-w-xs px-4 py-3");
			appendText(nameCell, "p", queue.name, "truncate font-bold text-es-ink");
			appendText(nameCell, "p", queue.lastProcessedMessage, "mt-1 truncate text-xs text-es-muted");
			row.appendChild(nameCell);
			appendText(row, "td", formatInteger(queue.lengthCurrentTryPeak), "px-4 py-3 text-right font-mono text-es-ink");
			appendText(row, "td", formatInteger(queue.avgItemsPerSecond), "px-4 py-3 text-right font-mono text-es-ink");
			tbody.appendChild(row);
		});
	}

	function renderQueueTable(state) {
		var tbody = state.root.querySelector("[data-queue-table-body]");
		if (!tbody)
			return;

		replaceChildren(tbody);
		if (state.blocks.length === 0) {
			var empty = element("tr");
			var cell = element("td", "px-5 py-4 text-es-muted");
			cell.colSpan = 6;
			cell.textContent = "Queue data is not available.";
			empty.appendChild(cell);
			tbody.appendChild(empty);
			return;
		}

		state.blocks.forEach(function (block) {
			tbody.appendChild(queueTableRow(block.summary, block, state));
			if (block.children.length > 0 && state.expanded.has(block.name)) {
				block.children.forEach(function (child) {
					tbody.appendChild(queueTableRow(child, null, state));
				});
			}
		});
	}

	function renderDashboardSnapshot(root, blocks) {
		var node = root.querySelector("[data-dashboard-snapshot]");
		if (!node)
			return;

		if (blocks.length === 0) {
			node.textContent = "Queue statistics are not available.";
			return;
		}

		var lines = [
			"Dashboard snapshot " + new Date().toISOString(),
			"",
			[
				padRight("Queue", 34),
				padLeft("Current", 9),
				padLeft("Peak", 9),
				padLeft("Rate/s", 8),
				padLeft("ms/item", 9),
				padLeft("Processed", 11),
				"Current / Last Message"
			].join("  ")
		];

		blocks.forEach(function (block) {
			lines.push(snapshotLine(block.summary));
			block.children.forEach(function (child) {
				lines.push(snapshotLine(child));
			});
		});

		node.textContent = lines.join("\n");
	}

	function renderTcpTable(root, connections, state) {
		var tbody = root.querySelector("[data-tcp-table-body]");
		if (!tbody)
			return;

		replaceChildren(tbody);
		if (connections.length === 0) {
			var empty = element("tr");
			var cell = element("td", "px-5 py-4 text-es-muted");
			cell.colSpan = 10;
			cell.textContent = "No TCP connections are currently reported.";
			empty.appendChild(cell);
			tbody.appendChild(empty);
			updateTcpPagination(root, state, 0);
			return;
		}

		var pageCount = Math.ceil(connections.length / tcpPageSize);
		state.tcpPage = Math.min(state.tcpPage, Math.max(0, pageCount - 1));
		var pageStart = state.tcpPage * tcpPageSize;
		connections.slice(pageStart, pageStart + tcpPageSize).forEach(function (connection) {
			var row = element("tr", "bg-white/70 text-es-ink");
			appendText(row, "td", connection.id || "<none>", "max-w-[14rem] truncate px-5 py-4 font-mono text-xs text-es-muted");
			appendText(row, "td", displayMessage(connection.clientConnectionName), "px-5 py-4 font-bold text-es-ink");
			appendText(row, "td", tcpTypeLabel(connection), "px-5 py-4 text-es-muted");
			appendText(row, "td", displayMessage(connection.remoteEndPoint), "px-5 py-4 font-mono text-xs text-es-muted");
			appendText(row, "td", formatByteRate(connection.sentRate), "px-5 py-4 text-right font-mono text-es-ink");
			appendText(row, "td", formatInteger(connection.totalBytesSent), "px-5 py-4 text-right font-mono text-es-ink");
			appendText(row, "td", formatInteger(connection.pendingSendBytes), "px-5 py-4 text-right font-mono text-es-ink");
			appendText(row, "td", formatByteRate(connection.receivedRate), "px-5 py-4 text-right font-mono text-es-ink");
			appendText(row, "td", formatInteger(connection.totalBytesReceived), "px-5 py-4 text-right font-mono text-es-ink");
			appendText(row, "td", formatInteger(connection.pendingReceivedBytes), "px-5 py-4 text-right font-mono text-es-ink");
			tbody.appendChild(row);
		});
		updateTcpPagination(root, state, pageCount);
	}

	function changeTcpPage(state, direction) {
		var pageCount = Math.ceil(state.tcpConnections.length / tcpPageSize);
		if (direction === "previous")
			state.tcpPage = Math.max(0, state.tcpPage - 1);
		else if (direction === "next")
			state.tcpPage = Math.min(Math.max(0, pageCount - 1), state.tcpPage + 1);

		renderTcpTable(state.root, state.tcpConnections, state);
	}

	function queueTableRow(queue, block, state) {
		var row = element("tr", rowClass(queue));
		var nameCell = element("td", "px-5 py-4");
		var nameWrap = element("div", nameClass(queue));

		if (block && block.children.length > 0) {
			var toggle = element("button", "mr-2 inline-flex size-6 items-center justify-center rounded-full border border-es-green/25 bg-es-green/10 text-es-green transition hover:bg-es-green hover:text-white");
			toggle.type = "button";
			toggle.setAttribute("data-queue-toggle", block.name);
			toggle.setAttribute("aria-label", (state.expanded.has(block.name) ? "Collapse " : "Expand ") + block.name);
			toggle.textContent = state.expanded.has(block.name) ? "-" : "+";
			nameWrap.appendChild(toggle);
		} else if (queue.kind === "member") {
			appendText(nameWrap, "span", "-", "mr-2 text-es-muted");
		}

		nameWrap.appendChild(document.createTextNode(queue.name));
		nameCell.appendChild(nameWrap);
		appendText(nameCell, "p", kindLabel(queue), "mt-1 text-xs font-black uppercase tracking-[0.16em] text-es-muted");
		row.appendChild(nameCell);

		var lengthCell = element("td", "px-5 py-4 text-right");
		appendText(lengthCell, "p", formatInteger(queue.lengthCurrentTryPeak), "font-mono text-es-ink");
		appendText(lengthCell, "p", "Peak " + formatInteger(queue.lengthLifetimePeak), "mt-1 text-xs font-black uppercase tracking-[0.16em] text-es-muted");
		row.appendChild(lengthCell);

		appendText(row, "td", formatInteger(queue.avgItemsPerSecond), "px-5 py-4 text-right font-mono text-es-ink");
		appendText(row, "td", queue.avgProcessingTime.toFixed(3), "px-5 py-4 text-right font-mono text-es-ink");
		appendText(row, "td", formatInteger(queue.totalItemsProcessed), "px-5 py-4 text-right font-mono text-es-ink");
		appendText(row, "td", currentLastMessage(queue), "px-5 py-4 font-mono text-xs text-es-muted");
		return row;
	}

	function spotlightQueues(queues, count) {
		return queues.filter(function (queue) {
			return queue.kind !== "group";
		}).sort(function (left, right) {
			var pressureCompare = pressure(right) - pressure(left);
			return pressureCompare || compareByName(left, right);
		}).slice(0, count);
	}

	function pressure(queue) {
		return queue.lengthCurrentTryPeak + queue.avgItemsPerSecond;
	}

	function setStatus(root, status, updated) {
		var statusNode = root.querySelector("[data-queue-status]");
		if (statusNode)
			statusNode.textContent = status;

		var updatedNode = root.querySelector("[data-queue-updated]");
		if (updatedNode)
			updatedNode.textContent = updated;
	}

	function setTcpStatus(root, status, detail) {
		var statusNode = root.querySelector("[data-tcp-status]");
		if (!statusNode)
			return;

		statusNode.textContent = detail ? status + " · " + detail : status;
	}

	function updateTcpPagination(root, state, pageCount) {
		var status = root.querySelector("[data-tcp-page-status]");
		var previous = root.querySelector('[data-tcp-page="previous"]');
		var next = root.querySelector('[data-tcp-page="next"]');
		var hasRows = state.tcpConnections.length > 0;

		if (status)
			status.textContent = hasRows
				? "Page " + (state.tcpPage + 1) + " of " + pageCount
				: "No pages";

		if (previous)
			previous.disabled = !hasRows || state.tcpPage === 0;

		if (next)
			next.disabled = !hasRows || state.tcpPage >= pageCount - 1;
	}

	function tcpTypeLabel(connection) {
		return (connection.isExternalConnection ? "External" : "Internal") + " " +
			(connection.isSslConnection ? "TLS" : "TCP");
	}

	function currentLastMessage(queue) {
		return queue.kind === "group"
			? "n/a"
			: displayMessage(queue.inProgressMessage) + " / " + displayMessage(queue.lastProcessedMessage);
	}

	function isBusy(queue) {
		return !isPlaceholderMessage(queue.inProgressMessage);
	}

	function rowClass(queue) {
		if (queue.kind === "group")
			return "bg-es-ink/5 font-black text-es-ink";

		return queue.kind === "member"
			? "bg-white/45 text-es-muted"
			: "bg-white/70 text-es-ink";
	}

	function nameClass(queue) {
		return queue.kind === "member"
			? "pl-8 font-bold text-es-muted"
			: "font-black text-es-ink";
	}

	function kindLabel(queue) {
		if (queue.kind === "group")
			return "Group";

		return queue.kind === "member" ? "Member" : "Queue";
	}

	function displayMessage(value) {
		return value && String(value).trim() ? String(value) : "<none>";
	}

	function isPlaceholderMessage(value) {
		var message = displayMessage(value).toLowerCase();
		return message === "<none>" || message === "n/a";
	}

	function appendText(parent, tagName, text, className) {
		var child = element(tagName, className);
		child.textContent = text;
		parent.appendChild(child);
		return child;
	}

	function appendTextBlock(parent, text, className) {
		appendText(parent, "p", text, className);
	}

	function element(tagName, className) {
		var node = document.createElement(tagName);
		if (className)
			node.className = className;
		return node;
	}

	function replaceChildren(node) {
		while (node.firstChild)
			node.removeChild(node.firstChild);
	}

	function readString(value, fallback) {
		if (value === null || value === undefined)
			return fallback;

		var text = String(value);
		return text.trim() ? text : fallback;
	}

	function readNumber(value) {
		var number = Number(value);
		return Number.isFinite(number) ? number : 0;
	}

	function readFieldString(source, keys, fallback) {
		var value = readField(source, keys);
		if (value === null || value === undefined)
			return fallback;

		var text = String(value);
		return text.trim() ? text : fallback;
	}

	function readFieldNumber(source, keys) {
		return readNumber(readField(source, keys));
	}

	function readFieldBoolean(source, keys) {
		var value = readField(source, keys);
		return value === true || String(value).toLowerCase() === "true";
	}

	function readField(source, keys) {
		if (!source || typeof source !== "object")
			return undefined;

		for (var i = 0; i < keys.length; i++) {
			if (Object.prototype.hasOwnProperty.call(source, keys[i]))
				return source[keys[i]];
		}

		return undefined;
	}

	function sum(rows, key) {
		return rows.reduce(function (total, row) {
			return total + row[key];
		}, 0);
	}

	function max(rows, key) {
		return rows.reduce(function (current, row) {
			return Math.max(current, row[key]);
		}, 0);
	}

	function compareByName(left, right) {
		return left.name.localeCompare(right.name, undefined, { sensitivity: "base" });
	}

	function formatInteger(value) {
		return Math.round(value).toLocaleString();
	}

	function formatByteRate(value) {
		return formatInteger(value) + " B/s";
	}

	function snapshotLine(queue) {
		return [
			padRight(queue.kind === "member" ? "  " + queue.name : queue.name, 34),
			padLeft(formatInteger(queue.lengthCurrentTryPeak), 9),
			padLeft(formatInteger(queue.lengthLifetimePeak), 9),
			padLeft(formatInteger(queue.avgItemsPerSecond), 8),
			padLeft(queue.avgProcessingTime.toFixed(3), 9),
			padLeft(formatInteger(queue.totalItemsProcessed), 11),
			currentLastMessage(queue)
		].join("  ");
	}

	function padRight(value, width) {
		var text = truncate(String(value), width);
		while (text.length < width)
			text += " ";

		return text;
	}

	function padLeft(value, width) {
		var text = truncate(String(value), width);
		while (text.length < width)
			text = " " + text;

		return text;
	}

	function truncate(value, width) {
		return value.length > width
			? value.slice(0, Math.max(0, width - 3)) + "..."
			: value;
	}

	function formatTime(date) {
		return date.toLocaleTimeString([], {
			hour: "2-digit",
			minute: "2-digit",
			second: "2-digit"
		});
	}

	function friendlyMessage(error) {
		return error && error.message ? error.message : "Unable to refresh queue statistics.";
	}

	if (document.readyState === "loading")
		document.addEventListener("DOMContentLoaded", start);
	else
		start();
})();
