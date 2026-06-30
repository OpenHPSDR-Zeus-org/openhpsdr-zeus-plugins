// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

using System.Buffers.Binary;

namespace Zeus.Server.Voyeur.Alerts;

/// <summary>
/// Builds a SMALL, attachable clip from a captured over for Watchword Alerts.
/// Reuses <see cref="WhisperWav"/>'s dependency-free 16 kHz mono resampler (the
/// same one ASR uses) — NO new native dependency — then caps the result to a few
/// seconds by truncating the WAV's data chunk so an alert email/SMS attachment
/// stays tiny regardless of how long the over ran.
///
/// The operator's original recording is never touched: resampling produces a
/// throw-away temp, and the capped clip is written to its own temp file which the
/// caller deletes after the alert is delivered. Format-agnostic: truncation is a
/// byte-aligned cut of the data chunk, so it works whatever rate/bit-depth the
/// source happens to be.
/// </summary>
internal static class AlertClipMaker
{
    /// <summary>
    /// Returns a path to a temp WAV no longer than <paramref name="maxSeconds"/>,
    /// down-converted to 16 kHz mono. The CALLER owns the file and must delete it.
    /// Returns null when there is no usable source audio (so the alert just goes
    /// out without an attachment).
    /// </summary>
    public static string? TryMake(string? sourceWavPath, int maxSeconds)
    {
        if (string.IsNullOrEmpty(sourceWavPath) || maxSeconds <= 0 || !File.Exists(sourceWavPath))
            return null;

        string? prepared = null;
        bool createdTemp = false;
        try
        {
            // 16 kHz mono via the shared resampler (no ffmpeg/sox, no native dep).
            prepared = WhisperWav.Prepare(sourceWavPath, out createdTemp);
            if (prepared is null || !File.Exists(prepared)) return null;

            byte[] wav = File.ReadAllBytes(prepared);
            if (!TryLocateData(wav, out int dataOff, out int dataLen, out int byteRate, out int blockAlign))
                return null;

            int capBytes = CapBytes(dataLen, byteRate, blockAlign, maxSeconds);

            // Already within the cap and we own a temp copy → hand it straight on.
            if (capBytes >= dataLen && createdTemp)
            {
                var passthrough = prepared;
                prepared = null;       // ownership transfers to caller; don't delete in finally
                createdTemp = false;
                return passthrough;
            }

            // Write a fresh capped clip: copy the header up to the data payload,
            // patch the two RIFF size fields, then copy only the capped bytes.
            var outPath = Path.Combine(
                Path.GetTempPath(), "zeus-voyeur-clip-" + Guid.NewGuid().ToString("N") + ".wav");
            WriteTruncated(wav, dataOff, capBytes, outPath);
            return outPath;
        }
        catch
        {
            return null; // a missing clip must never block the alert
        }
        finally
        {
            if (createdTemp && prepared is not null)
                try { if (File.Exists(prepared)) File.Delete(prepared); } catch { /* best effort */ }
        }
    }

    /// <summary>Bytes to keep: the largest block-aligned span not exceeding both
    /// the source length and <paramref name="maxSeconds"/> of audio. Pure.</summary>
    internal static int CapBytes(int dataLen, int byteRate, int blockAlign, int maxSeconds)
    {
        if (dataLen <= 0) return 0;
        if (blockAlign <= 0) blockAlign = 1;
        long want = byteRate > 0 ? (long)byteRate * Math.Max(0, maxSeconds) : dataLen;
        long capped = Math.Min(want, dataLen);
        capped -= capped % blockAlign;            // never cut mid-frame
        if (capped <= 0) capped = Math.Min(dataLen, blockAlign);
        return (int)capped;
    }

    // Walk the RIFF chunks for fmt (byteRate / blockAlign) + the data payload.
    private static bool TryLocateData(
        byte[] b, out int dataOff, out int dataLen, out int byteRate, out int blockAlign)
    {
        dataOff = dataLen = byteRate = blockAlign = 0;
        if (b.Length < 12 ||
            b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' ||
            b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            return false;

        int p = 12;
        bool haveFmt = false, haveData = false;
        while (p + 8 <= b.Length)
        {
            var id = b.AsSpan(p, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 4, 4));
            int body = p + 8;
            if (size < 0 || body + size > b.Length) size = b.Length - body;

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(body + 8, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 12, 2));
                haveFmt = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataOff = body;
                dataLen = size;
                haveData = true;
            }
            p = body + size + (size & 1);
        }
        return haveFmt && haveData && dataOff > 0;
    }

    // Emit a valid WAV = original bytes up to the data payload (header + fmt +
    // any leading chunks) with the RIFF/data sizes patched, then capBytes of
    // audio. We rewrite the trailing "data" size field that sits at dataOff-4.
    private static void WriteTruncated(byte[] src, int dataOff, int capBytes, string outPath)
    {
        int headerLen = dataOff;                 // everything up to and incl. "data"+size
        var outBuf = new byte[headerLen + capBytes];
        Array.Copy(src, 0, outBuf, 0, headerLen);
        Array.Copy(src, dataOff, outBuf, headerLen, capBytes);

        // Patch RIFF chunk size (offset 4) and data chunk size (dataOff-4).
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(4, 4), (uint)(outBuf.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(dataOff - 4, 4), (uint)capBytes);

        File.WriteAllBytes(outPath, outBuf);
    }
}
