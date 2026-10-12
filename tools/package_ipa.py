"""Build the final PeoplePlayground.ipa from the CI shell IPA plus the converted game data.

  python tools/package_ipa.py [--work work] [--shell path/to/PPG-shell.ipa]

Without --shell the `shell-latest` release of the PRIVATE repo is downloaded with the GitHub CLI.
Output: work/PeoplePlayground.ipa (install with TrollStore). Never upload it anywhere public.
"""
import argparse, json, os, shutil, subprocess, zipfile

PRIVATE_REPO = 'm7830380-cyber/ppg-ios-private'
SHELL_ASSET = 'PPG-shell.ipa'
# Player data files that come from the retail game (converted). Everything else in Data/ (IL2CPP
# metadata, "unity default resources", ScriptingAssemblies.json, ...) is the iOS shell's own.
RETAIL_PREFIXES = ('level', 'sharedassets', 'resources.', 'globalgamemanagers')
STORE_EXT = ('.resS', '.resource', '.assets', '.ttf')


def find_app(payload):
    for n in os.listdir(payload):
        if n.endswith('.app'):
            return os.path.join(payload, n)
    raise SystemExit('no .app in shell ipa')


def merge_rtiol(app_data, retail_json):
    """Union of RuntimeInitializeOnLoads.json from retail and shell (the shell adds PPGiOS)."""
    p_shell = os.path.join(app_data, 'RuntimeInitializeOnLoads.json')
    if not os.path.exists(retail_json):
        return
    shell = json.load(open(p_shell)) if os.path.exists(p_shell) else {'root': []}
    retail = json.load(open(retail_json))
    seen = {(e['assemblyName'], e['nameSpace'], e['className'], e['methodName']) for e in shell['root']}
    added = 0
    for e in retail['root']:
        k = (e['assemblyName'], e['nameSpace'], e['className'], e['methodName'])
        if k not in seen:
            shell['root'].append(e)
            seen.add(k)
            added += 1
    json.dump(shell, open(p_shell, 'w'))
    print('RuntimeInitializeOnLoads: %d entries (+%d from retail)' % (len(shell['root']), added))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--work', default='work')
    ap.add_argument('--shell')
    a = ap.parse_args()

    shell = a.shell
    if not shell:
        shell = os.path.join(a.work, SHELL_ASSET)
        if os.path.exists(shell):
            os.remove(shell)
        subprocess.check_call(['gh', 'release', 'download', 'shell-latest', '--repo', PRIVATE_REPO,
                               '-p', SHELL_ASSET, '-D', a.work])

    stage = os.path.join(a.work, 'ipa_stage')
    shutil.rmtree(stage, ignore_errors=True)
    with zipfile.ZipFile(shell) as z:
        z.extractall(stage)
    app = find_app(os.path.join(stage, 'Payload'))
    data = os.path.join(app, 'Data')
    print('shell app:', app)

    for n in os.listdir(data):
        if n.startswith(RETAIL_PREFIXES):
            os.remove(os.path.join(data, n))
    conv = os.path.join(a.work, 'Data')
    copied = 0
    for root, _, files in os.walk(conv):
        for f in files:
            src = os.path.join(root, f)
            dst = os.path.join(data, os.path.relpath(src, conv))
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copyfile(src, dst)
            copied += 1
    print('copied %d converted data files' % copied)
    merge_rtiol(data, os.path.join(a.work, 'retail-RuntimeInitializeOnLoads.json'))

    out = os.path.join(a.work, 'PeoplePlayground.ipa')
    if os.path.exists(out):
        os.remove(out)
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, allowZip64=True, compresslevel=6) as z:
        for root, dirs, files in os.walk(os.path.join(stage, 'Payload')):
            for f in files:
                p = os.path.join(root, f)
                arc = os.path.relpath(p, stage).replace('\\', '/')
                ct = zipfile.ZIP_STORED if f.endswith(STORE_EXT) else zipfile.ZIP_DEFLATED
                z.write(p, arc, compress_type=ct)
    print('wrote', out, os.path.getsize(out) >> 20, 'MB')


if __name__ == '__main__':
    main()
