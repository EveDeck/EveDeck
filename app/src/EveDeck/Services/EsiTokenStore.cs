using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveDeck.Services;

// Persists ESI OAuth tokens outside settings.json, encrypted with DPAPI (CurrentUser).
// A refresh token is a bearer credential for the character's ESI scopes, so it must never land in
// the plaintext settings.json the user might share when reporting a bug.
public sealed class EsiTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EveDeck.EsiTokenStore.v1");

    // Portable-export container. DPAPI is machine+user bound by design, so an export cannot reuse it:
    // the whole point is to survive a reinstall. Passphrase -> PBKDF2-SHA256 -> AES-GCM instead.
    private static readonly byte[] ExportMagic = Encoding.ASCII.GetBytes("EDTOK001");
    private const int ExportSaltBytes = 16;
    private const int ExportNonceBytes = 12;
    private const int ExportTagBytes = 16;
    private const int ExportKeyBytes = 32;
    private const int ExportPbkdf2Iterations = 600_000;

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<long, EsiToken> _tokens = new();

    public EsiTokenStore(string appDataFolder)
    {
        _path = System.IO.Path.Combine(appDataFolder, "esi-tokens.dat");
        Load();
    }

    public string Path => _path;

    // Set when Load() found a tokens file it could not decrypt and moved it aside. Surfaced so the
    // UI can say "your links were lost, here is the file" instead of the user finding out one dead
    // ESI feature at a time.
    public bool WasUnreadable { get; private set; }

    // Where the unreadable file was moved to, if any.
    public string? QuarantinedPath { get; private set; }

    public EsiToken? Get(long characterId)
    {
        lock (_lock)
            return _tokens.TryGetValue(characterId, out var t) ? t : null;
    }

    public bool Has(long characterId) => Get(characterId) is not null;

    // Every character that currently holds a grant, newest-authorised last. Feeds the global
    // character roster: a token IS the proof a character was linked at some point, so the roster
    // can be derived from these plus whatever the sets already reference, instead of being a
    // fourth thing to persist and keep in sync.
    public IReadOnlyList<EsiToken> All()
    {
        lock (_lock)
            return _tokens.Values.ToList();
    }

    public void Put(EsiToken token)
    {
        lock (_lock)
        {
            _tokens[token.CharacterId] = token;
            Persist();
        }
    }

    public void Remove(long characterId)
    {
        lock (_lock)
        {
            if (_tokens.Remove(characterId)) Persist();
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            _tokens = new Dictionary<long, EsiToken>();
            if (!File.Exists(_path)) return;
            try
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                var list = JsonSerializer.Deserialize<List<EsiToken>>(Encoding.UTF8.GetString(plain), JsonOptions);
                if (list is null) return;
                foreach (var t in list) _tokens[t.CharacterId] = t;
            }
            catch
            {
                // Unreadable (corrupt, or copied from another Windows user/machine - DPAPI is
                // machine+user bound). MOVE IT ASIDE, never delete: this file is the only copy of
                // every linked character's grant, and deleting it destroyed the one artifact a
                // recovery could have worked from. An OS reinstall lands here, and the user found
                // out only by re-linking every character from scratch.
                try
                {
                    var aside = _path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Move(_path, aside, overwrite: true);
                    QuarantinedPath = aside;
                }
                catch { }
                WasUnreadable = true;
            }
        }
    }

    // Writes every stored grant to a passphrase-encrypted file that can be carried to another
    // machine or a reinstalled OS. Returns how many characters were written.
    //
    // The at-rest file stays DPAPI CurrentUser (right call for a bearer credential: no other local
    // user or process can read it). An export is the deliberate, user-initiated exception, so it
    // carries its own passphrase rather than weakening the everyday store.
    public int Export(string path, string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("A passphrase is required.", nameof(passphrase));

        List<EsiToken> snapshot;
        lock (_lock) snapshot = _tokens.Values.ToList();
        if (snapshot.Count == 0) return 0;

        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, JsonOptions));
        var salt = RandomNumberGenerator.GetBytes(ExportSaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(ExportNonceBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, ExportPbkdf2Iterations, HashAlgorithmName.SHA256, ExportKeyBytes);

        var cipher = new byte[plain.Length];
        var tag = new byte[ExportTagBytes];
        using (var aes = new AesGcm(key, ExportTagBytes))
            aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(key);

        using var fs = File.Create(path);
        fs.Write(ExportMagic);
        fs.Write(salt);
        fs.Write(nonce);
        fs.Write(tag);
        fs.Write(cipher);
        return snapshot.Count;
    }

    // Reads an Export() file and merges its grants into the store, re-encrypting them under THIS
    // machine's DPAPI key. Existing entries for the same character are overwritten (the imported
    // grant is the one being restored). Returns how many characters were imported.
    public int Import(string path, string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("A passphrase is required.", nameof(passphrase));

        var raw = File.ReadAllBytes(path);
        var header = ExportMagic.Length + ExportSaltBytes + ExportNonceBytes + ExportTagBytes;
        if (raw.Length < header || !raw.AsSpan(0, ExportMagic.Length).SequenceEqual(ExportMagic))
            throw new InvalidDataException("That is not an EveDeck character-link export file.");

        var offset = ExportMagic.Length;
        var salt = raw.AsSpan(offset, ExportSaltBytes).ToArray(); offset += ExportSaltBytes;
        var nonce = raw.AsSpan(offset, ExportNonceBytes).ToArray(); offset += ExportNonceBytes;
        var tag = raw.AsSpan(offset, ExportTagBytes).ToArray(); offset += ExportTagBytes;
        var cipher = raw.AsSpan(offset).ToArray();

        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, ExportPbkdf2Iterations, HashAlgorithmName.SHA256, ExportKeyBytes);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, ExportTagBytes);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            // GCM authentication failed - wrong passphrase, or the file was altered. Same signal
            // either way, and the user can only act on the first.
            throw new InvalidDataException("Wrong passphrase, or the export file is damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var list = JsonSerializer.Deserialize<List<EsiToken>>(Encoding.UTF8.GetString(plain), JsonOptions)
                   ?? throw new InvalidDataException("The export file contained no characters.");

        lock (_lock)
        {
            foreach (var t in list) _tokens[t.CharacterId] = t;
            Persist();
        }
        return list.Count;
    }

    private void Persist()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(_tokens.Values.ToList(), JsonOptions);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        if (File.Exists(_path)) File.Replace(tmp, _path, destinationBackupFileName: null);
        else File.Move(tmp, _path);
    }
}

public sealed class EsiToken
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public List<string> Scopes { get; set; } = new();

    // 30s of slack so a token doesn't expire mid-flight between the check and the ESI call.
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromSeconds(30);

    public bool HasScope(string scope) => Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);
}
