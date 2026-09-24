using System.Security.Cryptography;
using Jaftim.Application.Abstractions;
using Jaftim.Infrastructure.Security;

namespace Jaftim.Application.Tests;

public sealed class IdentityCompatiblePasswordHasherTests
{
    private readonly IdentityCompatiblePasswordHasher _hasher = new();

    [Fact]
    public void Hash_then_Verify_round_trips()
    {
        string hash = _hasher.Hash("Secret123!");

        Assert.Equal(PasswordVerificationResult.Success, _hasher.Verify(hash, "Secret123!"));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify(hash, "secret123!"));
    }

    [Fact]
    public void Hash_uses_identity_v3_layout()
    {
        byte[] bytes = Convert.FromBase64String(_hasher.Hash("x"));

        Assert.Equal(0x01, bytes[0]);                 // V3 marker
        Assert.Equal(2u, ReadUInt32(bytes, 1));        // PRF = HMACSHA512
        Assert.Equal(100_000u, ReadUInt32(bytes, 5));  // iterations
        Assert.Equal(16u, ReadUInt32(bytes, 9));       // salt length
        Assert.Equal(13 + 16 + 32, bytes.Length);      // header + salt + subkey
    }

    [Fact]
    public void Verifies_v3_hash_with_older_parameters_and_requests_rehash()
    {
        // Same layout ASP.NET Core Identity 2.x/3.x wrote by default: V3, HMACSHA256, 10000 iterations.
        string legacyStyle = BuildV3("Password1!", prf: 1, iterations: 10_000, HashAlgorithmName.SHA256);

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, _hasher.Verify(legacyStyle, "Password1!"));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify(legacyStyle, "Password1?"));
    }

    [Fact]
    public void Verifies_v2_hash_and_requests_rehash()
    {
        // Identity V2 (marker 0x00): PBKDF2-HMAC-SHA1, 1000 iterations, 16-byte salt, 32-byte subkey.
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] subkey = Rfc2898DeriveBytes.Pbkdf2("Old", salt, 1000, HashAlgorithmName.SHA1, 32);
        byte[] bytes = new byte[1 + 16 + 32];
        Buffer.BlockCopy(salt, 0, bytes, 1, 16);
        Buffer.BlockCopy(subkey, 0, bytes, 17, 32);

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, _hasher.Verify(Convert.ToBase64String(bytes), "Old"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("AA==")]
    public void Garbage_hash_fails_closed(string hash) =>
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify(hash, "anything"));

    private static string BuildV3(string password, uint prf, uint iterations, HashAlgorithmName algorithm)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, (int)iterations, algorithm, 32);
        byte[] bytes = new byte[13 + 16 + 32];
        bytes[0] = 0x01;
        Write(bytes, 1, prf);
        Write(bytes, 5, iterations);
        Write(bytes, 9, 16);
        Buffer.BlockCopy(salt, 0, bytes, 13, 16);
        Buffer.BlockCopy(subkey, 0, bytes, 29, 32);
        return Convert.ToBase64String(bytes);
    }

    private static void Write(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static uint ReadUInt32(byte[] b, int o) =>
        ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
}
