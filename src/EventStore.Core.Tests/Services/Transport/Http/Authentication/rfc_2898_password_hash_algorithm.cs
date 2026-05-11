using System.Security.Cryptography;
using EventStore.Core.Services.Transport.Http.Authentication;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Http.Authentication {
	namespace rfc_2898_password_hash_algorithm {
		[TestFixture]
		public class when_hashing_a_password {
			private Rfc2898PasswordHashAlgorithm _algorithm;
			private string _password;
			private string _hash;
			private string _salt;

			[SetUp]
			public void SetUp() {
				_password = "Pa55w0rd!";
				_algorithm = new Rfc2898PasswordHashAlgorithm();
				_algorithm.Hash(_password, out _hash, out _salt);
			}

			[Test]
			public void verifies_correct_password() {
				Assert.That(_algorithm.Verify(_password, _hash, _salt));
			}

			[Test]
			public void prefixes_the_hash_with_the_current_version() {
				Assert.That(_hash, Does.StartWith("v2$SHA256$600000$"));
			}

			[Test]
			public void does_not_verify_incorrect_password() {
				Assert.That(!_algorithm.Verify(_password.ToUpper(), _hash, _salt));
			}

			[TestCase(null, "valid-salt")]
			[TestCase("valid-hash", null)]
			[TestCase("", "valid-salt")]
			[TestCase("valid-hash", "")]
			public void does_not_verify_missing_hash_material(string hash, string salt) {
				Assert.False(_algorithm.Verify(_password, hash, salt));
			}

			[TestCase("v2$SHA256$600000")]
			[TestCase("v2$SHA256$600000$not-base64")]
			[TestCase("v2$SHA256$not-a-number$ee1+y7tFN2rFnT6InxyxNuv16Fhq7VGC1nvLzgHm0qU=")]
			[TestCase("v2$SHA256$599999$ee1+y7tFN2rFnT6InxyxNuv16Fhq7VGC1nvLzgHm0qU=")]
			public void does_not_verify_malformed_current_hashes(string hash) {
				Assert.False(_algorithm.Verify(_password, hash, _salt));
			}

			[Test]
			public void verifies_hash_with_stored_iteration_count() {
				var saltData = RandomNumberGenerator.GetBytes(16);
				var iterations = 600_001;
				var hashData = Rfc2898DeriveBytes.Pbkdf2(
					_password,
					saltData,
					iterations,
					HashAlgorithmName.SHA256,
					32);

				var hash = $"v2$SHA256${iterations}${System.Convert.ToBase64String(hashData)}";
				var salt = System.Convert.ToBase64String(saltData);

				Assert.That(_algorithm.Verify(_password, hash, salt));
			}
		}

		[TestFixture]
		public class when_hashing_a_password_twice {
			private Rfc2898PasswordHashAlgorithm _algorithm;
			private string _password;
			private string _hash1;
			private string _salt1;
			private string _hash2;
			private string _salt2;

			[SetUp]
			public void SetUp() {
				_password = "Pa55w0rd!";
				_algorithm = new Rfc2898PasswordHashAlgorithm();
				_algorithm.Hash(_password, out _hash1, out _salt1);
				_algorithm.Hash(_password, out _hash2, out _salt2);
			}

			[Test]
			public void uses_different_salt() {
				Assert.That(_salt1 != _salt2);
			}

			[Test]
			public void generates_different_hashes() {
				Assert.That(_hash1 != _hash2);
			}
		}

		[TestFixture]
		public class when_verifying_legacy_hashes {
			private Rfc2898PasswordHashAlgorithm _algorithm;
			private readonly string _password = "Pa55w0rd!";
			private readonly string _hash = "HKoq6xw3Oird4KqU4RyoY9aFFRc=";
			private readonly string _salt = "+6eoSEkays/BOpzGMLE6Uw==";
			private readonly string _hashStartingWithVersionPrefix = "vYwkAhH8una9mDmclvp9G1UzWb4=";
			private readonly string _saltForHashStartingWithVersionPrefix = "WVadp2fjEOzN93uhQ2Fp3Q==";

			[SetUp]
			public void SetUp() {
				_algorithm = new Rfc2898PasswordHashAlgorithm();
			}

			[Test]
			public void old_hashes_do_not_verify() {
				Assert.False(_algorithm.Verify(_password, _hash, _salt));
			}

			[Test]
			public void old_hashes_starting_with_v_do_not_verify() {
				Assert.False(_algorithm.Verify(_password, _hashStartingWithVersionPrefix, _saltForHashStartingWithVersionPrefix));
			}
		}
	}
}
