using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace YGORogueDuelistCardModInstaller.Core;

/// <summary>
/// Pure-C# Native AOT implementation of Yu-Gi-Oh! Rogue Duelist's YGODLT01 delta patch format
/// (matching tools/android_delta.py, tools/windows_release.py, tools/linux_release.py, and UpdateDelta.java).
/// Reconstructs byte-for-byte identical web-X.Y.Z.zip or signed APK archives from ~5 MB delta patches
/// instead of re-downloading ~228 MB full archives!
/// </summary>
public static class DeltaPatchService
{
    private static readonly byte[] Magic = "YGODLT01"u8.ToArray();
    private const long MaxReconstructedBytes = 1_500_000_000L;
    private const int MaxRecords = 500_000;

    public static async Task<string> ApplyDeltaAsync(
        string baseZipPath,
        string deltaGzPath,
        string targetZipPath,
        string? expectedTargetSha256 = null,
        IInstallerInteraction? interaction = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(baseZipPath))
            throw new ModOperationException($"找不到基准文件：{baseZipPath}");
        if (!File.Exists(deltaGzPath))
            throw new ModOperationException($"找不到增量补丁文件：{deltaGzPath}");

        var dir = Path.GetDirectoryName(targetZipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempTarget = targetZipPath + ".delta.tmp";
        var success = false;

        try
        {
            await using var rawPatch = new FileStream(deltaGzPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            await using var gz = new GZipStream(rawPatch, CompressionMode.Decompress);
            await using var baseFile = new FileStream(baseZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);

            // Header: 8-byte MAGIC + uint64 BE oldSize + 32-byte oldHash + uint64 BE newSize + 32-byte newHash = 88 bytes
            var header = new byte[88];
            await ReadExactlyAsync(gz, header, ct);

            if (!header.AsSpan(0, 8).SequenceEqual(Magic))
                throw new ModOperationException("增量补丁格式不受支持（缺少 YGODLT01 魔数头）。");

            var oldSize = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
            var oldHash = header.AsSpan(16, 32).ToArray();
            var newSize = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(48, 8));
            var newHash = header.AsSpan(56, 32).ToArray();
            var newHashHex = Convert.ToHexStringLower(newHash);

            if (newSize <= 0 || newSize > MaxReconstructedBytes)
                throw new ModOperationException($"增量补丁目标大小异常：{newSize} 字节。");

            if (baseFile.Length != oldSize)
                throw new ModOperationException($"本地基准包大小 ({baseFile.Length}) 与补丁要求 ({oldSize}) 不匹配。");

            if (!string.IsNullOrWhiteSpace(expectedTargetSha256) &&
                !newHashHex.Equals(expectedTargetSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new ModOperationException("增量补丁目标 SHA-256 与 GitHub Release 校验值不一致。");
            }

            interaction?.ReportProgress("正在校验本地基准包 SHA-256...", 80);
            var actualOldHash = await ComputeSha256BytesAsync(baseZipPath, ct);
            if (!actualOldHash.AsSpan().SequenceEqual(oldHash))
                throw new ModOperationException("本地基准包 SHA-256 与增量补丁不匹配（可能已修改或版本不符）。");

            await using (var outStream = new FileStream(tempTarget, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[262144];
                var opBuf = new byte[1];
                var copyHeader = new byte[12];
                var litHeader = new byte[4];
                long written = 0;
                var lastReport = DateTime.MinValue;

                for (var rec = 0; ; rec++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (rec > MaxRecords)
                        throw new ModOperationException("增量补丁记录数超过安全上限。");

                    await ReadExactlyAsync(gz, opBuf, ct);
                    var op = opBuf[0];
                    if (op == 2) break; // EOF

                    if (op != 0 && op != 1)
                        throw new ModOperationException($"增量补丁包含未知操作码：0x{op:X2}");

                    long offset = 0;
                    int length;
                    if (op == 0)
                    {
                        await ReadExactlyAsync(gz, copyHeader, ct);
                        offset = (long)BinaryPrimitives.ReadUInt64BigEndian(copyHeader.AsSpan(0, 8));
                        length = (int)BinaryPrimitives.ReadUInt32BigEndian(copyHeader.AsSpan(8, 4));
                        if (length <= 0 || offset < 0 || offset > oldSize - length || length > newSize - written)
                            throw new ModOperationException("增量补丁复制边界越界。");
                        baseFile.Seek(offset, SeekOrigin.Begin);
                    }
                    else
                    {
                        await ReadExactlyAsync(gz, litHeader, ct);
                        length = (int)BinaryPrimitives.ReadUInt32BigEndian(litHeader);
                        if (length <= 0 || length > newSize - written)
                            throw new ModOperationException("增量补丁字面量长度越界。");
                    }

                    var remaining = length;
                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        var count = Math.Min(remaining, buffer.Length);
                        if (op == 0)
                            await ReadExactlyAsync(baseFile, buffer.AsMemory(0, count), ct);
                        else
                            await ReadExactlyAsync(gz, buffer.AsMemory(0, count), ct);

                        await outStream.WriteAsync(buffer.AsMemory(0, count), ct);
                        sha.AppendData(buffer, 0, count);
                        remaining -= count;
                        written += count;

                        if (interaction is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 150)
                        {
                            var pct = 82 + Math.Clamp(written * 14.0 / newSize, 0, 14);
                            interaction.ReportProgress(
                                $"正在应用增量补丁合成目标包 ({written / (1024.0 * 1024.0):0.1}/{newSize / (1024.0 * 1024.0):0.1} MB)...",
                                pct);
                            lastReport = DateTime.UtcNow;
                        }
                    }
                }

                if (written != newSize)
                    throw new ModOperationException($"增量补丁合成大小不匹配（期望 {newSize}，实际 {written}）。");

                var actualNewHash = sha.GetHashAndReset();
                if (!actualNewHash.AsSpan().SequenceEqual(newHash))
                    throw new ModOperationException("增量补丁合成结果 SHA-256 校验失败。");

                await outStream.FlushAsync(ct);
            }

            if (File.Exists(targetZipPath)) File.Delete(targetZipPath);
            File.Move(tempTarget, targetZipPath);
            success = true;
            return newHashHex;
        }
        finally
        {
            if (!success)
            {
                try { if (File.Exists(tempTarget)) File.Delete(tempTarget); } catch { }
            }
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], ct);
            if (n == 0) throw new EndOfStreamException("Unexpected end of YGODLT01 stream.");
            total += n;
        }
    }

    private static async Task<byte[]> ComputeSha256BytesAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        using var sha = SHA256.Create();
        return await sha.ComputeHashAsync(fs, ct);
    }
}
