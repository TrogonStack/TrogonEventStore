using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using EventStore.Common.Options;
using EventStore.Common.Utils;
using EventStore.Core.Index;
using NUnit.Framework;

namespace EventStore.Core.Tests.Index.IndexV2;

[TestFixture(PTable.IndexEntryV2Size), Explicit]
public class
	opening_a_ptable_with_more_than_32bits_of_records : IndexV1.opening_a_ptable_with_more_than_32bits_of_records
{
	public opening_a_ptable_with_more_than_32bits_of_records(int indexEntrySize) : base(indexEntrySize)
	{
	}
}
