using System.Security.Cryptography;
using Jaftim.Application.Abstractions;

namespace Jaftim.Infrastructure.Security;

/// <summary>
/// Verifies and produces hashes in exactly the format ASP.NET Core Identity writes to AspNetUsers.PasswordHash,
/// without referencing any Identity package. Existing users therefore log in unchanged, and rows the API creates
/// remain valid for the legacy MVC app during coexistence.
///
/// Format V3 (marker 0x01): { 0x01, prf(4, BE), iterations(4, BE), saltLength(4, BE), salt[], subkey[] }.
/// Format V2 (marker 0x00): PBKDF2-HMAC-SHA1, 1000 iterations, 16-byte salt, 32-byte subkey - verified, then re-hashed.
/// </summary>
public sealed class IdentityCompatiblePasswordHasher : IPasswordHasher
{
    private const int V3Iterations = 100_000;
    private const int SaltSize = 16;
    private const int SubkeySize = 32;
    private const int PrfSha1 = 0, PrfSha256 = 1, PrfSha512 = 2;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, V3Iterations, HashAlgorithmName.SHA512, SubkeySize);

        var output = new byte[13 + salt.Length + subkey.Length];
        output[0] = 0x01;
        WriteUInt32BigEndian(output, 1, PrfSha512);
        WriteUInt32BigEndian(output, 5, V3Iterations);
        WriteUInt32BigEndian(output, 9, SaltSize);
        Buffer.BlockCopy(salt, 0, output, 13, salt.Length);
        Buffer.BlockCopy(subkey, 0, output, 13 + salt.Length, subkey.Length);
        return Convert.ToBase64String(output);
    }

    public PasswordVerificationResult Verify(string hashedPassword, string providedPassword)
    {
        if (string.IsNullOrEmpty(hashedPassword) || string.IsNullOrEmpty(providedPassword))
            return PasswordVerificationResult.Failed;

        byte[] decoded;
        try { decoded = Convert.FromBase64String(hashedPassword); }
        catch (FormatException) { return PasswordVerificationResult.Failed; }

        if (decoded.Length == 0) return PasswordVerificationResult.Failed;

        return decoded[0] switch
        {
            0x00 => VerifyV2(decoded, providedPassword) ? PasswordVerificationResult.SuccessRehashNeeded : PasswordVerificationResult.Failed,
            0x01 => VerifyV3(decoded, providedPassword, out bool rehash)
                ? (rehash ? PasswordVerificationResult.SuccessRehashNeeded : PasswordVerificationResult.Success)
                : PasswordVerificationResult.Failed,
            _ => PasswordVerificationResult.Failed,
        };
    }

    private static bool VerifyV2(byte[] hashed, string password)
    {
        const int iterations = 1000, saltSize = 16, subkeySize = 32;
        if (hashed.Length != 1 + saltSize + subkeySize) return false;

        byte[] salt = new byte[saltSize];
        Buffer.BlockCopy(hashed, 1, salt, 0, saltSize);
        byte[] expected = new byte[subkeySize];
        Buffer.BlockCopy(hashed, 1 + saltSize, expected, 0, subkeySize);

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA1, subkeySize);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool VerifyV3(byte[] hashed, string password, out bool rehashNeeded)
    {
        rehashNeeded = false;
        try
        {
            int prf = (int)ReadUInt32BigEndian(hashed, 1);
            int iterations = (int)ReadUInt32BigEndian(hashed, 5);
            int saltLength = (int)ReadUInt32BigEndian(hashed, 9);

            if (saltLength < 128 / 8) return false;
            byte[] salt = new byte[saltLength];
            Buffer.BlockCopy(hashed, 13, salt, 0, saltLength);

            int subkeyLength = hashed.Length - 13 - saltLength;
            if (subkeyLength < 128 / 8) return false;
            byte[] expected = new byte[subkeyLength];
            Buffer.BlockCopy(hashed, 13 + saltLength, expected, 0, subkeyLength);

            HashAlgorithmName algorithm = prf switch
            {
                PrfSha1 => HashAlgorithmName.SHA1,
                PrfSha256 => HashAlgorithmName.SHA256,
                PrfSha512 => HashAlgorithmName.SHA512,
                _ => throw new CryptographicException("Unknown PRF"),
            };

            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, algorithm, subkeyLength);
            bool ok = CryptographicOperations.FixedTimeEquals(actual, expected);
            rehashNeeded = ok && (iterations < V3Iterations || prf != PrfSha512);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset + 0] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint ReadUInt32BigEndian(byte[] buffer, int offset) =>
        ((uint)buffer[offset + 0] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
}
