"""Convert retail People Playground (Windows) data so the iOS shell player can load it.

  python tools/convert_data.py [--game "C:/.../People Playground"] [--out work]

Produces work/Data/ (goes into the app's Data folder, see package_ipa.py). For every serialized file:
  * the target-platform tag is changed from StandaloneWindows64 (19) to iOS (9)
  * every Shader gets its DX11 programs translated to Metal (convert_shader.py)
  * every BCn/DXT (and crunched) texture is re-encoded as ASTC 4x4 (transcode_tex.py)
Audio / meshes / everything else stays as it is.
Nothing produced here may be committed to the public repo - it is derived from the game.
"""
import argparse, hashlib, json, os, pickle, shutil, sys, time, traceback
from concurrent.futures import ProcessPoolExecutor, as_completed

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import UnityPy
import convert_shader
from convert_shader import convert_shader as convert_one
from transcode_tex import transcode_in

IOS_PLATFORM = 9
PLAYER_FILES = ['globalgamemanagers', 'globalgamemanagers.assets', 'level0', 'level1', 'level2', 'resources.assets',
                'sharedassets0.assets', 'sharedassets1.assets', 'sharedassets2.assets', 'Resources/unity_builtin_extra']
COPY_WITH = ['.resS', '.resource']
DEFAULT_GAME = r'C:\Program Files (x86)\Steam\steamapps\common\People Playground'
CONVERTER_VERSION = '3'   # bump to invalidate the shader cache


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


def make_code_header(hdr):
    """Declarative code header from metal_ref.json: {"kind": "prefix", "hex": "<bytes before the MSL source>"}."""
    if hdr.get('kind') == 'prefix':
        prefix = bytes.fromhex(hdr['hex'])
        return lambda msl: prefix + (msl.encode() if isinstance(msl, str) else msl)
    raise ValueError('unknown code_header kind: %r' % hdr.get('kind'))


def load_metal_conventions(path):
    """Settings measured from the shell's own (Unity-compiled) Metal shaders, see inspect_reference.py."""
    if path and os.path.exists(path):
        ref = json.load(open(path))
        convert_shader.METAL_TIERS = ref.get('tiers', convert_shader.METAL_TIERS)
        hdr = ref.get('code_header')
        if hdr:
            convert_shader.METAL_CODE_HEADER = make_code_header(hdr)
    return convert_shader.METAL_TIERS, convert_shader.METAL_CODE_HEADER


def shader_key(tt, blob):
    h = hashlib.sha1()
    h.update(CONVERTER_VERSION.encode())
    h.update(json.dumps(convert_shader.METAL_TIERS).encode())
    h.update(tt['m_ParsedForm']['m_Name'].encode())
    h.update(blob)
    return h.hexdigest()


def convert_shaders_in(objects, cache_dir, work_dir, report):
    n = 0
    for obj in objects:
        if obj.type.name != 'Shader':
            continue
        tt = obj.read_typetree()
        blob = bytes(tt['compressedBlob'])
        name = tt['m_ParsedForm']['m_Name']
        key = shader_key(tt, blob)
        cp = os.path.join(cache_dir, key + '.pkl')
        if os.path.exists(cp):
            nt, stats = pickle.load(open(cp, 'rb'))
        else:
            tt['compressedBlob'] = b''
            msgs = []
            res = convert_one(tt, blob, os.path.join(work_dir, 'hlslcc', key), log=msgs.append)
            if res is None:
                report.setdefault('no_dx11', []).append(name)
                continue
            nt, stats = res
            stats = dict(stats, msgs=msgs[:50])
            pickle.dump((nt, stats), open(cp, 'wb'))
            shutil.rmtree(os.path.join(work_dir, 'hlslcc', key), ignore_errors=True)
        report.setdefault('shaders', {})[name] = {k: v for k, v in stats.items() if k != 'msgs'}
        if stats.get('fail'):
            report.setdefault('shader_msgs', {})[name] = stats.get('msgs', [])
        obj.save_typetree(nt)
        n += 1
    return n


def process_player_file(src, dst, cache_dir, work_dir, ref):
    load_metal_conventions(ref)
    report = {}
    env = UnityPy.load(src)
    sf = list(env.files.values())[0]
    n = convert_shaders_in(sf.objects.values(), cache_dir, work_dir, report)
    report['textures'] = transcode_in(sf.objects.values(), report)
    sf._m_target_platform = IOS_PLATFORM
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    with open(dst, 'wb') as f:
        f.write(sf.save())
    # streamed data next to the file: <name>.resS / <name>.resource, and for "x.assets" also "x.resource"
    # (audio: sharedassets0.assets -> sharedassets0.resource, resources.assets -> resources.resource)
    for base_src, base_dst in {(src, dst), (os.path.splitext(src)[0], os.path.splitext(dst)[0])}:
        for ext in COPY_WITH:
            if os.path.exists(base_src + ext):
                shutil.copyfile(base_src + ext, base_dst + ext)
    return n, report


def job(src, dst, cache_dir, work_dir, ref):
    try:
        return process_player_file(src, dst, cache_dir, work_dir, ref)
    except Exception:
        return -1, {'error': traceback.format_exc()}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=DEFAULT_GAME)
    ap.add_argument('--out', default='work')
    ap.add_argument('--ref', default=None, help='metal_ref.json from inspect_reference.py')
    ap.add_argument('--only', default=None, help='substring filter on file names (testing)')
    ap.add_argument('--jobs', type=int, default=3)
    a = ap.parse_args()

    data = os.path.join(a.game, 'People Playground_Data')
    out_data = os.path.join(a.out, 'Data')
    cache_dir = os.path.join(a.out, 'cache', 'shaders')
    work_dir = os.path.join(a.out, 'tmp')
    os.makedirs(cache_dir, exist_ok=True)
    ref = a.ref or os.path.join(a.out, 'metal_ref.json')
    tiers, hdr = load_metal_conventions(ref)
    log('metal tiers', tiers, 'code header', 'yes' if hdr else 'none (raw MSL)')

    jobs = [(os.path.join(data, f), os.path.join(out_data, f)) for f in PLAYER_FILES if os.path.exists(os.path.join(data, f))]
    if a.only:
        jobs = [j for j in jobs if a.only in j[0]]
    jobs.sort(key=lambda j: -os.path.getsize(j[0]))

    # StreamingAssets ship as Data/Raw
    sa = os.path.join(data, 'StreamingAssets')
    if os.path.isdir(sa) and not a.only:
        shutil.copytree(sa, os.path.join(out_data, 'Raw'), dirs_exist_ok=True)
    for f in ('RuntimeInitializeOnLoads.json', 'ScriptingAssemblies.json'):
        if not a.only:
            shutil.copyfile(os.path.join(data, f), os.path.join(a.out, 'retail-' + f))

    log('converting', len(jobs), 'files')
    report = {}
    t0 = time.time()
    with ProcessPoolExecutor(max_workers=a.jobs) as ex:
        futs = {ex.submit(job, src, dst, cache_dir, work_dir, ref): src for src, dst in jobs}
        for fu in as_completed(futs):
            src = futs[fu]
            n, rep = fu.result()
            sh = rep.get('shaders', {})
            bad = sum(1 for s in sh.values() if s.get('fail'))
            log('done', os.path.relpath(src, data), 'shaders', n, 'with failures', bad, 'textures', rep.get('textures'),
                'texture errors', len(rep.get('texture_errors', [])), rep.get('error', ''))
            report[os.path.relpath(src, data)] = rep
    json.dump(report, open(os.path.join(a.out, 'convert_report.json'), 'w'), indent=1)
    log('finished in %.0fs' % (time.time() - t0))


if __name__ == '__main__':
    main()
