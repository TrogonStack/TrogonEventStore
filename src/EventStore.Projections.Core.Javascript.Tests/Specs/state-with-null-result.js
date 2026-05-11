fromAll()
	.when({
		$init: function () { return { items: [] }; },
		tested: function (state, event) { state.items[event.sequenceNumber] = event.sequenceNumber; },
	})
	.transformBy(function () { return null; });
