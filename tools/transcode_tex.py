"""Re-encode desktop-only (BCn / DXT) textures as ASTC 4x4, which every iOS GPU samples natively.

Without this Unity decompresses BCn textures on the CPU at load time on iOS, which fails outright for
cubemaps and BC6H ("Failed to decompress ... texture") and garbles some UI sprites (style rank letters).
ASTC 4x4 is 16 bytes per 4x4 block like BC3/BC5/BC7, so those keep their size; BC1/BC4 double.
"""
import astc_encoder
import texture2ddecoder

# Unity TextureFormat ids
DXT1, DXT5, BC6H, BC7, BC4, BC5, DXT1_CRUNCHED, DXT5_CRUNCHED = 10, 12, 24, 25, 26, 27, 28, 29
ASTC_4x4 = 48

BLOCK_BYTES = {DXT1: 8, DXT5: 16, BC6H: 16, BC7: 16, BC4: 8, BC5: 16}
DECODERS = {
    DXT1: texture2ddecoder.decode_bc1,
    DXT5: texture2ddecoder.decode_bc3,
    BC6H: texture2ddecoder.decode_bc6,   # LDR (clamped) decode; good enough for the few HDR cubemaps
    BC7: texture2ddecoder.decode_bc7,
    BC4: texture2ddecoder.decode_bc4,
    BC5: texture2ddecoder.decode_bc5,
}
TEXTURE_CLASSES = ('Texture2D', 'Cubemap')

_ctx = None


def _context():
    global _ctx
    if _ctx is None:
        cfg = astc_encoder.ASTCConfig(astc_encoder.ASTCProfile.LDR, 4, 4, block_z=1, quality=60,
                                      flags=astc_encoder.ASTCConfigFlags.USE_DECODE_UNORM8)
        _ctx = astc_encoder.ASTCContext(cfg)
    return _ctx


def _blocks(n):
    return max(1, (n + 3) // 4)


def _mip_sizes(w, h, mips):
    out = []
    for _ in range(mips):
        out.append((w, h))
        w, h = max(1, w >> 1), max(1, h >> 1)
    return out


def _bgra_to_rgba(buf):
    b = bytearray(buf)
    b[0::4], b[2::4] = b[2::4], b[0::4]
    return bytes(b)


def transcode_obj(obj):
    """Transcode one Texture2D/Cubemap ObjectReader in place. Returns True if it was changed."""
    tt = obj.read_typetree()
    fmt = tt.get('m_TextureFormat')
    if fmt in (DXT1_CRUNCHED, DXT5_CRUNCHED):
        return _transcode_crunched(obj, tt, fmt)
    if fmt not in DECODERS:
        return False
    w, h = tt['m_Width'], tt['m_Height']
    mips = max(1, tt.get('m_MipCount', 1))
    images = max(1, tt.get('m_ImageCount', 1))
    data = obj.read().get_image_data()
    sizes = _mip_sizes(w, h, mips)
    src_img = sum(_blocks(mw) * _blocks(mh) * BLOCK_BYTES[fmt] for mw, mh in sizes)
    if len(data) < src_img * images:
        raise ValueError(f"{tt.get('m_Name')}: {len(data)} bytes < {images} x {src_img}")

    decode = DECODERS[fmt]
    ctx = _context()
    swz = astc_encoder.ASTCSwizzle.from_str('RGBA')
    out = bytearray()
    pos = 0
    for _ in range(images):
        for mw, mh in sizes:
            pw, ph = _blocks(mw) * 4, _blocks(mh) * 4
            n = _blocks(mw) * _blocks(mh) * BLOCK_BYTES[fmt]
            rgba = _bgra_to_rgba(decode(bytes(data[pos:pos + n]), pw, ph))
            pos += n
            out += ctx.compress(astc_encoder.ASTCImage(astc_encoder.ASTCType.U8, pw, ph, 1, rgba), swz)

    tt['m_TextureFormat'] = ASTC_4x4
    tt['m_CompleteImageSize'] = len(out) // images
    tt['image data'] = bytes(out)
    if 'm_StreamData' in tt:
        tt['m_StreamData'] = {'offset': 0, 'size': 0, 'path': ''}
    if 'm_PlatformBlob' in tt:
        tt['m_PlatformBlob'] = []
    obj.save_typetree(tt)
    return True


def _transcode_crunched(obj, tt, fmt):
    """Crunch is never decoded by the iOS player for DXT targets; unpack level 0 to DXT, then ASTC (no mips)."""
    w, h = tt['m_Width'], tt['m_Height']
    raw = bytes(obj.read().get_image_data())
    dxt = texture2ddecoder.unpack_unity_crunch(raw)
    base = DXT1 if fmt == DXT1_CRUNCHED else DXT5
    pw, ph = _blocks(w) * 4, _blocks(h) * 4
    rgba = _bgra_to_rgba(DECODERS[base](bytes(dxt[:_blocks(w) * _blocks(h) * BLOCK_BYTES[base]]), pw, ph))
    out = _context().compress(astc_encoder.ASTCImage(astc_encoder.ASTCType.U8, pw, ph, 1, rgba),
                              astc_encoder.ASTCSwizzle.from_str('RGBA'))
    tt['m_TextureFormat'] = ASTC_4x4
    tt['m_MipCount'] = 1
    tt['m_CompleteImageSize'] = len(out)
    tt['image data'] = bytes(out)
    if 'm_StreamData' in tt:
        tt['m_StreamData'] = {'offset': 0, 'size': 0, 'path': ''}
    obj.save_typetree(tt)
    return True


def transcode_in(objects, report):
    n = 0
    for obj in objects:
        if obj.type.name not in TEXTURE_CLASSES:
            continue
        try:
            if transcode_obj(obj):
                n += 1
        except Exception as e:
            report.setdefault('texture_errors', []).append(repr(e))
    return n
