using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgePanel.Core;

namespace EdgePanel.Services;

/// <summary>保险箱里保存的一条账号凭据。</summary>
public sealed record VaultItem(
    string Id,
    string App,
    string Account,
    string Password,
    string Note,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// 密码保险箱：AES-256-GCM 加密整个凭据清单，主密码用 PBKDF2-SHA256 派生密钥。
/// 密文落盘 %LOCALAPPDATA%\EdgePanel\vault.dat，主密码本身永不落地。
/// 未解锁时内存里不保留任何明文材料；解锁后驻留内存，Lock() 全部清零。
/// 所有公开方法在任意线程可调用，内部串行化。
/// </summary>
public sealed class VaultService
{
    public static VaultService Instance { get; } = new();

    private static readonly byte[] Magic = { (byte)'E', (byte)'P', (byte)'V', (byte)'B' };

    private const int Version = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 200_000;

    private readonly object _gate = new();
    private byte[]? _salt;
    private int _iterations = Pbkdf2Iterations;
    private List<VaultItem>? _items;

    /// <summary>解锁期间缓存的主密码明文，仅存内存，Lock() 时清零；变更条目时用它重算密钥。</summary>
    private string? _masterPassword;

    public static string FilePath { get; } = Path.Combine(App.DataRoot, "vault.dat");

    /// <summary>主密码校验失败。</summary>
    public sealed class WrongPasswordException : Exception
    {
        public WrongPasswordException() : base("主密码不正确") { }
    }

    /// <summary>磁盘上已有保险箱（重复创建）。</summary>
    public sealed class VaultExistsException : Exception
    {
        public VaultExistsException() : base("保险箱已存在，请直接解锁") { }
    }

    /// <summary>保险箱尚未创建 / 已锁定。</summary>
    public sealed class NoVaultException : Exception
    {
        public NoVaultException() : base("保险箱尚未解锁") { }
    }

    /// <summary>解锁状态变化（创建 / 解锁 / 加锁 / 条目增删后触发，在调用方线程上）。</summary>
    public event Action? StateChanged;

    private VaultService()
    {
    }

    /// <summary>磁盘上是否已有保险箱。</summary>
    public bool HasVault => File.Exists(FilePath);

    /// <summary>当前是否已解锁。</summary>
    public bool IsUnlocked
    {
        get
        {
            lock (_gate)
            {
                return _items is not null;
            }
        }
    }

    /// <summary>已解锁时的凭据条数（未解锁为 0）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items?.Count ?? 0;
            }
        }
    }

    // ————————————————————————— 创建 / 解锁 / 加锁 —————————————————————————

    /// <summary>用主密码创建新保险箱（空清单）。已有保险箱时抛 VaultExistsException。</summary>
    public void CreateVault(string masterPassword)
    {
        ValidatePassword(masterPassword);

        lock (_gate)
        {
            if (File.Exists(FilePath))
            {
                throw new VaultExistsException();
            }

            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[0];
            var tag = new byte[TagSize];
            var key = DeriveKey(masterPassword, salt, Pbkdf2Iterations);
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(nonce, ciphertext, ciphertext, tag);
            }
            finally
            {
                Array.Clear(key);
            }

            WriteFile(Pbkdf2Iterations, salt, nonce, tag, ciphertext);
            _salt = salt;
            _iterations = Pbkdf2Iterations;
            _masterPassword = masterPassword;
            _items = new List<VaultItem>();
        }

        App.Log("保险箱已创建");
        RaiseStateChanged();
    }

    /// <summary>
    /// 用主密码解锁。密码错误抛 WrongPasswordException，
    /// 磁盘上没有保险箱或数据损坏抛 NoVaultException。
    /// </summary>
    public void Unlock(string masterPassword)
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                throw new NoVaultException();
            }

            var data = ReadFile();
            var salt = data.Salt;
            var key = DeriveKey(masterPassword, salt, data.Iterations);
            byte[] plaintext;
            try
            {
                using var aes = new AesGcm(key, TagSize);
                plaintext = new byte[data.Ciphertext.Length];
                aes.Decrypt(data.Nonce, data.Ciphertext, data.Tag, plaintext);
            }
            catch (CryptographicException)
            {
                throw new WrongPasswordException();
            }
            finally
            {
                Array.Clear(key);
            }

            List<VaultItem> items;
            try
            {
                items = Deserialize(plaintext);
            }
            catch (Exception)
            {
                Array.Clear(plaintext);
                throw new WrongPasswordException();
            }
            finally
            {
                Array.Clear(plaintext);
            }

            _salt = salt;
            _iterations = data.Iterations;
            _masterPassword = masterPassword;
            _items = items;
        }

        App.Log("保险箱已解锁");
        RaiseStateChanged();
    }

    /// <summary>加锁：清零内存中的密钥材料与明文凭据。</summary>
    public void Lock()
    {
        lock (_gate)
        {
            _items = null;
            _salt = null;
            _masterPassword = null;
        }

        App.Log("保险箱已加锁");
        RaiseStateChanged();
    }

    /// <summary>
    /// 重置保险箱（忘记主密码时用）：主密码无法找回，只能删除整个保险箱。
    /// 清空内存材料并删除 vault.dat，回到"尚未创建"状态。不可撤销。
    /// </summary>
    public void ResetVault()
    {
        lock (_gate)
        {
            _items = null;
            _salt = null;
            _masterPassword = null;

            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }

            var temp = FilePath + ".tmp";
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        App.Log("保险箱已重置（vault.dat 已删除）");
        RaiseStateChanged();
    }

    /// <summary>更换主密码：先校验当前密码，再用新密码重新加密落盘。</summary>
    public void ChangeMasterPassword(string currentPassword, string newPassword)
    {
        ValidatePassword(newPassword);

        lock (_gate)
        {
            EnsureUnlocked();

            var key = DeriveKey(currentPassword, _salt!, _iterations);
            try
            {
                var data = ReadFile();
                using var aes = new AesGcm(key, TagSize);
                var check = new byte[data.Ciphertext.Length];
                aes.Decrypt(data.Nonce, data.Ciphertext, data.Tag, check);
                Array.Clear(check);
            }
            catch (CryptographicException)
            {
                throw new WrongPasswordException();
            }
            finally
            {
                Array.Clear(key);
            }

            var newSalt = RandomNumberGenerator.GetBytes(SaltSize);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var plaintext = Serialize(_items!);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            var newKey = DeriveKey(newPassword, newSalt, Pbkdf2Iterations);
            try
            {
                using var aes = new AesGcm(newKey, TagSize);
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
            }
            finally
            {
                Array.Clear(newKey);
                Array.Clear(plaintext);
            }

            WriteFile(Pbkdf2Iterations, newSalt, nonce, tag, ciphertext);
            _salt = newSalt;
            _iterations = Pbkdf2Iterations;
            _masterPassword = newPassword;
        }

        App.Log("保险箱主密码已更换");
    }

    // ————————————————————————— 增删改查（需已解锁） —————————————————————————

    public List<VaultItem> GetItems()
    {
        lock (_gate)
        {
            return _items is null ? new List<VaultItem>() : _items.Select(CloneItem).ToList();
        }
    }

    /// <summary>按应用 + 账号查一条凭据（自动填充场景用），找不到返回 null。</summary>
    public VaultItem? Find(string app, string account)
    {
        lock (_gate)
        {
            if (_items is null)
            {
                return null;
            }

            var match = _items.FirstOrDefault(item =>
                string.Equals(item.App, app, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Account, account, StringComparison.OrdinalIgnoreCase));
            return match is null ? null : CloneItem(match);
        }
    }

    public void AddItem(string app, string account, string password, string note = "")
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            throw new ArgumentException("应用名不能为空");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("密码不能为空");
        }

        var now = DateTime.Now;
        lock (_gate)
        {
            EnsureUnlocked();
            _items!.Add(new VaultItem(
                Guid.NewGuid().ToString("N"), app.Trim(), account.Trim(), password, note.Trim(), now, now));
            PersistLocked();
        }

        App.Log($"保险箱新增凭据：{app.Trim()} / {account.Trim()}");
        RaiseStateChanged();
    }

    public void UpdateItem(string id, string app, string account, string password, string note)
    {
        if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("应用名和密码不能为空");
        }

        lock (_gate)
        {
            EnsureUnlocked();
            var index = _items!.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return;
            }

            _items[index] = new VaultItem(id, app.Trim(), account.Trim(), password, note.Trim(),
                _items[index].CreatedAt, DateTime.Now);
            PersistLocked();
        }

        RaiseStateChanged();
    }

    public void DeleteItem(string id)
    {
        lock (_gate)
        {
            if (_items is null)
            {
                return;
            }

            if (_items.RemoveAll(item => item.Id == id) > 0)
            {
                PersistLocked();
            }
        }

        RaiseStateChanged();
    }

    /// <summary>用缓存的主密码重算密钥、重新加密并原子落盘（调用方必须持有 _gate）。</summary>
    private void PersistLocked()
    {
        var key = DeriveKey(_masterPassword!, _salt!, _iterations);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Serialize(_items!);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        finally
        {
            Array.Clear(key);
            Array.Clear(plaintext);
            Array.Clear(nonce);
        }

        WriteFile(_iterations, _salt!, nonce, tag, ciphertext);
    }

    private void EnsureUnlocked()
    {
        if (_items is null || _salt is null || _masterPassword is null)
        {
            throw new NoVaultException();
        }
    }

    // ————————————————————————— 密钥派生 —————————————————————————

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);

    // ————————————————————————— 文件格式：magic|ver|iter|saltLen|salt|nonce|tag|ciphertext —————————————————————————

    private sealed record VaultBlob(int Iterations, byte[] Salt, byte[] Nonce, byte[] Tag, byte[] Ciphertext);

    private static VaultBlob ReadFile()
    {
        var data = File.ReadAllBytes(FilePath);
        var minLen = 4 + 4 + 4 + 1 + SaltSize + NonceSize + TagSize;
        if (data.Length < minLen || !data.AsSpan(0, 4).SequenceEqual(Magic) ||
            BitConverter.ToUInt32(data, 4) != (uint)Version)
        {
            throw new NoVaultException();
        }

        var iterations = (int)BitConverter.ToUInt32(data, 8);
        if (data[12] != SaltSize)
        {
            throw new NoVaultException();
        }

        var offset = 13;
        var salt = data.AsSpan(offset, SaltSize).ToArray();
        offset += SaltSize;
        var nonce = data.AsSpan(offset, NonceSize).ToArray();
        offset += NonceSize;
        var tag = data.AsSpan(offset, TagSize).ToArray();
        offset += TagSize;
        var ciphertext = data.AsSpan(offset).ToArray();
        return new VaultBlob(iterations, salt, nonce, tag, ciphertext);
    }

    private static void WriteFile(int iterations, byte[] salt, byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        var buffer = new byte[4 + 4 + 4 + 1 + salt.Length + nonce.Length + tag.Length + ciphertext.Length];
        var offset = 0;
        Magic.CopyTo(buffer, offset); offset += 4;
        BitConverter.GetBytes((uint)Version).CopyTo(buffer, offset); offset += 4;
        BitConverter.GetBytes((uint)iterations).CopyTo(buffer, offset); offset += 4;
        buffer[offset++] = (byte)salt.Length;
        salt.CopyTo(buffer, offset); offset += salt.Length;
        nonce.CopyTo(buffer, offset); offset += nonce.Length;
        tag.CopyTo(buffer, offset); offset += tag.Length;
        ciphertext.CopyTo(buffer, offset);

        // 先写临时文件再替换，避免写一半断电把保险箱写成半截
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, buffer);
        File.Move(temp, FilePath, overwrite: true);
    }

    // ————————————————————————— 序列化 —————————————————————————

    private sealed record VaultPayload(int Version, List<VaultItem> Items);

    private static byte[] Serialize(List<VaultItem> items)
        => JsonSerializer.SerializeToUtf8Bytes(new VaultPayload(Version, items));

    private static List<VaultItem> Deserialize(byte[] data)
    {
        var payload = JsonSerializer.Deserialize<VaultPayload>(data)
            ?? throw new JsonException("保险箱数据为空");
        if (payload.Version != Version)
        {
            throw new JsonException("保险箱版本不匹配");
        }

        return payload.Items ?? new List<VaultItem>();
    }

    private static VaultItem CloneItem(VaultItem item) =>
        new(item.Id, item.App, item.Account, item.Password, item.Note, item.CreatedAt, item.UpdatedAt);

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("主密码不能为空");
        }

        if (password.Length < 6)
        {
            throw new ArgumentException("主密码至少 6 位");
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();
}
