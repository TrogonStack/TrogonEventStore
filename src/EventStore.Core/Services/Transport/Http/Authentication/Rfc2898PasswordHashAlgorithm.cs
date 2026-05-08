using System.Security.Cryptography;
using EventStore.Core.Authentication.InternalAuthentication;

namespace EventStore.Core.Services.Transport.Http.Authentication
{
	public class Rfc2898PasswordHashAlgorithm : PasswordHashAlgorithm
	{
		private const int LegacyHashSize = 20;
		private const int HashSize = 32;
		private const int SaltSize = 16;
		private const int LegacyIterations = 1000;
		private const int Iterations = 600_000;
		private const string Version = "v2";
		private const string HashAlgorithm = "SHA256";

		public override void Hash(string password, out string hash, out string salt)
		{
			var saltData = new byte[SaltSize];
			RandomNumberGenerator.Fill(saltData);

			var hashData = Rfc2898DeriveBytes.Pbkdf2(
				password,
				saltData,
				Iterations,
				HashAlgorithmName.SHA256,
				HashSize);
			hash = $"{Version}${HashAlgorithm}${Iterations}${System.Convert.ToBase64String(hashData)}";
			salt = System.Convert.ToBase64String(saltData);
		}

		public override bool Verify(string password, string hash, string salt)
		{
			if (!TryParseHash(hash, out var hashAlgorithm, out var iterations, out var expectedHash))
				return false;

			var saltData = System.Convert.FromBase64String(salt);
			var actualHash = Rfc2898DeriveBytes.Pbkdf2(
				password,
				saltData,
				iterations,
				hashAlgorithm,
				expectedHash.Length);

			return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
		}

		private static bool TryParseHash(
			string hash,
			out HashAlgorithmName hashAlgorithm,
			out int iterations,
			out byte[] expectedHash)
		{
			hashAlgorithm = HashAlgorithmName.SHA1;
			iterations = LegacyIterations;
			expectedHash = null;

			if (!hash.Contains('$', System.StringComparison.Ordinal))
				return TryReadHash(hash, LegacyHashSize, out expectedHash);

			var parts = hash.Split('$');
			if (parts.Length != 4 ||
				parts[0] != Version ||
				parts[1] != HashAlgorithm ||
				!int.TryParse(parts[2], out iterations) ||
				iterations <= 0)
				return false;

			hashAlgorithm = HashAlgorithmName.SHA256;
			return TryReadHash(parts[3], HashSize, out expectedHash);
		}

		private static bool TryReadHash(string hash, int hashSize, out byte[] expectedHash)
		{
			try
			{
				expectedHash = System.Convert.FromBase64String(hash);
			}
			catch (System.FormatException)
			{
				expectedHash = null;
				return false;
			}

			return expectedHash.Length == hashSize;
		}
	}
}
