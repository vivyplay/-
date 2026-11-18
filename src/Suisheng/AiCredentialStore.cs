using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Suisheng;

public sealed class AiCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Suisheng.AI.CurrentUser.v1");
    private readonly string path;

    public AiCredentialStore(string? root = null) => path = Path.Combine(root ?? SettingsStore.DataDirectory, "ai-credential.bin");
    public bool HasKey => File.Exists(path);

    public string? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            byte[] protectedBytes = File.ReadAllBytes(path);
            if (protectedBytes.Length == 0 || protectedBytes.Length > 16384) throw new CryptographicException();
            byte[] bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                string key = Encoding.UTF8.GetString(bytes);
                if (string.IsNullOrWhiteSpace(key) || key.Length > 1024) throw new CryptographicException();
                return key;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new InvalidDataException("AI 密钥无法读取。仅保存密钥的 Windows 用户可解密；请重新输入密钥，原加密文件未被修改。");
        }
    }

    public void Save(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1024 || key.Any(char.IsControl))
            throw new ArgumentException("API Key 不能为空、超过 1024 字符或包含控制字符。");
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".new";
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Clear()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
