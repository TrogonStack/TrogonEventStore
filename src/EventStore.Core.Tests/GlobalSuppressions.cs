using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
	"Reliability",
	"CA2022",
	Justification = "These storage tests intentionally exercise Stream.Read behavior rather than exact-read helpers.",
	Scope = "namespaceanddescendants",
	Target = "~N:EventStore.Core.Tests.TransactionLog")]
[assembly: SuppressMessage(
	"Reliability",
	"CA2022",
	Justification = "This ignored regression test preserves the historical FileStream.Read behavior it was written for.",
	Scope = "type",
	Target = "~T:EventStore.Core.Tests.mono_filestream_bug")]
