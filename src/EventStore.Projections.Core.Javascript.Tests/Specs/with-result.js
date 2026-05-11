fromAll()
	.when({
		$init: function () { return { count: 0 }; },
		tested: function (state) { state.count++; },
	})
	.transformBy(function (state) { return { total: state.count }; });
