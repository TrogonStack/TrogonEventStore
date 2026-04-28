(function () {
	"use strict";

	var pollIntervalMs = 1000;
	var dashboards = new WeakMap();

	function start() {
		var roots = document.querySelectorAll("[data-cluster-status]");
		for (var i = 0; i < roots.length; i++)
			initialize(roots[i]);
	}

	function initialize(root) {
		if (dashboards.has(root))
			return;

		var state = {
			root: root,
			members: [],
			replicas: [],
			previousReplicas: new Map(),
			lastReplicaRefresh: null,
			leaderEndpoint: "",
			gossipInFlight: false,
			replicationInFlight: false
		};

		dashboards.set(root, state);
		openClusterSnapshot();
		refreshGossip(state);
		window.setInterval(function () {
			if (document.hidden)
				return;

			refreshGossip(state);
		}, pollIntervalMs);
	}

	async function refreshGossip(state) {
		if (state.gossipInFlight)
			return;

		state.gossipInFlight = true;
		try {
			var response = await fetch("/gossip?format=json", {
				credentials: "same-origin",
				headers: { "Accept": "application/json" }
			});

			if (!response.ok)
				throw new Error("Gossip endpoint returned " + response.status + " " + response.statusText);

			var payload = await response.json();
			state.members = parseMembers(payload);
			setStatus(state.root, "Gossip live", "Updated " + formatTime(new Date()));
			render(state);
			refreshReplication(state);
		} catch (error) {
			setStatus(state.root, "Gossip unavailable", friendlyMessage(error));
		} finally {
			state.gossipInFlight = false;
		}
	}

	async function refreshReplication(state) {
		if (state.replicationInFlight)
			return;

		var leader = findLeader(state.members);
		if (!leader) {
			state.replicas = [];
			state.leaderEndpoint = "";
			renderReplicas(state);
			return;
		}

		var endpointValue = endpoint(leader.httpHost, leader.httpPort);
		if (state.leaderEndpoint !== endpointValue) {
			state.leaderEndpoint = endpointValue;
			state.previousReplicas = new Map();
			state.lastReplicaRefresh = null;
		}

		state.replicationInFlight = true;
		try {
			var response = await fetch(window.location.protocol + "//" + endpointValue + "/stats/replication?format=json", {
				credentials: "same-origin",
				headers: { "Accept": "application/json" }
			});

			if (!response.ok)
				throw new Error("Replication stats endpoint returned " + response.status + " " + response.statusText);

			var payload = await response.json();
			var now = Date.now();
			state.replicas = parseReplicas(payload, state.members, leader, state.previousReplicas, now);
			state.previousReplicas = indexReplicas(state.replicas);
			state.lastReplicaRefresh = now;
			renderReplicas(state);
		} catch (error) {
			setReplicaStatus(state.root, "Replica stats unavailable: " + friendlyMessage(error));
		} finally {
			state.replicationInFlight = false;
		}
	}

	function parseMembers(payload) {
		var rows = readField(payload || {}, ["members", "Members"], []);
		if (!Array.isArray(rows))
			return [];

		return rows.map(function (row) {
			var internalSecureTcpPort = readFieldNumber(row, ["internalSecureTcpPort", "InternalSecureTcpPort"]);
			var internalTcpPort = readFieldNumber(row, ["internalTcpPort", "InternalTcpPort"]);
			var externalSecureTcpPort = readFieldNumber(row, ["externalSecureTcpPort", "ExternalSecureTcpPort"]);
			var externalTcpPort = readFieldNumber(row, ["externalTcpPort", "ExternalTcpPort"]);

			return {
				instanceId: readFieldString(row, ["instanceId", "InstanceId"], ""),
				timeStamp: readFieldString(row, ["timeStamp", "TimeStamp"], ""),
				state: readFieldString(row, ["state", "State"], "Unknown"),
				isAlive: readFieldBoolean(row, ["isAlive", "IsAlive"]),
				internalTcpIp: readFieldString(row, ["internalTcpIp", "InternalTcpIp"], ""),
				internalTcpPort: internalSecureTcpPort || internalTcpPort,
				externalTcpIp: readFieldString(row, ["externalTcpIp", "ExternalTcpIp"], ""),
				externalTcpPort: externalSecureTcpPort || externalTcpPort,
				httpHost: readFieldString(row, ["httpEndPointIp", "HttpEndPointIp"], ""),
				httpPort: readFieldNumber(row, ["httpEndPointPort", "HttpEndPointPort"]),
				lastCommitPosition: readFieldNumber(row, ["lastCommitPosition", "LastCommitPosition"]),
				writerCheckpoint: readFieldNumber(row, ["writerCheckpoint", "WriterCheckpoint"]),
				chaserCheckpoint: readFieldNumber(row, ["chaserCheckpoint", "ChaserCheckpoint"]),
				epochPosition: readFieldNumber(row, ["epochPosition", "EpochPosition"]),
				epochNumber: readFieldNumber(row, ["epochNumber", "EpochNumber"]),
				epochId: readFieldString(row, ["epochId", "EpochId"], ""),
				nodePriority: readFieldNumber(row, ["nodePriority", "NodePriority"]),
				isReadOnlyReplica: readFieldBoolean(row, ["isReadOnlyReplica", "IsReadOnlyReplica"]),
				esVersion: readFieldString(row, ["esVersion", "ESVersion"], "")
			};
		});
	}

	function parseReplicas(payload, members, leader, previous, now) {
		var rows = Array.isArray(payload) ? payload : [];
		return rows.map(function (row) {
			var id = readFieldString(row, ["connectionId", "ConnectionId"], "");
			var totalBytesSent = readFieldNumber(row, ["totalBytesSent", "TotalBytesSent"]);
			var previousRow = previous.get(id);
			var replicaNode = findMemberByInternalEndpoint(members, readFieldString(row, ["subscriptionEndpoint", "SubscriptionEndpoint"], ""));
			var isCatchingUp = !!(replicaNode && replicaNode.state === "CatchingUp");
			var catchupStartTime = now;
			var catchupStartBytesSent = totalBytesSent;

			if (previousRow && previousRow.isCatchingUp) {
				catchupStartTime = previousRow.catchupStartTime;
				catchupStartBytesSent = previousRow.catchupStartBytesSent;
			}

			var catchupIntervals = Math.max(1, (now - catchupStartTime) / pollIntervalMs);
			var approxSpeed = Math.round(Math.max(0, totalBytesSent - catchupStartBytesSent) / catchupIntervals);
			var bytesToCatchUp = isCatchingUp
				? Math.max(0, leader.writerCheckpoint - replicaNode.writerCheckpoint)
				: 0;

			return {
				id: id,
				subscriptionEndpoint: readFieldString(row, ["subscriptionEndpoint", "SubscriptionEndpoint"], "<none>"),
				totalBytesSent: totalBytesSent,
				totalBytesReceived: readFieldNumber(row, ["totalBytesReceived", "TotalBytesReceived"]),
				pendingSendBytes: readFieldNumber(row, ["pendingSendBytes", "PendingSendBytes"]),
				pendingReceivedBytes: readFieldNumber(row, ["pendingReceivedBytes", "PendingReceivedBytes"]),
				sendQueueSize: readFieldNumber(row, ["sendQueueSize", "SendQueueSize"]),
				isCatchingUp: isCatchingUp,
				bytesToCatchUp: bytesToCatchUp,
				approxSpeed: approxSpeed,
				estimatedTime: isCatchingUp && approxSpeed > 0
					? formatDuration(Math.max(1, Math.round(bytesToCatchUp / approxSpeed)))
					: "-",
				catchupStartTime: catchupStartTime,
				catchupStartBytesSent: catchupStartBytesSent
			};
		});
	}

	function render(state) {
		updateMetrics(state.root, state.members);
		renderMembers(state.root, state.members);
		renderSnapshot(state.root, state.members);
		renderReplicas(state);
	}

	function updateMetrics(root, members) {
		var leader = findLeader(members);
		var metrics = {
			members: members.length,
			alive: members.filter(function (x) { return x.isAlive; }).length,
			leader: leader ? endpoint(leader.httpHost, leader.httpPort) : "-",
			readonly: members.filter(function (x) { return x.isReadOnlyReplica; }).length
		};

		Object.keys(metrics).forEach(function (key) {
			var metric = root.querySelector('[data-cluster-metric="' + key + '"]');
			if (!metric)
				return;

			var value = metric.querySelector("p:nth-of-type(2)");
			if (value)
				value.textContent = String(metrics[key]);
		});
	}

	function renderMembers(root, members) {
		var tbody = root.querySelector("[data-cluster-members-body]");
		if (!tbody)
			return;

		replaceChildren(tbody);
		if (members.length === 0) {
			var empty = element("tr");
			var cell = element("td", "px-5 py-4 text-es-muted");
			cell.colSpan = 7;
			cell.textContent = "No nodes in the cluster.";
			empty.appendChild(cell);
			tbody.appendChild(empty);
			return;
		}

		members.forEach(function (member) {
			var row = element("tr", member.isAlive ? "bg-white/70 text-es-ink" : "bg-red-50 text-es-ink");
			var stateCell = element("td", "px-5 py-4");
			appendText(stateCell, "p", member.state, stateClass(member));
			appendText(stateCell, "p", member.instanceId || "No instance id", "mt-1 max-w-[14rem] truncate font-mono text-xs text-es-muted");
			row.appendChild(stateCell);

			appendText(row, "td", member.isAlive ? "Alive" : "Unreachable", member.isAlive ? "px-5 py-4 font-bold text-es-forest" : "px-5 py-4 font-bold text-red-700");
			appendText(row, "td", formatUtc(member.timeStamp), "px-5 py-4 font-mono text-xs text-es-muted");
			appendCheckpoints(row, member);
			appendTcp(row, member);
			appendText(row, "td", endpoint(member.httpHost, member.httpPort), "px-5 py-4 font-mono text-xs text-es-muted");
			appendActions(row, member);
			tbody.appendChild(row);
		});
	}

	function appendCheckpoints(row, member) {
		var cell = element("td", "px-5 py-4 font-mono text-xs text-es-muted");
		if (member.state === "Manager") {
			cell.textContent = "n/a";
		} else {
			appendText(cell, "p", "L" + formatInteger(member.lastCommitPosition) + " / W " + formatInteger(member.writerCheckpoint) + " / C " + formatInteger(member.chaserCheckpoint), "");
			appendText(cell, "p", "E" + member.epochNumber + " @ " + formatInteger(member.epochPosition) + " : { " + member.epochId + " }", "mt-1");
		}
		row.appendChild(cell);
	}

	function appendTcp(row, member) {
		var cell = element("td", "px-5 py-4 font-mono text-xs text-es-muted");
		appendText(cell, "p", "Internal: " + endpoint(member.internalTcpIp, member.internalTcpPort), "");
		appendText(cell, "p", "External: " + endpoint(member.externalTcpIp, member.externalTcpPort), "mt-1");
		row.appendChild(cell);
	}

	function appendActions(row, member) {
		var cell = element("td", "px-5 py-4 text-right");
		var wrap = element("div", "flex flex-wrap justify-end gap-2");
		appendLink(wrap, "Ping", "//" + endpoint(member.httpHost, member.httpPort) + "/ping?format=json");
		appendLink(wrap, "Open", "//" + endpoint(member.httpHost, member.httpPort));
		appendLink(wrap, "Gossip", "//" + endpoint(member.httpHost, member.httpPort) + "/gossip?format=json");
		cell.appendChild(wrap);
		row.appendChild(cell);
	}

	function renderSnapshot(root, members) {
		var node = root.querySelector("[data-cluster-snapshot]");
		if (!node)
			return;

		if (members.length === 0) {
			node.textContent = "Cluster status is not available.";
			return;
		}

		var lines = [
			"Snapshot taken at " + formatUtc(new Date().toISOString()),
			padRight("Internal Tcp", 31) + " " +
				padRight("External Tcp", 31) + " " +
				padRight("Http", 23) + " " +
				padRight("Status", 7) + " " +
				padRight("State", 14) + " " +
				padRight("Timestamp (UTC)", 19) + " Checkpoints"
		];

		members.forEach(function (member) {
			lines.push(
				padRight(endpoint(member.internalTcpIp, member.internalTcpPort), 31) + " " +
				padRight(endpoint(member.externalTcpIp, member.externalTcpPort), 31) + " " +
				padRight(endpoint(member.httpHost, member.httpPort), 23) + " " +
				padRight(member.isAlive ? "Alive" : "Unreachable", 7) + " " +
				padRight(member.state, 14) + " " +
				padRight(formatUtc(member.timeStamp), 19) + " " +
				checkpointSnapshot(member)
			);
		});

		node.textContent = lines.join("\n");
	}

	function renderReplicas(state) {
		var tbody = state.root.querySelector("[data-replica-body]");
		if (!tbody)
			return;

		replaceChildren(tbody);
		if (state.replicas.length === 0) {
			var empty = element("tr");
			var cell = element("td", "px-5 py-4 text-es-muted");
			cell.colSpan = 9;
			cell.textContent = "No replica stats reported.";
			empty.appendChild(cell);
			tbody.appendChild(empty);
			setReplicaStatus(state.root, "No replica stats reported.");
			return;
		}

		state.replicas.forEach(function (replica) {
			var row = element("tr", "bg-white/70 text-es-ink");
			appendText(row, "td", replica.subscriptionEndpoint, "px-5 py-4 font-mono text-xs text-es-muted");
			appendText(row, "td", formatInteger(replica.totalBytesSent), "px-5 py-4 text-right font-mono text-es-ink");
			appendText(row, "td", formatInteger(replica.totalBytesReceived), "px-5 py-4 text-right font-mono text-es-ink");
			appendText(row, "td", formatInteger(replica.pendingSendBytes), "px-5 py-4 text-right font-mono text-es-ink");
			appendText(row, "td", formatInteger(replica.pendingReceivedBytes), "px-5 py-4 text-right font-mono text-es-ink");
			appendText(row, "td", formatInteger(replica.sendQueueSize), "px-5 py-4 text-right font-mono text-es-ink");
			if (!replica.isCatchingUp) {
				var caughtUp = element("td", "px-5 py-4 text-right font-bold text-es-forest");
				caughtUp.colSpan = 3;
				caughtUp.textContent = "Caught Up";
				row.appendChild(caughtUp);
			} else {
				appendText(row, "td", formatInteger(replica.bytesToCatchUp), "px-5 py-4 text-right font-mono text-es-ink");
				appendText(row, "td", formatInteger(replica.approxSpeed), "px-5 py-4 text-right font-mono text-es-ink");
				appendText(row, "td", replica.estimatedTime, "px-5 py-4 text-right font-mono text-es-ink");
			}
			tbody.appendChild(row);
		});
		setReplicaStatus(state.root, state.replicas.length + " replica connection" + (state.replicas.length === 1 ? "" : "s"));
	}

	function openClusterSnapshot() {
		if (window.location.hash !== "#cluster-snapshot")
			return;

		var snapshot = document.getElementById("cluster-snapshot");
		if (snapshot)
			snapshot.open = true;
	}

	function findLeader(members) {
		for (var i = 0; i < members.length; i++) {
			if (members[i].state === "Leader")
				return members[i];
		}
		return null;
	}

	function findMemberByInternalEndpoint(members, endpointValue) {
		var cleaned = endpointValue.replace("Unspecified/", "");
		for (var i = 0; i < members.length; i++) {
			if (endpoint(members[i].internalTcpIp, members[i].internalTcpPort) === cleaned)
				return members[i];
		}
		return null;
	}

	function indexReplicas(replicas) {
		var indexed = new Map();
		replicas.forEach(function (replica) {
			indexed.set(replica.id, replica);
		});
		return indexed;
	}

	function endpoint(host, port) {
		return (host || "<none>") + ":" + (port || 0);
	}

	function checkpointSnapshot(member) {
		if (member.state === "Manager")
			return "n/a";

		return "L" + member.lastCommitPosition +
			"/W" + member.writerCheckpoint +
			"/C" + member.chaserCheckpoint +
			"/E" + member.epochNumber +
			"@" + member.epochPosition +
			":{" + member.epochId + "}";
	}

	function stateClass(member) {
		if (member.state === "Leader")
			return "font-black text-es-ink";

		if (member.state === "Manager")
			return "font-bold text-es-muted";

		if (member.state === "CatchingUp")
			return "font-bold text-amber-700";

		return member.isAlive ? "font-bold text-es-ink" : "font-bold text-red-700";
	}

	function setStatus(root, status, updated) {
		var statusNode = root.querySelector("[data-cluster-status-label]");
		if (statusNode)
			statusNode.textContent = status;

		var updatedNode = root.querySelector("[data-cluster-updated]");
		if (updatedNode)
			updatedNode.textContent = updated;
	}

	function setReplicaStatus(root, status) {
		var statusNode = root.querySelector("[data-replica-status]");
		if (statusNode)
			statusNode.textContent = status;
	}

	function appendLink(parent, label, href) {
		var link = element("a", "rounded-full border border-es-ink/10 px-3 py-1.5 text-xs font-bold text-es-ink hover:border-es-green hover:text-es-forest");
		link.href = href;
		link.target = "_blank";
		link.rel = "noopener noreferrer";
		link.textContent = label;
		parent.appendChild(link);
	}

	function appendText(parent, tagName, text, className) {
		var node = element(tagName, className);
		node.textContent = text;
		parent.appendChild(node);
		return node;
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

	function readField(row, names, fallback) {
		for (var i = 0; i < names.length; i++) {
			if (Object.prototype.hasOwnProperty.call(row, names[i]))
				return row[names[i]];
		}
		return fallback;
	}

	function readFieldString(row, names, fallback) {
		var value = readField(row, names, fallback);
		if (value === null || value === undefined || value === "")
			return fallback;
		return String(value);
	}

	function readFieldNumber(row, names) {
		var value = Number(readField(row, names, 0));
		return Number.isFinite(value) ? value : 0;
	}

	function readFieldBoolean(row, names) {
		var value = readField(row, names, false);
		return value === true || String(value).toLowerCase() === "true";
	}

	function formatUtc(value) {
		var date = value instanceof Date ? value : new Date(value);
		if (Number.isNaN(date.getTime()))
			return "-";

		return date.getUTCFullYear() + "-" +
			String(date.getUTCMonth() + 1).padStart(2, "0") + "-" +
			String(date.getUTCDate()).padStart(2, "0") + " " +
			String(date.getUTCHours()).padStart(2, "0") + ":" +
			String(date.getUTCMinutes()).padStart(2, "0") + ":" +
			String(date.getUTCSeconds()).padStart(2, "0");
	}

	function formatTime(value) {
		return value.toLocaleTimeString(undefined, {
			hour: "2-digit",
			minute: "2-digit",
			second: "2-digit"
		});
	}

	function formatInteger(value) {
		return Math.round(Number(value) || 0).toLocaleString();
	}

	function formatDuration(totalSeconds) {
		var hours = Math.floor(totalSeconds / 3600);
		var minutes = Math.floor((totalSeconds % 3600) / 60);
		var seconds = totalSeconds % 60;
		return String(hours).padStart(2, "0") + ":" +
			String(minutes).padStart(2, "0") + ":" +
			String(seconds).padStart(2, "0");
	}

	function padRight(value, width) {
		var text = truncate(String(value), width);
		while (text.length < width)
			text += " ";

		return text;
	}

	function truncate(value, width) {
		return value.length > width
			? value.slice(0, Math.max(0, width - 3)) + "..."
			: value;
	}

	function friendlyMessage(error) {
		return error && error.message ? error.message : String(error);
	}

	if (document.readyState === "loading")
		document.addEventListener("DOMContentLoaded", start);
	else
		start();
})();
