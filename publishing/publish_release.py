"""Publish an immutable, versioned release from the already tested Windows executable."""
import hashlib
import json
import os
import re
import sys
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import quote, urlparse
from urllib.request import HTTPRedirectHandler, Request, build_opener

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = 'Taimai07/big-fat-fish-desktop-pet'


class SafeRedirects(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, url):
        redirected = super().redirect_request(request, fp, code, message, headers, url)
        if redirected is not None and urlparse(request.full_url).hostname != urlparse(url).hostname:
            redirected.remove_header('Authorization')
        return redirected


def inputs():
    source = (ROOT / 'src/Runtime.cs').read_text(encoding='utf-8-sig')
    match = re.search(r'AssemblyFileVersion\("(\d+)\.(\d+)\.(\d+)\.0"\)', source)
    if not match:
        raise ValueError('Expected a three-part release version with zero revision.')
    version = '.'.join(match.groups())
    executable = ROOT / 'downloads' / '\u5927\u80a5\u9c7c\u684c\u5ba0.exe'
    checksum = ROOT / 'downloads/SHA256SUMS.txt'
    digest = hashlib.sha256(executable.read_bytes()).hexdigest()
    if checksum.read_text(encoding='utf-8').strip() != digest + '  ' + executable.name:
        raise ValueError('Executable checksum mismatch; publication stopped.')
    notes = (ROOT / 'RELEASE_NOTES.md').read_text(encoding='utf-8-sig')
    if version not in notes.splitlines()[0]:
        raise ValueError('Release notes and application version differ.')
    return version, executable, checksum, notes


def main():
    version, executable, checksum, notes = inputs()
    tag = 'v' + version
    if sys.argv[1:] == ['--validate-only']:
        print(json.dumps({'tag': tag, 'sha256': hashlib.sha256(executable.read_bytes()).hexdigest(), 'inputs_verified': True}))
        return
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('GITHUB_REPOSITORY') != REPOSITORY:
        raise ValueError('Publish only in the authorized repository workflow.')
    if os.environ.get('GITHUB_REF_TYPE') == 'tag' and os.environ.get('GITHUB_REF_NAME') != tag:
        raise ValueError('Pushed tag and executable version differ.')
    token = os.environ['GH_TOKEN']
    opener = build_opener(SafeRedirects())

    def request(url, data=None, method=None, accept='application/vnd.github+json', kind='application/json'):
        payload = json.dumps(data, ensure_ascii=False).encode('utf-8') if isinstance(data, dict) else data
        req = Request(url, data=payload, method=method, headers={
            'Authorization': 'Bearer ' + token, 'Accept': accept,
            'Content-Type': kind, 'User-Agent': 'BigFatFish-Release', 'X-GitHub-Api-Version': '2022-11-28'})
        with opener.open(req, timeout=60) as response:
            raw = response.read()
        return raw if accept == 'application/octet-stream' else json.loads(raw)

    def api(path, data=None, method=None):
        return request('https://api.github.com/repos/' + REPOSITORY + path, data, method)

    try:
        release = api('/releases/tags/' + tag)
    except HTTPError as error:
        if error.code != 404:
            raise
        release = api('/releases', {'tag_name': tag, 'target_commitish': os.environ['GITHUB_SHA'],
                                   'name': '\u5927\u80a5\u9c7c\u684c\u5ba0 ' + version,
                                   'body': notes, 'draft': True, 'prerelease': False}, 'POST')
    assets = {item['name']: item for item in release['assets']}
    verified = []
    for path in (executable, checksum):
        expected = hashlib.sha256(path.read_bytes()).hexdigest()
        asset = assets.get(path.name)
        if asset is None:
            if not release['draft']:
                raise ValueError('Published versions are immutable; missing asset requires a new version.')
            asset = request('https://uploads.github.com/repos/' + REPOSITORY + '/releases/' + str(release['id']) + '/assets?name=' + quote(path.name),
                            path.read_bytes(), 'POST', kind='application/octet-stream')
        if asset['size'] != path.stat().st_size or asset.get('state') != 'uploaded':
            raise ValueError('Uploaded asset metadata does not match.')
        server_digest = asset.get('digest')
        if server_digest:
            if server_digest != 'sha256:' + expected:
                raise ValueError('Existing release asset differs; never overwrite a version.')
        else:
            downloaded = request(asset['url'], accept='application/octet-stream')
            if hashlib.sha256(downloaded).hexdigest() != expected:
                raise ValueError('Existing release bytes differ; never overwrite a version.')
        verified.append((asset, expected))
    if release['draft']:
        release = api('/releases/' + str(release['id']), {'draft': False, 'make_latest': 'true'}, 'PATCH')
    for asset, expected in verified:
        # No credential is sent when verifying the public download.
        req = Request(asset['browser_download_url'], headers={'User-Agent': 'BigFatFish-Release-Verification'})
        with opener.open(req, timeout=60) as response:
            downloaded = response.read()
        if hashlib.sha256(downloaded).hexdigest() != expected:
            raise ValueError('Public release download mismatch.')
    print(json.dumps({'release': release['html_url'], 'tag': tag, 'public_downloads_verified': True}))


if __name__ == '__main__':
    main()
