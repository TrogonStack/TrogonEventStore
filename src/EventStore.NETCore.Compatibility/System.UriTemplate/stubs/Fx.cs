using System.Diagnostics;

namespace System.Runtime;

internal sealed class Fx
{
	internal static void Assert(bool condition, string message)
	{
		Debug.Assert(condition, message);
	}

	internal static void Assert(string message)
	{
		Debug.Assert(false, message);
	}

	internal static class Exception
	{
		public static System.Exception ArgumentNull(string paramName)
		{
			throw new ArgumentNullException(paramName);
		}
	}
}
