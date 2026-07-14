using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SpeedyCompass.Backend;

public static class EncryptionHelper
{
    // PRODUCTION NOTE: These must be 32 bytes (Key) and 16 bytes (IV). 
    // In production, fetch these from Azure Key Vault or Environment Config!
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("SpeedyCompassSecureKey1234567890");
    private static readonly byte[] IV = Encoding.UTF8.GetBytes("SpeedyCompassIV_");

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;

        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = IV;

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();
        using var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write);
        using (var sw = new StreamWriter(cs))
        {
            sw.Write(plainText);
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;

        try
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.IV = IV;

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream(Convert.FromBase64String(cipherText));
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);
            return sr.ReadToEnd();
        }
        catch
        {
            // Fallback: If the decryption fails, it likely means the data was saved 
            // BEFORE encryption was implemented. Return raw string.
            return cipherText;
        }
    }
}