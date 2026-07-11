using System.Security.Cryptography;
using System.Text;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Hashes and verifies tenant bearer credentials (<c>key2</c>). Credentials are high-entropy random
/// tokens, so a plain SHA-256 is sufficient (no slow KDF needed); only the hash is ever stored.
/// </summary>
public static class CredentialHasher
{
    /// <summary>Returns the lowercase-hex SHA-256 of a credential, as stored in the credential record.</summary>
    public static string Hash(string credential) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    /// <summary>
    /// Constant-time check that a presented credential matches a stored hash. Returns false for an
    /// empty credential or a malformed stored hash.
    /// </summary>
    public static bool Verify(string presented, string expectedHashHex)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expectedHashHex))
            return false;

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHashHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var presentedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(presentedBytes, expected);
    }
}
