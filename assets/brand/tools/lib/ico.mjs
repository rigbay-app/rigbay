// ICO packer: PNG-compressed entries (Vista+ and every Windows 10/11 shell path). Entries: [{ size, png: Buffer }].
export function packIco(entries) {
  const list = [...entries].sort((a, b) => a.size - b.size);
  const head = Buffer.alloc(6 + 16 * list.length);
  head.writeUInt16LE(0, 0); head.writeUInt16LE(1, 2); head.writeUInt16LE(list.length, 4);
  let off = head.length;
  list.forEach((e, i) => {
    const o = 6 + 16 * i, s = e.size >= 256 ? 0 : e.size;
    head[o] = s; head[o + 1] = s; head[o + 2] = 0; head[o + 3] = 0;
    head.writeUInt16LE(1, o + 4); head.writeUInt16LE(32, o + 6);
    head.writeUInt32LE(e.png.length, o + 8); head.writeUInt32LE(off, o + 12);
    off += e.png.length;
  });
  return Buffer.concat([head, ...list.map((e) => e.png)]);
}

/** Read back an ICO: [{ w, h, bpp, bytes, isPng }] (used by the verification step). */
export function readIco(buf) {
  if (buf.readUInt16LE(0) !== 0 || buf.readUInt16LE(2) !== 1) throw new Error('not an ICO');
  const n = buf.readUInt16LE(4), out = [];
  for (let i = 0; i < n; i++) {
    const o = 6 + 16 * i, bytes = buf.readUInt32LE(o + 8), off = buf.readUInt32LE(o + 12);
    const isPng = buf.subarray(off, off + 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
    const w = buf[o] || 256, h = buf[o + 1] || 256;
    out.push({ w, h, bpp: buf.readUInt16LE(o + 6), bytes, isPng, pngW: isPng ? buf.readUInt32BE(off + 16) : null, pngH: isPng ? buf.readUInt32BE(off + 20) : null });
  }
  return out;
}
