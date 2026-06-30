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

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Zeus.Server.Voyeur.Alerts;

/// <summary>
/// Push delivery via ntfy (free, open-source, no account needed): a plain HTTP
/// POST of the body text to <c>{ServerUrl}/{Topic}</c>, with the alert subject
/// in the <c>X-Title</c> header and an optional bearer token. No SDK, no NuGet —
/// just <see cref="HttpClient"/>.
///
/// Testability: the <see cref="HttpClient"/> is injectable, so tests pass one
/// built over a fake <see cref="HttpMessageHandler"/> and assert the URL,
/// headers and body WITHOUT a real socket.
/// </summary>
public sealed class NtfyAlertChannel : IAlertChannel
{
    public string Name => "ntfy";

    private readonly HttpClient _http;

    public NtfyAlertChannel() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }) { }

    // Test/seam ctor.
    internal NtfyAlertChannel(HttpClient http) => _http = http;

    public bool IsConfigured(AlertSettings settings)
    {
        var n = settings.EffectiveNtfy;
        return n.Enabled
            && !string.IsNullOrWhiteSpace(n.ServerUrl)
            && !string.IsNullOrWhiteSpace(n.Topic);
    }

    public async Task SendAsync(AlertPayload payload, AlertSettings settings, CancellationToken ct)
    {
        var n = settings.EffectiveNtfy;
        var url = n.ServerUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(n.Topic);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.BodyText(), Encoding.UTF8),
        };
        // ntfy reads these as the notification title / priority / tags.
        // TryAddWithoutValidation writes the value to the wire RAW, so strip
        // control chars (CR/LF/DEL) first: the Subject embeds over-the-air
        // transcript text and the DeepLink embeds an operator-supplied base URL —
        // either could otherwise inject extra headers into the request.
        req.Headers.TryAddWithoutValidation("X-Title", HeaderSafe(payload.Subject));
        req.Headers.TryAddWithoutValidation("X-Tags", "radio");
        if (!string.IsNullOrEmpty(payload.DeepLink))
            req.Headers.TryAddWithoutValidation("X-Click", HeaderSafe(payload.DeepLink));
        if (!string.IsNullOrEmpty(n.Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", n.Token);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    // Strip control characters (incl. CR/LF and DEL) so a value can never inject
    // additional HTTP headers when written via TryAddWithoutValidation.
    private static string HeaderSafe(string? s) =>
        s is null ? "" : new string(s.Where(c => c >= ' ' && c != '\u007f').ToArray());
}
