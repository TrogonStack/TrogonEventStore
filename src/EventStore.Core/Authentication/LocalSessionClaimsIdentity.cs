using System.Collections.Generic;
using System.Security.Claims;

namespace EventStore.Core.Authentication;

public sealed class LocalSessionClaimsIdentity(IEnumerable<Claim> claims) : ClaimsIdentity(claims, "ES-Legacy");
