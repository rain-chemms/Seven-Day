"""生成爆炸音效（无外部素材依赖，纯合成）。

用法：
    python tools/gen_explosion.py out.wav
然后用 ffmpeg 转成 ogg（Godot 工程里用的是 ogg）：
    ffmpeg -y -i out.wav -c:a libvorbis -q:a 5 gameAsset/audios/sfx/explosion.ogg

风格取向：低沉的"轰"+ 短促的噪声冲击 + 一点饱和度，配合 3渲2 卡通战场。
"""

import sys
import wave

import numpy as np

SR = 44100


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else "explosion.wav"
    dur = 1.05
    n = int(SR * dur)
    t = np.arange(n) / SR

    rng = np.random.default_rng(7)

    # ── 低频"轰"：90Hz 滑到 42Hz，指数衰减
    f0, f1 = 92.0, 42.0
    sweep = np.cumsum(np.linspace(f0, f1, n) / SR) * 2 * np.pi
    boom = np.sin(sweep) * np.exp(-t * 3.6) * 1.7
    boom += 0.55 * np.sin(sweep * 0.5) * np.exp(-t * 2.4)

    # ── 噪声冲击：白噪声 + 两级低通（12dB/oct，截止频率随时间下降），前 60ms 最猛
    noise = rng.normal(0.0, 1.0, n)
    cutoff = np.interp(t, [0.0, 0.06, 0.30, dur], [950.0, 650.0, 320.0, 120.0])
    alpha = np.clip(2 * np.pi * cutoff / SR, 0.0, 1.0)
    low = np.zeros(n)
    prev = 0.0
    for i in range(n):
        prev += alpha[i] * (noise[i] - prev)
        low[i] = prev

    # 第二级再滤一次，压掉高频，做出真正的"闷响"
    low2 = np.zeros(n)
    prev = 0.0
    for i in range(n):
        prev += alpha[i] * (low[i] - prev)
        low2[i] = prev

    attack = np.minimum(t / 0.006, 1.0)
    decay = np.exp(-t * np.interp(t, [0.0, 0.25, dur], [5.0, 6.5, 4.5]))
    body = low2 * attack * decay * 6.0

    # ── 起爆的"啪"：很短的高频噪声（只留一点点，别太刺耳）
    click = rng.normal(0.0, 1.0, n) * np.exp(-t * 150.0) * 0.09

    mix = boom + body + click

    # ── 轻微饱和 + 整体衰减，做出"卡通但有分量"的质感
    mix = np.tanh(mix * 1.6) * 0.85
    tail = np.exp(-t * 2.0)
    mix *= tail

    # 归一化，留 1dB 余量
    mix = mix / max(1e-6, np.max(np.abs(mix))) * 0.89

    pcm = (mix * 32767.0).astype(np.int16)
    with wave.open(out, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())

    print(f"wrote {out}: {dur:.2f}s @ {SR}Hz")


if __name__ == "__main__":
    main()
