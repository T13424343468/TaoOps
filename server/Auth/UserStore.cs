using System.IO;
using System.Text.Json;

namespace TaoOps.Server.Auth;

/// <summary>
/// 面板用户存储：JSON 真源（dataDir/auth/users.json），tmp+move 原子写盘（家法三.6）。
/// 首次为空：经 bootstrap 端点创建首个管理员，避免出厂默认口令。
/// </summary>
public sealed class UserStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly object _lock = new();

    public UserStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "auth");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "users.json");
    }

    public string FilePath => _filePath;

    public List<UserAccount> Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return [];
            return JsonSerializer.Deserialize<List<UserAccount>>(File.ReadAllText(_filePath), JsonOpts) ?? [];
        }
    }

    public UserAccount? Find(string username) =>
        Load().FirstOrDefault(u => u.Username == username);

    public void Save(List<UserAccount> users)
    {
        lock (_lock)
        {
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(users, JsonOpts));
            File.Move(tmp, _filePath, overwrite: true);
        }
    }

    /// <summary>新增账号；用户名重复抛错（边界校验放入口，三.7）。</summary>
    public UserAccount Add(UserAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Username))
            throw new InvalidOperationException("username must not be empty");
        var users = Load();
        if (users.Any(u => u.Username == account.Username))
            throw new InvalidOperationException($"user already exists: {account.Username}");
        users.Add(account);
        Save(users);
        return account;
    }

    public bool Remove(string username)
    {
        var users = Load();
        var removed = users.RemoveAll(u => u.Username == username) > 0;
        if (removed) Save(users);
        return removed;
    }

    /// <summary>更新密码哈希（backlog F2 修改自己密码）；用户不存在返回 false。</summary>
    public bool UpdatePasswordHash(string username, string passwordHash)
    {
        var users = Load();
        var account = users.FirstOrDefault(u => u.Username == username);
        if (account is null) return false;
        account.PasswordHash = passwordHash;
        Save(users);
        return true;
    }
}
