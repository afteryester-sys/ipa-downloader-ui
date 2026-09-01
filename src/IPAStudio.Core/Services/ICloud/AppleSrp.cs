using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace IPAStudio.Core.Services.ICloud;

/// <summary>
/// SRP-6a client for Apple ID sign-in.
///
/// Apple switched its sign-in endpoints to SRP, so the password never travels over the
/// wire — the client and server each derive a shared secret and only exchange proofs of
/// it. We implement the exchange directly because the protocol has a handful of
/// Apple-specific quirks:
///
///   * the 2048-bit group and SHA-256 from RFC 5054,
///   * the username is left out of the <c>x</c> derivation, and
///   * the password is pre-hashed with PBKDF2 using the server's salt and iteration
///     count, in one of two "protocols" the server picks (<c>s2k</c> or <c>s2k_fo</c>).
///
/// Instances are single-use: one sign-in attempt each.
/// </summary>
internal sealed class AppleSrp
{
    /// <summary>The 2048-bit safe prime from RFC 5054, appendix A.</summary>
    private const string NHex =
        "AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4" +
        "A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50E8083969EDB767B0CF60" +
        "95179A163AB3661A05FBD5FAAAE82918A9962F0B93B855F97993EC975EEAA80D740ADBF4FF" +
        "747359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481F1D2B907" +
        "8717461A5B9D32E688F87748544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB37861" +
        "60279004E57AE6AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DB" +
        "FBB694B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73";

    private static readonly BigInteger N = Parse(NHex);
    private static readonly BigInteger G = new(2);

    /// <summary>Byte length of N; every value is zero-padded to this width when hashed.</summary>
    private static readonly int PadLength = NHex.Length / 2;

    private readonly BigInteger _a;      // private ephemeral
    private readonly BigInteger _bigA;   // public ephemeral
    private readonly string _accountName;

    private byte[]? _m1;
    private byte[]? _sessionKey;

    public AppleSrp(string accountName)
    {
        _accountName = accountName;

        // 32 random bytes, as an unsigned value.
        var bytes = RandomNumberGenerator.GetBytes(32);
        _a = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        _bigA = BigInteger.ModPow(G, _a, N);
    }

    /// <summary>Public ephemeral A, base64-encoded, for the sign-in "init" call.</summary>
    public string PublicEphemeralBase64 => Convert.ToBase64String(Pad(_bigA));

    /// <summary>
    /// Runs the client half of the exchange against the server's response and returns the
    /// M1 proof to send back.
    /// </summary>
    /// <param name="saltBase64">Per-account salt from the server.</param>
    /// <param name="serverEphemeralBase64">Server public ephemeral B.</param>
    /// <param name="iterations">PBKDF2 iteration count chosen by the server.</param>
    /// <param name="protocol">Either <c>s2k</c> or <c>s2k_fo</c>.</param>
    /// <param name="password">The account password. Not retained.</param>
    public string ComputeProof(string saltBase64, string serverEphemeralBase64, int iterations, string protocol, string password)
    {
        var salt = Convert.FromBase64String(saltBase64);
        var bigBBytes = Convert.FromBase64String(serverEphemeralBase64);
        var bigB = new BigInteger(bigBBytes, isUnsigned: true, isBigEndian: true);

        // The server rejects B ≡ 0 (mod N) itself, but check anyway: continuing would
        // hand a predictable secret to an attacker able to tamper with the response.
        if (bigB % N == BigInteger.Zero)
            throw new CryptographicException("iCloud sign-in: server sent an invalid ephemeral value.");

        var passwordKey = DerivePasswordKey(password, salt, iterations, protocol);

        // x = H(salt | H(":" | passwordKey)) — Apple omits the username here.
        var inner = Sha256(Concat(Encoding.UTF8.GetBytes(":"), passwordKey));
        var x = ToUnsigned(Sha256(Concat(salt, inner)));

        // k = H(N | PAD(g)), u = H(PAD(A) | PAD(B))
        var k = ToUnsigned(Sha256(Concat(Pad(N), Pad(G))));
        var u = ToUnsigned(Sha256(Concat(Pad(_bigA), Pad(bigB))));

        // S = (B - k*g^x)^(a + u*x) mod N, keeping the base positive before ModPow.
        var gx = BigInteger.ModPow(G, x, N);
        var base_ = BigInteger.Remainder(bigB - k * gx, N);
        if (base_.Sign < 0) base_ += N;
        var s = BigInteger.ModPow(base_, _a + u * x, N);

        _sessionKey = Sha256(Pad(s));

        // M1 = H(H(N) XOR H(g) | H(I) | salt | A | B | K)
        var hn = Sha256(Pad(N));
        var hg = Sha256(Pad(G));
        var xor = new byte[hn.Length];
        for (var i = 0; i < hn.Length; i++) xor[i] = (byte)(hn[i] ^ hg[i]);

        _m1 = Sha256(Concat(
            xor,
            Sha256(Encoding.UTF8.GetBytes(_accountName)),
            salt,
            Pad(_bigA),
            Pad(bigB),
            _sessionKey));

        return Convert.ToBase64String(_m1);
    }

    /// <summary>
    /// The proof we expect back from Apple, M2 = H(A | M1 | K). Apple's "complete" call
    /// wants this alongside M1, and the response is checked against it.
    /// Available only after <see cref="ComputeProof"/>.
    /// </summary>
    public string ExpectedServerProofBase64
    {
        get
        {
            if (_m1 is null || _sessionKey is null)
                throw new InvalidOperationException("ComputeProof must run first.");
            return Convert.ToBase64String(ExpectedServerProof());
        }
    }

    /// <summary>
    /// Verifies the server's M2 proof, which is what actually authenticates Apple to us.
    /// Skipping it would leave the exchange open to a server that never knew the password.
    /// </summary>
    public bool VerifyServerProof(string serverProofBase64)
    {
        if (_m1 is null || _sessionKey is null) return false;

        var expected = ExpectedServerProof();
        byte[] actual;
        try
        {
            actual = Convert.FromBase64String(serverProofBase64);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>M2 = H(A | M1 | K).</summary>
    private byte[] ExpectedServerProof() => Sha256(Concat(Pad(_bigA), _m1!, _sessionKey!));

    /// <summary>
    /// Pre-hashes the password the way the server asked. <c>s2k</c> feeds PBKDF2 the raw
    /// SHA-256 digest; <c>s2k_fo</c> feeds it the lowercase hex of that digest.
    /// </summary>
    private static byte[] DerivePasswordKey(string password, byte[] salt, int iterations, string protocol)
    {
        var digest = Sha256(Encoding.UTF8.GetBytes(password));

        byte[] input;
        if (string.Equals(protocol, "s2k_fo", StringComparison.OrdinalIgnoreCase))
            input = Encoding.UTF8.GetBytes(Convert.ToHexString(digest).ToLowerInvariant());
        else
            input = digest;

        return Rfc2898DeriveBytes.Pbkdf2(input, salt, iterations, HashAlgorithmName.SHA256, 32);
    }

    private static BigInteger Parse(string hex)
        => new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);

    private static BigInteger ToUnsigned(byte[] bytes)
        => new(bytes, isUnsigned: true, isBigEndian: true);

    private static byte[] Sha256(byte[] data) => SHA256.HashData(data);

    /// <summary>Big-endian bytes, left-padded with zeros to the width of N.</summary>
    private static byte[] Pad(BigInteger value)
    {
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == PadLength) return raw;
        if (raw.Length > PadLength) return raw[^PadLength..];

        var padded = new byte[PadLength];
        raw.CopyTo(padded, PadLength - raw.Length);
        return padded;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
