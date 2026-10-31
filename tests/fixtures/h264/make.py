"""Makes the synthetic H.264 fixtures in this folder (see README.md). No guest content: every frame is drawn here.

Usage: python -I make.py <ffmpeg.exe>   (an FFmpeg build with libx264; output goes next to this script)

The picture is the one Spice.Video.Tests' TestClip draws: four flat quadrants and a light 32x32 block that moves 8
pixels right per frame. x264 runs as spice-server's x264enc does (server/gstreamer-encoder.c): Annex B with access unit
delimiters, ultrafast, zerolatency, sliced threads, periodic intra refresh instead of IDR frames.
"""
import pathlib
import subprocess
import sys

W, H, BLOCK, TOP, STEP = 320, 240, 32, 8, 8
QUADRANTS = [(200, 40, 40), (40, 180, 60), (50, 60, 200), (128, 128, 128)]
LIGHT = (235, 235, 235)


def frame(i, w=W, h=H):
    bx = i * STEP % (w - BLOCK)
    rows = bytearray()
    for y in range(h):
        for x in range(w):
            if TOP <= y < TOP + BLOCK and bx <= x < bx + BLOCK:
                rows += bytes(LIGHT)
            else:
                rows += bytes(QUADRANTS[(0 if y < h // 2 else 2) + (0 if x < w // 2 else 1)])
    return bytes(rows)


def encode(ffmpeg, out, frames, pix_fmt, w=W, h=H):
    # BT.601 studio range, not signalled in the stream (unspecified), as the decoder then assumes below 720 lines.
    args = [ffmpeg, '-hide_banner', '-loglevel', 'error', '-y', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-s', f'{w}x{h}',
            '-r', '30', '-i', '-', '-vf', 'scale=out_color_matrix=bt601:out_range=tv:flags=accurate_rnd+full_chroma_int',
            '-pix_fmt', pix_fmt, '-c:v', 'libx264', '-preset', 'ultrafast', '-tune', 'zerolatency', '-qp', '12',
            '-x264-params', 'aud=1:intra-refresh=1:sliced-threads=1:threads=4:keyint=30',
            '-f', 'h264', str(out)]
    data = b''.join(frame(i, w, h) for i in range(frames))
    subprocess.run(args, input=data, check=True)
    print(out.name, out.stat().st_size, 'bytes')


def main():
    ffmpeg = sys.argv[1]
    here = pathlib.Path(__file__).resolve().parent
    encode(ffmpeg, here / 'clip-444.h264', 30, 'yuv444p')
    encode(ffmpeg, here / 'clip-420.h264', 30, 'yuv420p')
    encode(ffmpeg, here / 'clip-444-10bit.h264', 4, 'yuv444p10le', 64, 48)


main()
