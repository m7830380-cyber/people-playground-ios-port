"""Inspect the Metal shaders Unity itself compiled into the shell IPA ("unity default resources") to learn
the exact 2020.3 iOS conventions (hardware tiers, code header, parameter encoding).

  python tools/inspect_reference.py <PPG-shell.ipa | unity default resources> [--shader "Sprites/Default"] [--dump N]
"""
import argparse, json, os, struct, sys, zipfile, io
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import UnityPy
from convert_shader import platform_data, segment
from blob2020 import read_sub


def load(path):
    if path.endswith('.ipa'):
        z = zipfile.ZipFile(path)
        name = next(n for n in z.namelist() if n.endswith('Data/unity default resources'))
        return UnityPy.load(z.read(name))
    return UnityPy.load(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('src')
    ap.add_argument('--shader', default=None)
    ap.add_argument('--dump', type=int, default=2)
    a = ap.parse_args()
    env = load(a.src)
    sf = list(env.files.values())[0]
    print('unity', sf.unity_version, 'platform', sf._m_target_platform)
    tiers, types = set(), set()
    shown = 0
    for o in sf.objects.values():
        if o.type.name != 'Shader':
            continue
        tt = o.read_typetree()
        name = tt['m_ParsedForm']['m_Name']
        if 14 not in tt['platforms']:
            print(name, 'platforms', tt['platforms'])
            continue
        data = platform_data(tt, bytes(tt['compressedBlob']), 14)
        for ss in tt['m_ParsedForm']['m_SubShaders']:
            for p in ss['m_Passes']:
                for prog in ('progVertex', 'progFragment'):
                    for e in p[prog]['m_SubPrograms']:
                        tiers.add(e['m_ShaderHardwareTier'])
                        types.add(e['m_GpuProgramType'])
        if (a.shader and a.shader == name) or (not a.shader and shown < a.dump):
            shown += 1
            p = tt['m_ParsedForm']['m_SubShaders'][0]['m_Passes'][0]
            print('=====', name, 'platforms', tt['platforms'], 'pass platforms', p['m_Platforms'])
            print('nameIndices', p['m_NameIndices'])
            for prog in ('progVertex', 'progFragment'):
                for e in p[prog]['m_SubPrograms'][:4]:
                    print(prog, json.dumps(e))
                    S = read_sub(segment(data, e['m_BlobIndex']))
                    code = S['code']
                    print('  blob: type', S['type'], 'ver', S['version'], 'stats', S['stats'], 'kw', S['globalKw'], S['localKw'],
                          'srcmap', S['sourceMap'], 'ch', S['channels'], 'tail', len(S['tail']))
                    print('  cbs', [(c['name'], c['size'], [(q['name'], q['index']) for q in c['params']]) for c in S['cbs']])
                    print('  binds', S['binds'])
                    print('  code len', len(code), 'head', code[:96].hex())
                    print('  code text', code[:400])
    print('tiers', sorted(tiers), 'program types', sorted(types))


if __name__ == '__main__':
    main()
