namespace NUnit.Framework;

#pragma warning disable CS0618
public sealed class HardTimeoutAttribute(int timeout) : TimeoutAttribute(timeout);
#pragma warning restore CS0618
