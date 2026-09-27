using System.Security.Cryptography;
using System.Text;

namespace Portal.Core.Services;

public static class CryptoService
{
    private const int InitializationVectorLength = 16;

    public static string? Encrypt(string? input)
    {
        if (input is null)
        {
            return null;
        }

        var key = CredentialsService.CryptoKey;

        if (key is null)
        {
            return input;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Key = GetKeyBytes(key);
            aes.GenerateIV();

            var data = Encoding.UTF8.GetBytes(input);
            var encrypted = aes.EncryptCbc(data, aes.IV);
            var final = aes.IV
                .Concat(encrypted)
                .ToArray();

            return Convert.ToBase64String(final);
        }
        catch
        {
            return input;
        }
    }

    public static string? Decrypt(string? input)
    {
        if (input is null)
        {
            return null;
        }

        var key = CredentialsService.CryptoKey;

        if (key is null)
        {
            return input;
        }

        try
        {
            if (input.Length % 4 != 0 || input.Any(char.IsWhiteSpace))
                return input;

            var data = Convert.FromBase64String(input);
            if (data.Length <= InitializationVectorLength ||
                (data.Length - InitializationVectorLength) % 16 != 0)
                return input;

            using var aes = Aes.Create();
            aes.Key = GetKeyBytes(key);
            var iv = data[..InitializationVectorLength];
            var encrypted = data[InitializationVectorLength..];
            var decrypted = aes.DecryptCbc(encrypted, iv);

            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return input;
        }
    }

    private static byte[] GetKeyBytes(string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        return keyBytes.Length is 16 or 24 or 32
            ? keyBytes
            : SHA256.HashData(keyBytes);
    }
}
