namespace StuiPodcast.Infra.Sync;

public interface IKeyring
{
    // Returns true if the entry was stored successfully.
    bool TrySet(string account, string password);

    // Returns the stored password, or null if unavailable / entry not found.
    string? TryGet(string account);

    // Deletes the entry. No-op if it doesn't exist or keyring is unavailable.
    void TryDelete(string account);
}
