using StuiPodcast.Infra.Sync;

namespace StuiPodcast.App.Tests.Fakes;

/// <summary>
/// In-memory keyring for unit tests.
/// Set <see cref="AlwaysFail"/> = true to simulate a system with no keyring available
/// (all operations fail / return null), which forces the plaintext fallback path.
/// </summary>
sealed class FakeKeyring : IKeyring
{
    readonly Dictionary<string, string> _store = new();

    /// <summary>When true every call fails, simulating an unavailable keyring.</summary>
    public bool AlwaysFail { get; set; } = false;

    public bool TrySet(string account, string password)
    {
        if (AlwaysFail) return false;
        _store[account] = password;
        return true;
    }

    public string? TryGet(string account)
    {
        if (AlwaysFail) return null;
        return _store.TryGetValue(account, out var pwd) ? pwd : null;
    }

    public void TryDelete(string account) => _store.Remove(account);

    public bool Contains(string account) => _store.ContainsKey(account);
}
