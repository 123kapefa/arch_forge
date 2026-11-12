using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArchForge.Map.Core.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Map.Infrastructure;

/// <summary>Защита ключа шифрования сессии средствами самой системы, без установки чего-либо (adr/0013, п. 3).</summary>
public interface IKeyProtector
{
    /// <summary>dpapi | keychain | file.</summary>
    string Name { get; }
    /// <summary>Читает ключ (32 байта) или создаёт и сохраняет новый.</summary>
    byte[] GetOrCreateKey();
}

/// <summary>Windows: ключ лежит в файле, зашифрованном DPAPI для текущего пользователя.</summary>
public sealed class DpapiKeyProtector(string directory) : IKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("archforge-session-key-v1");
    public string Name => "dpapi";

    public byte[] GetOrCreateKey()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI есть только в Windows");
        var path = Path.Combine(directory, "session.key");
        if (File.Exists(path))
        {
            try { return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { /* ключ от другого пользователя/профиля: создаём новый, прежняя сессия станет нечитаемой — войдёте заново */ }
        }
        var key = RandomNumberGenerator.GetBytes(32);
        FileSecurity.WriteAtomic(path, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        return key;
    }
}

/// <summary>macOS: ключ лежит в связке ключей Keychain (утилита <c>security</c> есть в системе).
/// Ограничение: запись создана утилитой <c>security</c>, поэтому процесс этого же пользователя может прочитать ключ тем же способом, а на
/// миг записи он виден в списке процессов. Защита — от чтения файла сессии без доступа к связке (резервная копия, другая учётная запись).</summary>
public sealed class KeychainKeyProtector(string account = "archforge", string service = "archforge-session-key") : IKeyProtector
{
    public string Name => "keychain";
    public static bool Available => OperatingSystem.IsMacOS() && File.Exists("/usr/bin/security");

    public byte[] GetOrCreateKey()
    {
        var (code, output) = Run("find-generic-password", "-a", account, "-s", service, "-w");
        if (code == 0 && TryDecode(output.Trim(), out var existing)) return existing;
        // 44 — записи нет. Любой другой сбой (связка заблокирована, отказ пользователя) ключ не создаёт: иначе мы затёрли бы рабочий.
        if (code != 0 && code != 44) throw new InvalidOperationException("Keychain недоступна (код " + code + ")");
        var key = RandomNumberGenerator.GetBytes(32);
        var (add, _) = Run("add-generic-password", "-U", "-a", account, "-s", service, "-w", Convert.ToBase64String(key));
        if (add != 0) throw new InvalidOperationException("не удалось записать ключ в Keychain");
        return key;
    }

    private static bool TryDecode(string s, out byte[] key)
    {
        try { key = Convert.FromBase64String(s); return key.Length == 32; }
        catch (FormatException) { key = []; return false; }
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("security не запустился");
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new TimeoutException("security не ответил"); }
        return (p.ExitCode, output);
    }
}

/// <summary>Запасной способ (Linux без системного хранилища, сбой Keychain): ключ в файле, доступном только владельцу (0600).
/// Слабее системного хранилища: защищает от других пользователей машины, но не от процессов самого пользователя.</summary>
public sealed class FileKeyProtector(string directory) : IKeyProtector
{
    private static bool OwnerOnly(string p) => FileSecurity.OwnerOnly(p);
    public string Name => "file";
    public byte[] GetOrCreateKey()
    {
        var path = Path.Combine(directory, "session.key");
        // Готовый файл ключа доверяем, только если он доступен одному владельцу: чужой/общий файл мог подложить кто-то другой.
        if (File.Exists(path) && OwnerOnly(path) && File.ReadAllBytes(path) is { Length: 32 } existing) return existing;
        var key = RandomNumberGenerator.GetBytes(32);
        FileSecurity.WriteAtomic(path, key);
        return key;
    }
}

internal static class FileSecurity
{
    public static bool OwnerOnly(string path) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;

    /// <summary>Атомарная запись (временный файл + замена), права только владельцу на Unix.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);   // 0700
        }
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                fs.Write(data); fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}

/// <summary>Сессия на диске: AES-256-GCM, ключ защищён системой. Файл повреждён, подменён или ключ другой — сессии нет (не ошибка запуска).</summary>
public sealed class EncryptedFileSessionVault : ISessionVault
{
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("archforge-session-v1");
    private const byte Version = 1;
    private const long MaxFileBytes = 256 * 1024;

    private readonly string _path;
    private readonly IKeyProtector _keys;
    private byte[]? _key;

    public EncryptedFileSessionVault(string directory, IKeyProtector keys) { _path = Path.Combine(directory, "session.bin"); _keys = keys; }
    public string Storage => _keys.Name;

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "arch_forge");

    public static EncryptedFileSessionVault Create(string? directory)
    {
        var dir = directory ?? DefaultDirectory();
        IKeyProtector keys = OperatingSystem.IsWindows() ? new DpapiKeyProtector(dir)
            : KeychainKeyProtector.Available ? new KeychainKeyProtector() : new FileKeyProtector(dir);
        return new EncryptedFileSessionVault(dir, keys);
    }

    public Task<StoredSession?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return Task.FromResult<StoredSession?>(null);
        if (new FileInfo(_path).Length is < 30 or > MaxFileBytes) return Task.FromResult<StoredSession?>(null);
        var raw = File.ReadAllBytes(_path);
        if (raw[0] != Version) return Task.FromResult<StoredSession?>(null);
        try
        {
            var plain = new byte[raw.Length - 29];
            using var gcm = new AesGcm(Key(), 16);
            gcm.Decrypt(raw.AsSpan(1, 12), raw.AsSpan(29), raw.AsSpan(13, 16), plain, Aad);
            return Task.FromResult(JsonSerializer.Deserialize<StoredSession>(plain));
        }
        catch (Exception e) when (e is CryptographicException or JsonException) { return Task.FromResult<StoredSession?>(null); }
    }

    public Task SaveAsync(StoredSession session, CancellationToken ct = default)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        using (var gcm = new AesGcm(Key(), 16)) gcm.Encrypt(nonce, plain, cipher, tag, Aad);
        var file = new byte[1 + 12 + 16 + cipher.Length];
        file[0] = Version; nonce.CopyTo(file, 1); tag.CopyTo(file, 13); cipher.CopyTo(file, 29);
        FileSecurity.WriteAtomic(_path, file);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (File.Exists(_path)) File.Delete(_path);
        return Task.CompletedTask;
    }

    private byte[] Key() => _key ??= _keys.GetOrCreateKey();
}

public static class SessionVaultRegistration
{
    /// <summary>Хранилище сессии (ленивое: ключ и файл трогаются только при первом обращении).</summary>
    public static IServiceCollection AddSessionVault(this IServiceCollection services, string? directory = null)
    {
        services.AddSingleton<ISessionVault>(_ => EncryptedFileSessionVault.Create(directory));
        return services;
    }
}
