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

namespace Zeus.Server.Voyeur;

/// <summary>
/// Settings for the training-corpus retention module. Lives in its OWN file so
/// it never collides with other modules' settings records.
///
/// <para><see cref="RetainCorpus"/> defaults to <c>false</c>: banking
/// operator-confirmed over audio + transcripts for a future ham ASR fine-tune is
/// a deliberate, opt-in act. Nothing is written to disk until the operator turns
/// it on.</para>
///
/// <para><see cref="MaxClips"/> bounds disk use: when the bank exceeds the cap,
/// the oldest clips (audio + their index lines) are pruned, the same rolling
/// retention discipline <c>VoyeurStore</c> applies to sessions.</para>
/// </summary>
public sealed record CorpusSettings(
    bool RetainCorpus = false,
    int MaxClips = 5000);
