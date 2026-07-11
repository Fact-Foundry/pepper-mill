using System.Security.Cryptography;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Generates peppers — 256-bit secrets from a cryptographically secure RNG. Never <c>Random</c>,
/// never <c>Guid</c>.
/// </summary>
public static class PepperGenerator
{
    /// <summary>The pepper length in bytes (256 bits).</summary>
    public const int PepperLengthBytes = 32;

    /// <summary>Generates a fresh 256-bit pepper.</summary>
    public static byte[] Generate() => RandomNumberGenerator.GetBytes(PepperLengthBytes);
}
