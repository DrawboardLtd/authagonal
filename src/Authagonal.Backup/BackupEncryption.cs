using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Authagonal.Backup;

/// <summary>
/// Envelope encryption for backup archives: a per-backup content key, wrapped by a key the host holds
/// outside the backup target, and AES-256-GCM over the data files.
/// </summary>
/// <remarks>
/// Archives were plaintext JSONL. They carry MFA TOTP seeds — directly replayable second factors, with
/// no rotation short of re-enrolling the user — alongside every password hash, client secret hash and
/// recovery-code hash in the deployment, offline-crackable at leisure. Owner-only file permissions
/// removed the most common way those get read; they do nothing about the copy on the backup target,
/// which is the copy that lives longest and travels furthest.
/// <para>
/// A content key per backup means the long-lived key-encryption key is used only to wrap 32 bytes, so
/// it is never applied to gigabytes of plaintext, and a single archive can be handed to someone by
/// releasing its wrapped key rather than the KEK. The wrapped key rides in the manifest, which is
/// authenticated separately by <c>ManifestMac</c>.
/// </para>
/// </remarks>
public static class BackupEncryption
{
    /// <summary>Identifies the frame format, so a future change is a loud version mismatch rather
    /// than a garbled decrypt.</summary>
    private static readonly byte[] Magic = "AGBK1"u8.ToArray();

    private const int KeyBytes = 32;      // AES-256
    private const int NonceBytes = 12;    // GCM standard
    private const int TagBytes = 16;
    private const int NoncePrefixBytes = 8;

    /// <summary>
    /// Plaintext bytes per frame. GCM needs a complete message to authenticate, and a backup file can
    /// be gigabytes, so the stream is framed rather than buffered whole.
    /// </summary>
    private const int FrameSize = 64 * 1024;

    /// <summary>A fresh content key. One per backup.</summary>
    public static byte[] NewContentKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    /// <summary>
    /// Wraps <paramref name="contentKey"/> under <paramref name="keyEncryptionKey"/>, returning
    /// base64 of nonce ‖ ciphertext ‖ tag.
    /// </summary>
    public static string WrapKey(byte[] contentKey, byte[] keyEncryptionKey)
    {
        RequireKeySize(keyEncryptionKey);

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[contentKey.Length];
        var tag = new byte[TagBytes];

        using var gcm = new AesGcm(keyEncryptionKey, TagBytes);
        gcm.Encrypt(nonce, contentKey, ciphertext, tag, "authagonal-backup-cek"u8);

        var wrapped = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(wrapped, 0);
        ciphertext.CopyTo(wrapped, nonce.Length);
        tag.CopyTo(wrapped, nonce.Length + ciphertext.Length);
        return Convert.ToBase64String(wrapped);
    }

    /// <summary>Reverses <see cref="WrapKey"/>. Throws when the key is wrong or the value was
    /// tampered with — GCM does not distinguish the two, deliberately.</summary>
    public static byte[] UnwrapKey(string wrapped, byte[] keyEncryptionKey)
    {
        RequireKeySize(keyEncryptionKey);

        var blob = Convert.FromBase64String(wrapped);
        if (blob.Length != NonceBytes + KeyBytes + TagBytes)
            throw new InvalidOperationException("The wrapped backup content key is malformed.");

        var nonce = blob.AsSpan(0, NonceBytes);
        var ciphertext = blob.AsSpan(NonceBytes, KeyBytes);
        var tag = blob.AsSpan(NonceBytes + KeyBytes, TagBytes);

        var contentKey = new byte[KeyBytes];
        using var gcm = new AesGcm(keyEncryptionKey, TagBytes);
        try
        {
            gcm.Decrypt(nonce, ciphertext, tag, contentKey, "authagonal-backup-cek"u8);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "The backup content key could not be unwrapped: the supplied EncryptionKey is wrong, " +
                "or the manifest has been tampered with.", ex);
        }

        return contentKey;
    }

    private static void RequireKeySize(byte[] key)
    {
        if (key is not { Length: KeyBytes })
            throw new ArgumentException($"Backup encryption keys must be exactly {KeyBytes} bytes (AES-256).", nameof(key));
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> so everything written is encrypted before it reaches the target.
    /// </summary>
    /// <param name="fileName">
    /// Bound into every frame as associated data, so a file cannot be swapped for another from the
    /// same backup — which would otherwise authenticate perfectly.
    /// </param>
    public static Stream Encrypt(Stream inner, byte[] contentKey, string fileName, bool leaveOpen = false)
        => new EncryptingStream(inner, contentKey, fileName, leaveOpen);

    /// <summary>Reverses <see cref="Encrypt"/>.</summary>
    public static Stream Decrypt(Stream inner, byte[] contentKey, string fileName)
        => new DecryptingStream(inner, contentKey, fileName);

    /// <summary>
    /// Per-frame nonce: an 8-byte random prefix shared by the file, then a 4-byte big-endian counter.
    /// Distinct per frame within a file, and distinct across files because the prefix is redrawn — GCM
    /// nonce reuse under one key is catastrophic, so this is structural rather than incidental.
    /// </summary>
    private static void FillNonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, uint counter)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[NoncePrefixBytes..], counter);
    }

    /// <summary>
    /// Associated data per frame: the file name, the frame index, and whether this is the terminator.
    /// The index stops frames being reordered or dropped; the terminator flag is what makes a
    /// truncated file fail rather than decrypt to a shorter, valid-looking one.
    /// </summary>
    private static byte[] FrameAad(string fileName, uint counter, bool isFinal)
    {
        var name = Encoding.UTF8.GetBytes(fileName);
        var aad = new byte[name.Length + 5];
        name.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(name.Length), counter);
        aad[^1] = isFinal ? (byte)1 : (byte)0;
        return aad;
    }

    private sealed class EncryptingStream : Stream
    {
        private readonly Stream _inner;
        private readonly AesGcm _gcm;
        private readonly string _fileName;
        private readonly bool _leaveOpen;
        private readonly byte[] _noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixBytes);
        private readonly byte[] _buffer = new byte[FrameSize];
        private int _buffered;
        private uint _counter;
        private bool _finished;
        private bool _headerWritten;

        public EncryptingStream(Stream inner, byte[] contentKey, string fileName, bool leaveOpen)
        {
            _inner = inner;
            _gcm = new AesGcm(contentKey, TagBytes);
            _fileName = fileName;
            _leaveOpen = leaveOpen;
        }

        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureHeader();
            while (buffer.Length > 0)
            {
                var take = Math.Min(FrameSize - _buffered, buffer.Length);
                buffer[..take].CopyTo(_buffer.AsSpan(_buffered));
                _buffered += take;
                buffer = buffer[take..];
                if (_buffered == FrameSize) WriteFrame(isFinal: false);
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            // The frame assembly is CPU-bound and small; only the inner writes are worth awaiting, and
            // they happen inside WriteFrame. Correctness matters more than shaving a copy here.
            await Task.Yield();
            Write(buffer.Span);
        }

        private void EnsureHeader()
        {
            if (_headerWritten) return;
            _inner.Write(Magic);
            _inner.Write(_noncePrefix);
            _headerWritten = true;
        }

        private void WriteFrame(bool isFinal)
        {
            Span<byte> nonce = stackalloc byte[NonceBytes];
            FillNonce(nonce, _noncePrefix, _counter);

            var plaintext = _buffer.AsSpan(0, _buffered);
            var ciphertext = new byte[_buffered];
            Span<byte> tag = stackalloc byte[TagBytes];
            _gcm.Encrypt(nonce, plaintext, ciphertext, tag, FrameAad(_fileName, _counter, isFinal));

            Span<byte> lengthPrefix = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, _buffered);
            _inner.Write(lengthPrefix);
            _inner.Write(ciphertext);
            _inner.Write(tag);

            _buffered = 0;
            _counter++;
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            EnsureHeader();
            // Flush whatever is buffered, then a terminator frame. A file that stops before the
            // terminator is a truncation, and decrypt refuses it.
            if (_buffered > 0) WriteFrame(isFinal: false);
            WriteFrame(isFinal: true);
            _inner.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Finish();
                _gcm.Dispose();
                if (!_leaveOpen) _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Finish();
            _gcm.Dispose();
            if (!_leaveOpen) await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class DecryptingStream(Stream inner, byte[] contentKey, string fileName) : Stream
    {
        private readonly AesGcm _gcm = new(contentKey, TagBytes);
        private readonly byte[] _noncePrefix = new byte[NoncePrefixBytes];
        private byte[] _plaintext = [];
        private int _offset;
        private uint _counter;
        private bool _headerRead;
        private bool _sawTerminator;

        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset >= _plaintext.Length && !ReadFrame()) return 0;

            var take = Math.Min(count, _plaintext.Length - _offset);
            _plaintext.AsSpan(_offset, take).CopyTo(buffer.AsSpan(offset, take));
            _offset += take;
            return take;
        }

        private void EnsureHeader()
        {
            if (_headerRead) return;

            var magic = new byte[Magic.Length];
            ReadExactly(magic, "header");
            if (!magic.AsSpan().SequenceEqual(Magic))
                throw new InvalidOperationException(
                    $"Backup file '{fileName}' is not in the expected encrypted format.");

            ReadExactly(_noncePrefix, "nonce prefix");
            _headerRead = true;
        }

        /// <summary>Reads the next frame into the plaintext buffer. False once the terminator is
        /// consumed.</summary>
        private bool ReadFrame()
        {
            EnsureHeader();
            if (_sawTerminator) return false;

            var lengthPrefix = new byte[4];
            ReadExactly(lengthPrefix, "frame length");
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
            if (length < 0 || length > FrameSize)
                throw new InvalidOperationException($"Backup file '{fileName}' has a malformed frame length.");

            var ciphertext = new byte[length];
            ReadExactly(ciphertext, "frame body");
            var tag = new byte[TagBytes];
            ReadExactly(tag, "frame tag");

            Span<byte> nonce = stackalloc byte[NonceBytes];
            FillNonce(nonce, _noncePrefix, _counter);

            var plaintext = new byte[length];

            // A frame authenticates as final or non-final, never both — so a truncated file cannot be
            // passed off as a complete one, and a complete one cannot have frames appended.
            var isFinal = TryDecrypt(nonce, ciphertext, tag, plaintext, isFinal: false)
                ? false
                : TryDecrypt(nonce, ciphertext, tag, plaintext, isFinal: true)
                    ? true
                    : throw new InvalidOperationException(
                        $"Backup file '{fileName}' failed authentication at frame {_counter}: it has been " +
                        "truncated, reordered, or modified, or the wrong key was supplied.");

            _plaintext = plaintext;
            _offset = 0;
            _counter++;
            if (isFinal)
            {
                _sawTerminator = true;
                // Trailing bytes after the terminator would mean something was appended.
                if (inner.ReadByte() != -1)
                    throw new InvalidOperationException(
                        $"Backup file '{fileName}' has data after its final frame.");
            }

            return length > 0 || !isFinal;
        }

        private bool TryDecrypt(ReadOnlySpan<byte> nonce, byte[] ciphertext, byte[] tag, byte[] plaintext, bool isFinal)
        {
            try
            {
                _gcm.Decrypt(nonce, ciphertext, tag, plaintext, FrameAad(fileName, _counter, isFinal));
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        private void ReadExactly(Span<byte> buffer, string what)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = inner.Read(buffer[read..]);
                if (n == 0)
                    throw new InvalidOperationException(
                        $"Backup file '{fileName}' ended while reading its {what} — the file is truncated.");
                read += n;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gcm.Dispose();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
