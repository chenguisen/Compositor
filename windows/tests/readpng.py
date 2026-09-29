"""Read a PNG the way a check needs to, and say where its pixels differ.

The port's UI checks render the canvas control to a PNG (`Compositor.Desktop --render`); this
reads one back without a dependency, so a drawing can be checked from a command line. It
handles every PNG filter and either channel order, because what writes the file is Skia and
not always this port's own writer.

    python tests/readpng.py <file.png> [x y]     what the file is, and what sits at x, y
    python tests/readpng.py <a.png> <b.png>      how two renders of the same size differ
"""
import struct
import sys
import zlib
from collections import Counter


def decompress(idat):
    return zlib.decompress(idat)


def read_png(path):
    data = open(path, 'rb').read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n', 'not a PNG'
    at, idat, header = 8, b'', None
    while at < len(data):
        length = struct.unpack('>I', data[at:at + 4])[0]
        kind = data[at + 4:at + 8]
        body = data[at + 8:at + 8 + length]
        if kind == b'IHDR':
            header = struct.unpack('>IIBBBBB', body)
        elif kind == b'IDAT':
            idat += body
        at += 12 + length
    width, height, depth, colour, *_ = header
    assert depth == 8, depth
    channels = {0: 1, 2: 3, 4: 2, 6: 4}[colour]
    raw = decompress(idat)
    stride = width * channels
    rows, previous, at, step = [], bytearray(stride), 0, channels
    for _ in range(height):
        filt = raw[at]
        line = bytearray(raw[at + 1:at + 1 + stride])
        if filt == 1:
            for i in range(step, stride):
                line[i] = (line[i] + line[i - step]) & 0xFF
        elif filt == 2:
            for i in range(stride):
                line[i] = (line[i] + previous[i]) & 0xFF
        elif filt == 3:
            for i in range(stride):
                left = line[i - step] if i >= step else 0
                line[i] = (line[i] + ((left + previous[i]) >> 1)) & 0xFF
        elif filt == 4:
            for i in range(stride):
                left = line[i - step] if i >= step else 0
                up = previous[i]
                upleft = previous[i - step] if i >= step else 0
                guess = left + up - upleft
                pa, pb, pc = abs(guess - left), abs(guess - up), abs(guess - upleft)
                nearest = left if (pa <= pb and pa <= pc) else (up if pb <= pc else upleft)
                line[i] = (line[i] + nearest) & 0xFF
        assert 0 <= filt <= 4, filt
        rows.append(line)
        previous = line
        at += 1 + stride
    return width, height, channels, rows


def size(path):
    width, height, channels, rows = read_png(path)
    return width, height, channels, rows


def show(path, at):
    width, height, channels, rows = size(path)
    print(f'{path}: {width}x{height}, {channels} channels')
    if at:
        x, y = at
        pixel = tuple(rows[y][x * channels:x * channels + 3])
        print(f'at {x},{y}: {pixel}')


def compare(first, second):
    width, height, channels, rows = size(first)
    other = size(second)
    assert (width, height, channels) == other[:3], 'the two renders are not the same size'
    others = other[3]
    columns, lines, total = Counter(), Counter(), 0
    for y in range(height):
        for x in range(width):
            i = x * channels
            if rows[y][i:i + 3] != others[y][i:i + 3]:
                total += 1
                columns[x] += 1
                lines[y] += 1
    print(f'{total} pixels differ')
    print('columns:', sorted(columns.items(), key=lambda pair: -pair[1])[:6])
    print('rows:', sorted(lines.items(), key=lambda pair: -pair[1])[:6])


if __name__ == '__main__':
    if len(sys.argv) == 2:
        show(sys.argv[1], None)
    elif len(sys.argv) == 3:
        compare(sys.argv[1], sys.argv[2])
    elif len(sys.argv) == 4:
        show(sys.argv[1], (int(sys.argv[2]), int(sys.argv[3])))
    else:
        print(__doc__)
        sys.exit(2)
