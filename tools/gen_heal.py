"""生成维修/回血音效（纯合成：柔和的三音上行铃音）。

用法：
    python tools/gen_heal.py out.wav
    ffmpeg -y -i out.wav -c:a libvorbis -q:a 5 gameAsset/audios/sfx/heal.ogg
"""

import sys
import wave

import numpy as np

SR = 44100


def bell(t: np.ndarray, freq: float, decay: float, start: float = 0.0) -> np.ndarray:
    """带包络的铃音（基频 + 二次谐波 + 轻微失谐）。"""
    local = np.maximum(t - start, 0.0)
    gate = (t >= start).astype(np.float64)
    env = np.exp(-local * decay) * np.minimum(local / 0.008, 1.0) * gate

    w = 2 * np.pi * freq * local
    return (np.sin(w) + 0.35 * np.sin(2.02 * w) + 0.16 * np.sin(3.01 * w)) * env


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else "heal.wav"
    dur = 0.85
    n = int(SR * dur)
    t = np.arange(n) / SR

    # 上行三音（C6 → E6 → G6），依次错开，像"叮-咚-叮"的治愈感
    mix = np.zeros(n)
    mix += bell(t, 1046.5, 6.5, 0.00) * 0.55
    mix += bell(t, 1318.5, 7.0, 0.07) * 0.42
    mix += bell(t, 1568.0, 7.5, 0.14) * 0.35

    # 柔和的空气感底噪（很轻，避免太"电子"）
    rng = np.random.default_rng(3)
    air = rng.normal(0.0, 1.0, n)
    prev = 0.0
    alpha = 0.02
    for i in range(n):
        prev += alpha * (air[i] - prev)
        air[i] = prev
    mix += air * np.exp(-t * 2.5) * 0.35

    # 归一化并做一点柔化
    mix = np.tanh(mix * 1.1) * 0.85
    mix *= np.exp(-t * 1.6)
    mix = mix / max(1e-6, np.max(np.abs(mix))) * 0.85

    pcm = (mix * 32767.0).astype(np.int16)
    with wave.open(out, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())

    print(f"wrote {out}: {dur:.2f}s @ {SR}Hz")


if __name__ == "__main__":
    main()
