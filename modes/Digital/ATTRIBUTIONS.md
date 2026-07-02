# Zeus Digital · FT8/FT4 — Provenance and Attributions

This file states the provenance of the third-party code the
`com.kb2uka.digital` plugin builds on. It moved here from the Zeus core
`ATTRIBUTIONS.md` together with the FT8/FT4/WSPR digital-mode engine itself;
the wording is preserved so the licence-obligation chain stays auditable.

The plugin is distributed under the **GNU General Public License, version 2
or (at your option) any later version** (GPL-2.0-or-later), the same licence
as Zeus itself. Every first-party source file carries the
`SPDX-License-Identifier: GPL-2.0-or-later` tag plus a short-form copyright
and attribution block.

## ft8_lib (FT8/FT4 decode + encode)

The plugin's native FT8/FT4 digital-mode core links against **ft8_lib**
(Kārlis Goba), vendored in-tree under [`native/ft8/vendor/`](native/ft8/vendor/)
and wrapped by `native/ft8/zeus_ft8.c` in the stable `zeus_ft8_*` C ABI that
the managed `Dsp/` P/Invoke layer binds against. It builds as
`libzeus_ft8.{so,dll,dylib}` with hidden symbol visibility so only the
`zeus_ft8_*` exports surface.

ft8_lib is **Copyright (c) 2018 Kārlis Goba** and is distributed under the
**MIT License**. The full licence text is preserved verbatim at
[`native/ft8/vendor/LICENSE`](native/ft8/vendor/LICENSE).

ft8_lib is an **independent, clean-room implementation** of the FT8/FT4
protocols written from the published specification — it is **not** derived
from WSJT-X or JTDX (which are GPL Fortran/Qt applications). The protocol
constants it reproduces (the LDPC(174,91) parity matrix, the three Costas 7×7
sync arrays, the CRC-14 polynomial, and the 77-bit message packing) were
placed in the **public domain** by the protocol authors in *"The FT4 and FT8
Communication Protocols"* (Franke, Somerville, Taylor — QEX, 2020), so their
reproduction under the MIT licence is legitimate. Every conformant FT8
implementation necessarily shares these constants, because they define the
over-the-air signal.

ft8_lib bundles its own **KISS FFT** (Mark Borgerding, BSD-3-Clause, under
[`native/ft8/vendor/fft/`](native/ft8/vendor/fft/)); the FT8 path therefore
has no FFTW dependency. MIT and BSD-3-Clause are both one-way
licence-compatible with this plugin's GPL-2.0-or-later distribution. The
vendored ft8_lib `ft8/`, `common/`, and `fft/` sources are unmodified;
per-file headers are preserved as received from upstream and must remain so
on re-vendor.

Upstream:
- <https://github.com/kgoba/ft8_lib>
- FT4/FT8 protocol paper — <https://wsjt.sourceforge.io/FT4_FT8_QEX.pdf>

## wsprd (WSPR encode + decode)

The plugin's native WSPR core vendors the **WSPR encoder and decoder**
in-tree under [`native/wspr/vendor/`](native/wspr/vendor/), pinned from
**pavel-demin/wsprd** (the minimal build-able extract of the WSJT-X `wsprd`)
at commit `8aa903085479910c77de95f7e7c178f66a245ed3`.

Unlike FT8 — where the clean-room MIT `ft8_lib` exists — **no permissively
licensed WSPR decoder exists anywhere**; the canonical K1JT/K9AN demodulator
(4-FSK sync search + K=32 r=1/2 convolutional FEC via a Fano/Jelinek sequential
decoder) is the only working implementation, and its algorithm was never placed
in the public domain the way the FT4/FT8 protocol was. The decoder
(`wsprd.c`, `wsprd_utils.c`, `fano.c`, `jelinek.c`, `nhash.c`, `tab.c`,
`metric_tables.c`) and the encoder (`wsprsim_utils.c`) are **Copyright
2001-2018 Joe Taylor (K1JT) and Steven Franke (K9AN)**, distributed under the
**GNU General Public License v3**.

GPL-3 is one-way licence-compatible with this plugin's GPL-2.0-or-later
distribution, so vendoring it is consistent with both licences — the combined
work is governed by GPLv3, which the "or later" permits. This is the single
component of WSJT-X decoder lineage in the plugin; it is used because a
clean-room permissive WSPR decoder does not exist and reimplementing the Fano
demodulator from scratch carries no benefit (the on-air format is standardised
regardless).

The decoder bundles **pffft** (Julien Pommier's "pretty fast FFT", FFTPACK-style
permissive licence) instead of FFTW, so the WSPR path has no external FFT
dependency. The vendored source is unmodified; all Zeus-specific glue lives in
the `zeus_wspr` shim. See [`native/wspr/README.md`](native/wspr/README.md).

Upstream:
- <https://github.com/pavel-demin/wsprd>
- WSJT-X (decoder lineage) — <https://wsjt.sourceforge.io/>
