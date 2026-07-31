using System.Security.Cryptography;
using System.Text;
using Authagonal.Backup;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// F228 (remainder) — envelope encryption of the backup archive.
/// </summary>
/// <remarks>
/// Owner-only file permissions removed the most common way a backup gets read; they do nothing about
/// the copy on the backup target, which is the copy that lives longest and travels furthest. The
/// archive carries MFA TOTP seeds — directly replayable second factors with no rotation short of
/// re-enrolling the user — alongside every password hash, client secret hash and recovery-code hash in
/// the deployment.
/// </remarks>
[Collection("Azurite")]
public class BackupEncryptionTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableClient Table(string prefix, string name)
    {
        var client = _svc.GetTableClient($"{prefix}{name}");
        client.CreateIfNotExists();
        return client;
    }

    private static byte[] Kek() => RandomNumberGenerator.GetBytes(32);

    // -----------------------------------------------------------------------
    // The stream format, on its own
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    // Straddles the 64 KiB frame boundary in both directions, which is where framing bugs live.
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(200_000)]
    public void RoundTrips_AtEveryFrameBoundary(int size)
    {
        var key = BackupEncryption.NewContentKey();
        var plaintext = RandomNumberGenerator.GetBytes(size);

        var buffer = new MemoryStream();
        using (var encrypting = BackupEncryption.Encrypt(buffer, key, "Users.jsonl", leaveOpen: true))
            encrypting.Write(plaintext);

        buffer.Position = 0;
        using var decrypting = BackupEncryption.Decrypt(buffer, key, "Users.jsonl");
        using var result = new MemoryStream();
        decrypting.CopyTo(result);

        Assert.Equal(plaintext, result.ToArray());
    }

    [Fact]
    public void Ciphertext_DoesNotContainThePlaintext()
    {
        var key = BackupEncryption.NewContentKey();
        var secret = "TOTP-SEED-THAT-MUST-NOT-APPEAR";

        var buffer = new MemoryStream();
        using (var encrypting = BackupEncryption.Encrypt(buffer, key, "MfaCredentials.jsonl", leaveOpen: true))
            encrypting.Write(Encoding.UTF8.GetBytes(secret));

        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(buffer.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedFile_IsRefused()
    {
        // Without the terminator frame a truncated archive would decrypt to a shorter, entirely
        // valid-looking one — and a restore from it silently drops whatever was cut off.
        var key = BackupEncryption.NewContentKey();
        var buffer = new MemoryStream();
        using (var encrypting = BackupEncryption.Encrypt(buffer, key, "Users.jsonl", leaveOpen: true))
            encrypting.Write(RandomNumberGenerator.GetBytes(150_000));

        var truncated = new MemoryStream(buffer.ToArray()[..80_000]);
        using var decrypting = BackupEncryption.Decrypt(truncated, key, "Users.jsonl");

        Assert.ThrowsAny<Exception>(() => decrypting.CopyTo(Stream.Null));
    }

    [Fact]
    public void AModifiedByte_IsRefused()
    {
        var key = BackupEncryption.NewContentKey();
        var buffer = new MemoryStream();
        using (var encrypting = BackupEncryption.Encrypt(buffer, key, "Users.jsonl", leaveOpen: true))
            encrypting.Write(Encoding.UTF8.GetBytes("original"));

        var bytes = buffer.ToArray();
        bytes[^20] ^= 0xFF;

        using var decrypting = BackupEncryption.Decrypt(new MemoryStream(bytes), key, "Users.jsonl");
        Assert.ThrowsAny<Exception>(() => decrypting.CopyTo(Stream.Null));
    }

    [Fact]
    public void AFileFromTheSameBackup_CannotBeSubstituted()
    {
        // Both files authenticate under the same content key, so without the name in the associated
        // data, dropping Users.jsonl in place of Clients.jsonl would verify perfectly — and restore
        // would apply one table's rows to another.
        var key = BackupEncryption.NewContentKey();
        var buffer = new MemoryStream();
        using (var encrypting = BackupEncryption.Encrypt(buffer, key, "Users.jsonl", leaveOpen: true))
            encrypting.Write(Encoding.UTF8.GetBytes("row"));

        buffer.Position = 0;
        using var decrypting = BackupEncryption.Decrypt(buffer, key, "Clients.jsonl");
        Assert.ThrowsAny<Exception>(() => decrypting.CopyTo(Stream.Null));
    }

    [Fact]
    public void TheWrongKeyIsRefused_AndSaysSoWithoutSayingWhy()
    {
        var contentKey = BackupEncryption.NewContentKey();
        var wrapped = BackupEncryption.WrapKey(contentKey, Kek());

        // GCM does not distinguish "wrong key" from "tampered", deliberately — and neither does the
        // message, so it is not an oracle.
        Assert.Throws<InvalidOperationException>(() => BackupEncryption.UnwrapKey(wrapped, Kek()));
    }

    [Fact]
    public void WrappedKey_RoundTrips()
    {
        var kek = Kek();
        var contentKey = BackupEncryption.NewContentKey();
        Assert.Equal(contentKey, BackupEncryption.UnwrapKey(BackupEncryption.WrapKey(contentKey, kek), kek));
    }

    // -----------------------------------------------------------------------
    // End to end, through backup and restore
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EncryptedBackup_RoundTripsAndLeaksNothingOnDisk()
    {
        var kek = Kek();
        var prefix = $"be{Guid.NewGuid():N}";
        await Table(prefix, "Users").AddEntityAsync(
            new TableEntity("u1", "profile") { ["Email"] = "secret-address@corp.test" });

        var dir = Path.Combine(Path.GetTempPath(), $"be{Guid.NewGuid():N}");
        try
        {
            var manifest = await new BackupService(_svc, new FileSystemBackupTarget(dir), new BackupOptions
            {
                TablePrefix = prefix,
                Gzip = false,
                EncryptionKey = kek,
            }).RunAsync();

            Assert.False(string.IsNullOrEmpty(manifest.WrappedContentKey));

            // What an attacker holding the archive sees.
            var onDisk = await File.ReadAllTextAsync(Path.Combine(dir, manifest.BackupId, "Users.jsonl"));
            Assert.DoesNotContain("secret-address@corp.test", onDisk, StringComparison.Ordinal);

            var restorePrefix = $"ber{Guid.NewGuid():N}";
            var result = await new RestoreService(_svc, new FileSystemBackupSource(dir), new RestoreOptions
            {
                TablePrefix = restorePrefix,
                EncryptionKey = kek,
            }).RunAsync(manifest.BackupId);

            Assert.Equal(1, result.TotalRestored);
            var restored = (await Table(restorePrefix, "Users").GetEntityAsync<TableEntity>("u1", "profile")).Value;
            Assert.Equal("secret-address@corp.test", restored["Email"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task EncryptedBackup_WithoutTheKey_IsRefusedRatherThanGarbled()
    {
        var prefix = $"bn{Guid.NewGuid():N}";
        await Table(prefix, "Users").AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "a@b.test" });

        var dir = Path.Combine(Path.GetTempPath(), $"bn{Guid.NewGuid():N}");
        try
        {
            var manifest = await new BackupService(_svc, new FileSystemBackupTarget(dir), new BackupOptions
            {
                TablePrefix = prefix, Gzip = false, EncryptionKey = Kek(),
            }).RunAsync();

            var restore = new RestoreService(_svc, new FileSystemBackupSource(dir), new RestoreOptions
            {
                TablePrefix = $"bnr{Guid.NewGuid():N}",
            });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => restore.RunAsync(manifest.BackupId));
            Assert.Contains("EncryptionKey", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task APlaintextArchive_IsRefusedWhenAKeyWasSupplied()
    {
        // Downgrade protection. A caller supplying a key has said this deployment's backups are
        // encrypted; accepting a plaintext archive anyway would let anyone who can write to the target
        // replace one with plaintext of their choosing and have it restored without a word.
        var prefix = $"bd{Guid.NewGuid():N}";
        await Table(prefix, "Users").AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "a@b.test" });

        var dir = Path.Combine(Path.GetTempPath(), $"bd{Guid.NewGuid():N}");
        try
        {
            var manifest = await new BackupService(_svc, new FileSystemBackupTarget(dir),
                new BackupOptions { TablePrefix = prefix, Gzip = false }).RunAsync();

            var source = new FileSystemBackupSource(dir);
            var restorePrefix = $"bdr{Guid.NewGuid():N}";

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RestoreService(_svc, source, new RestoreOptions
                {
                    TablePrefix = restorePrefix, EncryptionKey = Kek(),
                }).RunAsync(manifest.BackupId));

            // …and the explicit opt-out works, for an archive that genuinely predates encryption.
            var result = await new RestoreService(_svc, source, new RestoreOptions
            {
                TablePrefix = restorePrefix, EncryptionKey = Kek(), AllowUnencrypted = true,
            }).RunAsync(manifest.BackupId);
            Assert.Equal(1, result.TotalRestored);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RollingUpAnEncryptedBackup_StaysEncrypted()
    {
        // A retention job that quietly produced a plaintext snapshot from encrypted inputs would be
        // performing the downgrade itself — on the copy that outlives everything it was rolled up
        // from, since RollupAndCleanAsync deletes the inputs.
        var kek = Kek();
        var prefix = $"br{Guid.NewGuid():N}";
        await Table(prefix, "Users").AddEntityAsync(
            new TableEntity("u1", "profile") { ["Email"] = "rollup-secret@corp.test" });

        var dir = Path.Combine(Path.GetTempPath(), $"br{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            var full = await new BackupService(_svc, target, new BackupOptions
            {
                TablePrefix = prefix, Gzip = false, EncryptionKey = kek,
            }).RunAsync();

            var source = new FileSystemBackupSource(dir);
            var rolled = await new RollupService(source, target)
                .RollupAsync(full.BackupId, [], gzip: false, newBackupId: $"{full.BackupId}-weekly",
                    encryptionKey: kek);

            Assert.False(string.IsNullOrEmpty(rolled.WrappedContentKey));
            var onDisk = await File.ReadAllTextAsync(Path.Combine(dir, rolled.BackupId, "Users.jsonl"));
            Assert.DoesNotContain("rollup-secret@corp.test", onDisk, StringComparison.Ordinal);

            var restorePrefix = $"brr{Guid.NewGuid():N}";
            var result = await new RestoreService(_svc, source, new RestoreOptions
            {
                TablePrefix = restorePrefix, EncryptionKey = kek,
            }).RunAsync(rolled.BackupId);

            Assert.Equal(1, result.TotalRestored);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RollingUpAnEncryptedBackup_WithoutTheKey_IsRefused()
    {
        var prefix = $"bx{Guid.NewGuid():N}";
        await Table(prefix, "Users").AddEntityAsync(new TableEntity("u1", "profile") { ["Email"] = "a@b.test" });

        var dir = Path.Combine(Path.GetTempPath(), $"bx{Guid.NewGuid():N}");
        try
        {
            var target = new FileSystemBackupTarget(dir);
            var full = await new BackupService(_svc, target, new BackupOptions
            {
                TablePrefix = prefix, Gzip = false, EncryptionKey = Kek(),
            }).RunAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RollupService(new FileSystemBackupSource(dir), target)
                    .RollupAsync(full.BackupId, [], gzip: false));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
