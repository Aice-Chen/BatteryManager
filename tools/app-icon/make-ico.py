"""Write a multi-frame .ico from hand-drawn PNG frames: 32-bit DIB frames below 256 px, a PNG frame at 256.
usage: python make-ico.py <frames-dir> <out.ico>"""
import struct
import sys
from PIL import Image

SIZES = [16, 20, 24, 28, 30, 32, 36, 40, 48, 56, 60, 64, 72, 80, 96, 128, 256]


def dib(im):
    w, h = im.size
    px = im.load()
    xor = bytearray()
    for y in range(h - 1, -1, -1):              # bottom-up rows, BGRA, straight (not premultiplied) alpha
        for x in range(w):
            r, g, b, a = px[x, y]
            xor += bytes((b, g, r, a))
    stride = ((w + 31) // 32) * 4               # 1-bit AND mask, rows padded to 32 bits; 1 = transparent
    mask = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray(stride)
        for x in range(w):
            if px[x, y][3] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask += row
    header = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, len(xor) + len(mask), 0, 0, 0, 0)
    return header + bytes(xor) + bytes(mask)


def main(frames, out):
    blobs = []
    for s in SIZES:
        im = Image.open(f'{frames}/{s}.png').convert('RGBA')
        assert im.size == (s, s), (s, im.size)
        if s >= 256:
            with open(f'{frames}/{s}.png', 'rb') as f:
                data = f.read()
        else:
            data = dib(im)
        blobs.append((s, data))
    offset = 6 + 16 * len(blobs)
    head = struct.pack('<HHH', 0, 1, len(blobs))
    entries, body = b'', b''
    for s, data in blobs:
        dim = 0 if s >= 256 else s
        entries += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(data), offset + len(body))
        body += data
    with open(out, 'wb') as f:
        f.write(head + entries + body)


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
