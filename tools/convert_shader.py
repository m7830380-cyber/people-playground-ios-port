"""Convert a Unity 2020.3 Shader typetree (with DX11 programs) to a Metal-only Shader typetree.

2020.3 keeps one flat m_SubPrograms list per program stage that mixes all graphics APIs; each entry
points (m_BlobIndex) into the decompressed blob of its API's platform and carries its parameters both in
the typetree (m_ConstantBuffers, m_TextureParams, ... by m_NameIndices) and at the end of the blob
segment (see blob2020.py). DX11 vertex/pixel programs are translated with HLSLcc (hlslcc_cli) to MSL,
OpenGL/Vulkan programs are dropped, and the shader is re-tagged to the single platform Metal (14).
"""
import os, re, json, struct, subprocess, copy
import lz4.block
from blob2020 import read_sub, write_sub, SUB_VERSION
from dx2metal import merge_params, prep_dxbc

HERE = os.path.dirname(os.path.abspath(__file__))
_UK_HLSLCC = os.path.join(os.path.dirname(os.path.dirname(HERE)), 'ultrakill-ios-port', 'tools', 'hlslcc_cli',
                          'build', 'Release', 'hlslcc_cli.exe')
HLSLCC = os.environ.get('HLSLCC_CLI') or next(
    (p for p in (os.path.join(HERE, 'hlslcc_cli', 'build', 'Release', 'hlslcc_cli.exe'), _UK_HLSLCC) if os.path.exists(p)),
    'hlslcc_cli')
PLATFORM_D3D11, PLATFORM_METAL = 4, 14
DX_VS = {15, 16}
DX_PS = {17, 18}
DX_TYPES = set(range(15, 23))
METAL_VS, METAL_FS = 23, 24
HLSLCC_FLAGS = 0x20000 | 0x80000   # TRANSLATE_MATRICES | METAL_SHADOW_SAMPLER_LINEAR
# Hardware tiers the converted programs are emitted for (Windows stores DX11 programs once, tier 3 = any).
# Overridden by convert_data.py from what the iOS reference shaders use.
METAL_TIERS = [3]
# Metal code header (Unity "MetalShaderBlob" prefix) - set from the reference; None = raw MSL source.
METAL_CODE_HEADER = None

SEM_SRC = {'POSITION': 0, 'NORMAL': 1, 'TANGENT': 2, 'COLOR': 3, 'BLENDWEIGHTS': 12, 'BLENDWEIGHT': 12, 'BLENDINDICES': 13}
# GPU-instancing variants (struct arrays in cbuffers) are dropped and the passes marked non-instanced;
# Unity then falls back to regular / dynamic batching, which is what 2D sprites use anyway.
INSTANCING_KW = {'INSTANCING_ON', 'PROCEDURAL_INSTANCING_ON', 'STEREO_INSTANCING_ON'}
PROGS = ('progVertex', 'progFragment', 'progGeometry', 'progHull', 'progDomain', 'progRayTracing')


class ConvertError(Exception):
    pass


def metal_code(msl):
    if METAL_CODE_HEADER is None:
        return msl
    return METAL_CODE_HEADER(msl)


def platform_data(tt, blob, plat):
    pi = tt['platforms'].index(plat)
    parts = []
    for j in range(len(tt['offsets'][pi])):
        o, cl, dl = tt['offsets'][pi][j], tt['compressedLengths'][pi][j], tt['decompressedLengths'][pi][j]
        parts.append(lz4.block.decompress(blob[o:o + cl], uncompressed_size=dl))
    return b''.join(parts)


def segment(data, idx):
    o, l, _ = struct.unpack_from('<iii', data, 4 + 12 * idx)
    return data[o:o + l]


def join_segments(segs):
    hdr = 4 + 12 * len(segs)
    out = bytearray(struct.pack('<i', len(segs)))
    body = bytearray()
    for s in segs:
        out += struct.pack('<iii', hdr + len(body), len(s), 0)
        body += s
    return bytes(out + body)


# ---------------- reflection -> metal params ----------------
def metal_layout(c):
    n = max(1, c['arraySize'])
    if c['isMatrix']:
        return 16 * c['cols'] * n, 16
    cols = c['cols']
    if cols == 1:
        return 4 * n, 4
    if cols == 2:
        return 8 * n, 8
    return 16 * n, 16


def build_metal_params(refl, msl, P, cbname_map):
    """HLSLcc reflection + original DX params -> (cbs, binds) in blob form (first cb is the empty '' one)."""
    orig_vars = {}
    for cn, cb in P['cbs'].items():
        for v in cb['vars']:
            orig_vars[(cn, v['name'])] = v
    cbs = [dict(name='', size=0, params=[], structs=[])]
    binds = []
    cur = None
    off = 0
    for r in refl['refl']:
        k = r['k']
        if k == 'cb':
            cur = dict(name=r['name'], size=0, params=[], structs=[])
            cbs.append(cur)
            off = 0
        elif k == 'const':
            if cur is None:
                raise ConvertError('const outside cb')
            size, align = metal_layout(r)
            off = (off + align - 1) // align * align
            ocb = cbname_map.get(cur['name'], cur['name'])
            ov = orig_vars.get((ocb, r['name']))
            if ov is None:
                raise ConvertError('unknown const %s.%s' % (ocb, r['name']))
            if ov.get('struct'):
                st = dict(ov['struct'], index=off)
                cur['structs'].append(st)
                off += st['size'] * max(1, st['arraySize'])
                cur['size'] = off
                continue
            cur['params'].append(dict(name=r['name'], type=ov['type'], rows=ov['rows'], cols=ov['cols'],
                                      isMatrix=ov['isMatrix'], arraySize=ov['arraySize'], index=off))
            off += size
            cur['size'] = off
        elif k == 'cbbind':
            binds.append(dict(kind='cb', name=r['name'], index=r['index'], arraySize=0))
        elif k == 'tex':
            if r['uav']:
                ou = next((u for u in P['uav'] if u['name'] == r['name']), None)
                binds.append(dict(kind='uav', name=r['name'], index=r['index'], orig=ou['orig'] if ou else r['index']))
                continue
            ot = next((t for t in P['tex'] if t['name'] == r['name']), None)
            if ot is None:
                raise ConvertError('unknown texture %s' % r['name'])
            samp = r['sampler'] if r['sampler'] >= 0 else -1
            binds.append(dict(kind='tex', name=r['name'], index=r['index'], sampler=samp,
                              extra=(ot['dim'] << 1) | (ot['ms'] & 1)))
        elif k == 'buf':
            if r['uav']:
                ou = next((u for u in P['uav'] if u['name'] == r['name']), None)
                binds.append(dict(kind='uav', name=r['name'], index=r['index'], orig=ou['orig'] if ou else r['index']))
            else:
                ob = next((b for b in P['buf'] if b['name'] == r['name']), None)
                binds.append(dict(kind='buf', name=r['name'], index=r['index'], arraySize=ob['arraySize'] if ob else 0))
    for m in re.finditer(rb'sampler sampler_inline(\d+) \[\[ sampler \((\d+)\) \]\]', msl):
        s = next((x for x in P['samplers'] if x['bind'] == int(m.group(1))), None)
        if s is not None:
            binds.append(dict(kind='sampler', name='', bind=int(m.group(2)), sampler=s['sampler']))
    return cbs, binds


def vertex_channels(refl):
    ch = []
    smap = 0
    for r in refl['refl']:
        if r['k'] != 'input':
            continue
        m = re.match(r'([A-Z_]+?)(\d*)$', r['name'])
        sem, idx = m.group(1), int(m.group(2) or 0)
        if sem == 'TEXCOORD':
            src = 4 + idx
        elif sem in SEM_SRC:
            src = SEM_SRC[sem]
        else:
            raise ConvertError('unknown vertex semantic %s' % r['name'])
        ch.append((src, 13 + r['index']))
        smap |= 1 << src
    return smap, ch


def fix_msl(msl, is_vs):
    if is_vs:
        msl = msl.replace(b'[[ position ]]', b'[[ position, invariant ]]')
    return msl


# ---------------- typetree params ----------------
class Names:
    """A pass's m_NameIndices with the ability to add names."""
    def __init__(self, pairs):
        self.idx = {n: i for n, i in pairs}
        self.next = max(self.idx.values(), default=-1) + 1

    def __call__(self, name):
        if name not in self.idx:
            self.idx[name] = self.next
            self.next += 1
        return self.idx[name]

    def pairs(self):
        return sorted(self.idx.items())


def tt_params(cbs, binds, N):
    """(cbs, binds) blob form -> typetree subprogram parameter fields."""
    def split(params):
        vec, mat = [], []
        for p in params:
            if p['isMatrix']:
                mat.append(dict(m_NameIndex=N(p['name']), m_Index=p['index'], m_ArraySize=p['arraySize'],
                                m_Type=p['type'], m_RowCount=p['rows']))
            else:
                vec.append(dict(m_NameIndex=N(p['name']), m_Index=p['index'], m_ArraySize=p['arraySize'],
                                m_Type=p['type'], m_Dim=p['cols']))
        return vec, mat

    out = dict(m_VectorParams=[], m_MatrixParams=[], m_TextureParams=[], m_BufferParams=[], m_ConstantBuffers=[],
               m_ConstantBufferBindings=[], m_UAVParams=[], m_Samplers=[])
    for cb in cbs:
        vec, mat = split(cb['params'])
        if cb['name'] == '':
            out['m_VectorParams'], out['m_MatrixParams'] = vec, mat
            continue
        structs = []
        for st in cb['structs']:
            svec, smat = split(st['params'])
            structs.append(dict(m_NameIndex=N(st['name']), m_Index=st['index'], m_ArraySize=st['arraySize'],
                                m_StructSize=st['size'], m_VectorMembers=svec, m_MatrixMembers=smat))
        out['m_ConstantBuffers'].append(dict(m_NameIndex=N(cb['name']), m_MatrixParams=mat, m_VectorParams=vec,
                                             m_StructParams=structs, m_Size=cb['size']))
    for b in binds:
        k = b['kind']
        if k == 'tex':
            out['m_TextureParams'].append(dict(m_NameIndex=N(b['name']), m_Index=b['index'], m_SamplerIndex=b['sampler'],
                                               m_MultiSampled=bool(b['extra'] & 1), m_Dim=b['extra'] >> 1))
        elif k == 'cb':
            out['m_ConstantBufferBindings'].append(dict(m_NameIndex=N(b['name']), m_Index=b['index'], m_ArraySize=1))
        elif k == 'buf':
            out['m_BufferParams'].append(dict(m_NameIndex=N(b['name']), m_Index=b['index'], m_ArraySize=b['arraySize']))
        elif k == 'uav':
            out['m_UAVParams'].append(dict(m_NameIndex=N(b['name']), m_Index=b['index'], m_OriginalIndex=b['orig']))
        elif k == 'sampler':
            out['m_Samplers'].append(dict(sampler=b['sampler'], bindPoint=b['bind']))
    return out


# ---------------- hlslcc batch ----------------
class HlslccBatch:
    def __init__(self, workdir):
        self.dir = workdir
        os.makedirs(workdir, exist_ok=True)
        self.jobs = []

    def add(self, key, dxbc):
        open(os.path.join(self.dir, key) + '.dxbc', 'wb').write(dxbc)
        self.jobs.append(key)

    def _line(self, key):
        p = os.path.join(self.dir, key)
        return f'{p}.dxbc\t{p}.metal\t{p}.json\t{HLSLCC_FLAGS}\n'

    def run(self, nproc=None):
        if not self.jobs:
            return
        nproc = max(1, min(nproc or (os.cpu_count() or 2) - 1, len(self.jobs)))
        procs = []
        for i in range(nproc):
            part = self.jobs[i::nproc]
            lf = os.path.join(self.dir, 'list%d.txt' % i)
            with open(lf, 'w') as f:
                f.writelines(self._line(k) for k in part)
            procs.append(subprocess.Popen([HLSLCC, lf], stderr=subprocess.DEVNULL, stdout=subprocess.DEVNULL))
        for p in procs:
            p.wait()
        for key in self.jobs:   # a crash kills the rest of its list: retry those one by one
            if not os.path.exists(os.path.join(self.dir, key) + '.json'):
                lf = os.path.join(self.dir, 'single.txt')
                with open(lf, 'w') as f:
                    f.write(self._line(key))
                subprocess.run([HLSLCC, lf], stderr=subprocess.DEVNULL, stdout=subprocess.DEVNULL)

    def result(self, key):
        p = os.path.join(self.dir, key)
        if not os.path.exists(p + '.json'):
            return None, None
        refl = json.loads(open(p + '.json', 'rb').read().decode('utf8', 'replace'))
        if not refl['ok']:
            return None, refl
        return open(p + '.metal', 'rb').read(), refl


# ---------------- main conversion ----------------
def convert_shader(tt, blob, workdir, log=print):
    """tt: Shader typetree (compressedBlob emptied), blob: compressed blob bytes.
    Returns (new typetree incl. compressedBlob, stats) or None when the shader has no DX11 programs."""
    if PLATFORM_D3D11 not in tt['platforms']:
        return None
    dx = platform_data(tt, blob, PLATFORM_D3D11)
    name = tt['m_ParsedForm']['m_Name']
    batch = HlslccBatch(workdir)
    plan = {}       # dx blob index -> (key, P, sub, is_vs)
    stats = dict(ok=0, fail=0, dropped=0, instancing=0)
    for ss in tt['m_ParsedForm']['m_SubShaders']:
        for p in ss['m_Passes']:
            for prog in PROGS:
                for e in p[prog]['m_SubPrograms']:
                    t = e['m_GpuProgramType']
                    if t not in DX_TYPES:
                        continue
                    if t not in DX_VS | DX_PS:
                        stats['dropped'] += 1
                        continue
                    bi = e['m_BlobIndex']
                    if bi in plan:
                        continue
                    key = 'b%d' % bi
                    try:
                        sub = read_sub(segment(dx, bi))
                        if INSTANCING_KW & set(sub['globalKw'] + sub['localKw']):
                            plan[bi] = 'instancing'
                            continue
                        P = merge_params(None, {}, dict(cbs=sub['cbs'], binds=sub['binds']))
                        code, _ = prep_dxbc(sub['code'], P)
                        batch.add(key, code)
                        plan[bi] = (key, P, sub, t in DX_VS)
                    except Exception as ex:
                        plan[bi] = None
                        log('  [%s] prep failed: %r' % (name, ex))
    batch.run()

    segs = []
    results = {}    # dx blob index -> (metal blob index, cbs, binds, channels, sourceMap)
    for bi, item in plan.items():
        if item == 'instancing':
            stats['instancing'] += 1
            continue
        if item is None:
            stats['fail'] += 1
            continue
        key, P, sub, is_vs = item
        msl, refl = batch.result(key)
        if msl is None:
            stats['fail'] += 1
            log('  [%s] hlslcc failed for blob %d%s' % (name, bi, (': ' + str(refl.get('err', ''))[:200]) if refl else ''))
            continue
        try:
            msl = fix_msl(msl, is_vs)
            cbs, binds = build_metal_params(refl, msl, P, {'VGlobals': '$Globals'} if is_vs else {'FGlobals': '$Globals'})
            smap, ch = vertex_channels(refl) if is_vs else (sub['sourceMap'], [])
            msub = dict(version=SUB_VERSION, type=METAL_VS if is_vs else METAL_FS, stats=list(sub['stats']),
                        globalKw=sub['globalKw'], localKw=sub['localKw'], code=metal_code(msl),
                        sourceMap=smap, channels=ch, cbs=cbs, binds=binds, tail=b'')
            segs.append(write_sub(msub))
            results[bi] = (len(segs) - 1, cbs, binds, ch, smap)
            stats['ok'] += 1
        except Exception as ex:
            stats['fail'] += 1
            log('  [%s] build failed: %r' % (name, ex))

    nt = copy.deepcopy(tt)
    for ss in nt['m_ParsedForm']['m_SubShaders']:
        for p in ss['m_Passes']:
            if p['m_Platforms']:
                p['m_Platforms'] = [PLATFORM_METAL] if PLATFORM_D3D11 in p['m_Platforms'] else []
            N = Names(p['m_NameIndices'])
            for prog in PROGS:
                pr = p[prog]
                out = []
                for e in pr['m_SubPrograms']:
                    if e['m_GpuProgramType'] not in DX_VS | DX_PS:
                        continue
                    r = results.get(e['m_BlobIndex'])
                    if r is None:
                        continue
                    mi, cbs, binds, ch, smap = r
                    ne = dict(e)
                    ne.update(tt_params(cbs, binds, N))
                    ne['m_BlobIndex'] = mi
                    ne['m_GpuProgramType'] = METAL_VS if e['m_GpuProgramType'] in DX_VS else METAL_FS
                    ne['m_Channels'] = dict(m_Channels=[dict(source=s, target=t) for s, t in ch], m_SourceMap=smap)
                    for tier in METAL_TIERS:
                        te = dict(ne)
                        te['m_ShaderHardwareTier'] = tier
                        out.append(te)
                pr['m_SubPrograms'] = out
            p['m_NameIndices'] = N.pairs()
            if stats['instancing']:
                p['m_HasInstancingVariant'] = False
                p['m_HasProceduralInstancingVariant'] = False

    raw = join_segments(segs)
    comp = lz4.block.compress(raw, store_size=False, mode='high_compression')
    nt['platforms'] = [PLATFORM_METAL]
    nt['offsets'] = [[0]]
    nt['compressedLengths'] = [[len(comp)]]
    nt['decompressedLengths'] = [[len(raw)]]
    nt['compressedBlob'] = comp
    log('  [%s] variants ok=%d fail=%d dropped(non vs/ps)=%d instancing=%d'
        % (name, stats['ok'], stats['fail'], stats['dropped'], stats['instancing']))
    return nt, stats
