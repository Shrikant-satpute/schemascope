using System.Security.Cryptography;
using System.Text;

namespace SchemaScope.Core.Storage;

/// <summary>
/// Encrypts saved passwords with Windows DPAPI, tied to the current Windows
/// user. The key never leaves this machine and no other user account can read
/// the file, even with the raw database in hand.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SchemaScope.v1.connection");

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static byte[]? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;
        if (!OperatingSystem.IsWindows()) return null;

        return ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText),
            Entropy,
            DataProtectionScope.CurrentUser);
    }

    public static string? Unprotect(byte[]? cipher)
    {
        if (cipher is null || cipher.Length == 0) return null;
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var bytes = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            // Saved by a different Windows user, or the profile was rebuilt.
            // Treat it as "no password saved" rather than crashing.
            return null;
        }
    }
}
