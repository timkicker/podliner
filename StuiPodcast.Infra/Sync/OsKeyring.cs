using KeySharp;
using Serilog;

namespace StuiPodcast.Infra.Sync;

// OS keyring via KeySharp (libsecret on Linux, Keychain on macOS, Credential Store on Windows).
// Every method catches all exceptions — callers must handle the bool/null return.
public sealed class OsKeyring : IKeyring
{
    private const string Package = "podliner";
    private const string Service = "gpodder-sync";

    public bool TrySet(string account, string password)
    {
        try
        {
            Keyring.SetPassword(Package, Service, account, password);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Keyring: failed to store password for {Account}", account);
            return false;
        }
    }

    public string? TryGet(string account)
    {
        try
        {
            return Keyring.GetPassword(Package, Service, account);
        }
        catch (Exception ex)
        {
            // KeyringException is thrown when the entry doesn't exist as well as when
            // the keyring is unavailable — both cases just mean "no stored password".
            Log.Debug(ex, "Keyring: could not retrieve password for {Account}", account);
            return null;
        }
    }

    public void TryDelete(string account)
    {
        try
        {
            Keyring.DeletePassword(Package, Service, account);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Keyring: could not delete password for {Account}", account);
        }
    }
}
