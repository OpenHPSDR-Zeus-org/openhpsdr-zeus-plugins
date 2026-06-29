# Voyeur Mode · Net Monitor — Third-Party Attributions

Voyeur is GPL-2.0-or-later. It downloads optional speech/AI engines and models on
first use (they are NOT bundled in the plugin zip). Their licenses and required
attributions are listed here.

## Speech-to-text engines

- **whisper.cpp** — MIT License. © Georgi Gerganov and contributors.
  https://github.com/ggerganov/whisper.cpp
- **Whisper ggml models** (`ggml-small.en`, `ggml-medium.en`) — MIT License.
  https://huggingface.co/ggerganov/whisper.cpp
- **sherpa-onnx** (next-gen Kaldi / k2-fsa) — Apache-2.0 License. © Xiaomi Corporation
  and the k2-fsa authors. https://github.com/k2-fsa/sherpa-onnx

## Speech-to-text models (Parakeet engine)

- **NVIDIA Parakeet-TDT-0.6B-v2** — licensed under **CC-BY-4.0**.
  Source model: https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2
  ONNX / int8 conversion by the sherpa-onnx project.
  https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8
  > "NVIDIA Parakeet-TDT-0.6B-v2 (https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2),
  > ONNX/int8 conversion by Xiaomi sherpa-onnx, licensed under CC-BY-4.0
  > (https://creativecommons.org/licenses/by/4.0/)."

## Voice activity detection

- **Silero VAD** — MIT License. © Silero Team.
  https://github.com/snakers4/silero-vad

## Digest / summarisation

- **llama.cpp** — MIT License. © Georgi Gerganov and contributors.
  https://github.com/ggerganov/llama.cpp
- **Qwen2.5-0.5B / 1.5B Instruct (GGUF)** — Apache-2.0 License. © Alibaba Cloud / Qwen team.
  https://huggingface.co/Qwen

## Derived source

- `BandUtils.cs` — ham-band edge data derived from **pihpsdr** (dl1ycf) and
  **deskhpsdr** (DL1BZ), both GPL. See the header in `BandUtils.cs`.
