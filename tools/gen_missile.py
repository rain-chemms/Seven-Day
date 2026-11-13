"""生成导弹飞行音效（火箭喷流的持续轰鸣 + 一点呼啸）。

用法：
    python tools/gen_missile.py out.wav
    ffmpeg -y -i out.wav -c:a libvorbis -q:a 5 gameAsset/audios/sfx/missile_fly.ogg
"""

import sys
import wave

import numpy as np

SR = 44100


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else "missile_fly.wav"
    dur = 1.5
    n = int(SR * dur)
    t = np.arange(n) / SR
    rng = np.random.default_rng(11)

    def lowpass(sig, cutoff):
        alpha = np.clip(2 * np.pi * np.asarray(cutoff, dtype=np.float64) / SR, 0.0, 1.0)
        alpha = np.broadcast_to(alpha, sig.shape)
        out_sig = np.zeros_like(sig)
        prev = 0.0
        for i in range(len(sig)):
            prev += alpha[i] * (sig[i] - prev)
            out_sig[i] = prev
        return out_sig

    noise = rng.normal(0.0, 1.0, n)

    # 喷流轰鸣：低频噪声，截止频率随"推上去"略微升高
    cutoff = np.interp(t, [0.0, 0.25, dur], [500.0, 900.0, 600.0])
    rumble = lowpass(noise, cutoff)
    rumble /= max(1e-6, np.max(np.abs(rumble)))

    # 呼啸：窄一点的高频噪声，音高感来自带通
    hiss = noise - lowpass(noise, 2500.0)
    hiss /= max(1e-6, np.max(np.abs(hiss)))

    # 包络：快速起音、缓慢衰减，尾巴略收
    attack = np.minimum(t / 0.05, 1.0)
    decay = np.interp(t, [0.0, 1.0, dur], [1.0, 0.75, 0.0])
    env = attack * decay

    mix = rumble * 0.85 * env + hiss * 0.35 * env

    # 低频"推力"感
    mix += 0.25 * np.sin(2 * np.pi * 70.0 * t) * env

    mix = np.tanh(mix * 1.2) * 0.85

    # 首尾各做 20ms 淡入淡出，避免爆音
    fade = int(0.02 * SR)
    mix[:fade] *= np.linspace(0, 1, fade)
    mix[-fade:] *= np.linspace(1, 0, fade)

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
