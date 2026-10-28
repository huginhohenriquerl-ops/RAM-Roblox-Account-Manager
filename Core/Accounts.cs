using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RAM.Core;

public sealed class Account : ObservableObject
{
    string _username = "";
    string _displayName = "";
    string? _alias;
    string? _notes;
    string? _avatarUrl;
    DateTime? _lastLaunchedAt;
    int _runningCount;
    string? _status;
    bool _isInvalid;

    public long UserId { get; set; }
    public string Cookie { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;

    public string Username
    {
        get => _username;
        set { if (Set(ref _username, value)) { OnPropertyChanged(nameof(Subtitle)); OnPropertyChanged(nameof(Initial)); } }
    }

    public string DisplayName
    {
        get => _displayName;
        set { if (Set(ref _displayName, value)) { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Initial)); } }
    }

    public string? Alias
    {
        get => _alias;
        set { if (Set(ref _alias, string.IsNullOrWhiteSpace(value) ? null : value.Trim())) { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Initial)); } }
    }

    public string? Notes
    {
        get => _notes;
        set => Set(ref _notes, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    public string? AvatarUrl
    {
        get => _avatarUrl;
        set => Set(ref _avatarUrl, value);
    }

    public DateTime? LastLaunchedAt
    {
        get => _lastLaunchedAt;
        set => Set(ref _lastLaunchedAt, value);
    }

    [JsonIgnore] public string? CsrfToken { get; set; }

    [JsonIgnore] public string Title => Alias ?? (string.IsNullOrEmpty(DisplayName) ? Username : DisplayName);

    [JsonIgnore] public string Initial => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "?";

    [JsonIgnore]
    public string Subtitle => Status ?? (IsInvalid ? "Cookie expirado" : "@" + Username);

    [JsonIgnore]
    public int RunningCount
    {
        get => _runningCount;
        set { if (Set(ref _runningCount, value)) OnPropertyChanged(nameof(IsRunning)); }
    }

    [JsonIgnore] public bool IsRunning => RunningCount > 0;

    /// <summary>Transient line shown under the name while something is happening to this account.</summary>
    [JsonIgnore]
    public string? Status
    {
        get => _status;
        set { if (Set(ref _status, value)) OnPropertyChanged(nameof(Subtitle)); }
    }

    [JsonIgnore]
    public bool IsInvalid
    {
        get => _isInvalid;
        set { if (Set(ref _isInvalid, value)) OnPropertyChanged(nameof(Subtitle)); }
    }
}

public sealed class AccountStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RAM/Clawd/accounts/v1");

    sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public List<Account> Accounts { get; set; } = new();
    }

    public ObservableCollection<Account> Accounts { get; } = new();

    /// <summary>Set when an existing accounts file could not be decrypted (moved aside, never overwritten).</summary>
    public string? LoadWarning { get; private set; }

    public static AccountStore Load()
    {
        var store = new AccountStore();
        if (!File.Exists(Paths.Accounts)) return store;
        try
        {
            var json = Dpapi.Unprotect(File.ReadAllBytes(Paths.Accounts), Entropy);
            var model = JsonSerializer.Deserialize<FileModel>(json);
            foreach (var account in model?.Accounts ?? new List<Account>())
                store.Accounts.Add(account);
        }
        catch (Exception ex)
        {
            var backup = Paths.Accounts + $".{DateTime.Now:yyyyMMddHHmmss}.bad";
            try { File.Move(Paths.Accounts, backup); } catch { }
            store.LoadWarning = "Não foi possível ler o arquivo de contas. Uma cópia foi guardada em " + backup;
            Log.Error("load accounts", ex);
        }
        return store;
    }

    public void Save()
    {
        try
        {
            var model = new FileModel { Accounts = Accounts.ToList() };
            var json = JsonSerializer.SerializeToUtf8Bytes(model);
            Paths.WriteAtomic(Paths.Accounts, Dpapi.Protect(json, Entropy));
        }
        catch (Exception ex)
        {
            Log.Error("save accounts", ex);
        }
    }

    public Account? FindByUserId(long userId) => Accounts.FirstOrDefault(a => a.UserId == userId);
}
