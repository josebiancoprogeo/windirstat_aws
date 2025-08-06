using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace S3ConfigCli;

class Program
{
    static void Main()
    {
        Console.Write("Access key: ");
        var accessKey = Console.ReadLine()!.Trim();

        Console.Write("Secret key: ");
        var secretKey = ReadPassword();

        Console.Write("Region: ");
        var region = Console.ReadLine()!.Trim();

        Console.Write("Bucket: ");
        var bucket = Console.ReadLine()!.Trim();

        Console.Write("Password to encrypt config: ");
        var password = ReadPassword();

        var config = new S3Config
        {
            AccessKeyId = accessKey,
            SecretAccessKey = secretKey,
            Region = region,
            Bucket = bucket
        };

        EncryptedConfig.Save("s3config.enc", config, password);
        Console.WriteLine("Config saved to s3config.enc");
    }

    static string ReadPassword()
    {
        var sb = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }
            }
            else
            {
                sb.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        Console.WriteLine();
        return sb.ToString();
    }
}

public class S3Config
{
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string Region { get; set; } = "";
    public string Bucket { get; set; } = "";
}

public static class EncryptedConfig
{
    public static void Save(string path, S3Config config, string password)
    {
        var json = JsonSerializer.Serialize(config);
        using var aes = Aes.Create();
        aes.KeySize = 256;
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        aes.Key = key.GetBytes(32);
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var output = new byte[salt.Length + aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(salt, 0, output, 0, salt.Length);
        Buffer.BlockCopy(aes.IV, 0, output, salt.Length, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, output, salt.Length + aes.IV.Length, cipherBytes.Length);

        File.WriteAllBytes(path, output);
    }

    public static S3Config Load(string path, string password)
    {
        var bytes = File.ReadAllBytes(path);
        var salt = bytes[..16];
        var iv = bytes[16..32];
        var cipherBytes = bytes[32..];

        using var aes = Aes.Create();
        var key = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        aes.Key = key.GetBytes(32);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        var json = Encoding.UTF8.GetString(plainBytes);
        return JsonSerializer.Deserialize<S3Config>(json)!;
    }
}
