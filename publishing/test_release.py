import contextlib
import hashlib
import io
import json
import os
import unittest
from unittest.mock import patch

import publish_release as publisher


class FakeGitHub:
    def __init__(self, existing=False, collision=False, legacy=False):
        self.existing = existing
        self.collision = collision
        self.calls = []
        self.assets = []
        _, executable, checksum, _ = publisher.inputs()
        self.draft = not existing
        self.contents = {'BigFatFish.exe': executable.read_bytes(), 'SHA256SUMS.txt': checksum.read_bytes()}
        if existing:
            for name, data in self.contents.items():
                self.assets.append(self.asset(name, data, 'v0.7.1'))
            if collision:
                self.assets[0]['digest'] = 'sha256:' + '0' * 64
            if legacy:
                self.assets[0]['name'] = 'default.exe'
                self.assets[0]['browser_download_url'] = self.assets[0]['browser_download_url'].replace('BigFatFish.exe', 'default.exe')

    def asset(self, name, data, tag):
        from urllib.parse import quote
        return {'id': 7 + len(self.assets), 'name': name, 'size': len(data), 'state': 'uploaded',
                'digest': 'sha256:' + hashlib.sha256(data).hexdigest(),
                'browser_download_url': 'https://github.com/test/releases/download/' + tag + '/' + quote(name)}

    def release(self, draft):
        return {'id': 123, 'draft': draft, 'assets': self.assets, 'html_url': 'https://github.com/test/releases/tag/v0.7.1'}

    def open(self, request, timeout):
        from urllib.error import HTTPError
        from urllib.parse import unquote, urlparse, parse_qs
        url = request.full_url
        method = request.get_method()
        self.calls.append((method, url))
        if method == 'GET' and '/releases/tags/' in url:
            if not self.existing:
                raise HTTPError(url, 404, 'Not Found', {}, None)
            result = self.release(False)
        elif method == 'POST' and url.endswith('/releases'):
            result = self.release(True)
        elif method == 'POST' and '/assets?' in url:
            name = parse_qs(urlparse(url).query)['name'][0]
            result = self.asset(name, request.data, 'untagged-draft')
            self.assets.append(result)
        elif method == 'GET' and url.endswith('/releases/123'):
            result = self.release(self.draft)
        elif method == 'PATCH' and '/releases/assets/' in url:
            metadata = json.loads(request.data)
            self.assets[0] = dict(self.assets[0], name=metadata['name'], browser_download_url=self.assets[0]['browser_download_url'].replace('default.exe', metadata['name']))
            result = self.assets[0]
        elif method == 'PATCH':
            self.draft = False
            self.assets = [dict(asset, browser_download_url=asset['browser_download_url'].replace('untagged-draft', 'v0.7.1')) for asset in self.assets]
            result = self.release(False)
        else:
            if 'untagged-draft' in url:
                raise AssertionError('A published download used a stale draft URL.')
            return io.BytesIO(self.contents[unquote(url.rsplit('/', 1)[1])])
        return io.BytesIO(json.dumps(result).encode('utf-8'))


class ReleaseContractTests(unittest.TestCase):
    def run_publisher(self, fake):
        with patch.dict(os.environ, {'GITHUB_ACTIONS': 'true', 'GITHUB_REPOSITORY': publisher.REPOSITORY,
                                     'GITHUB_SHA': '1' * 40, 'GH_TOKEN': 'local-fixture', 'GITHUB_REF_TYPE': 'branch'}), \
                patch.object(publisher, 'build_opener', return_value=fake), \
                patch.object(publisher.sys, 'argv', ['publish_release.py']), contextlib.redirect_stdout(io.StringIO()):
            publisher.main()

    def test_new_release_verifies_final_urls(self):
        fake = FakeGitHub()
        self.run_publisher(fake)
        self.assertEqual(sum(method == 'POST' and '/assets?' in url for method, url in fake.calls), 2)
        self.assertTrue(any(method == 'GET' and '/download/v0.7.1/' in url for method, url in fake.calls))

    def test_existing_identical_version_is_read_only(self):
        fake = FakeGitHub(existing=True)
        self.run_publisher(fake)
        self.assertTrue(all(method == 'GET' for method, _ in fake.calls))

    def test_existing_different_version_bytes_are_never_overwritten(self):
        fake = FakeGitHub(existing=True, collision=True)
        with self.assertRaises(ValueError):
            self.run_publisher(fake)
        self.assertTrue(all(method == 'GET' for method, _ in fake.calls))

    def test_sanitized_filename_is_repaired_without_uploading_new_bytes(self):
        fake = FakeGitHub(existing=True, legacy=True)
        self.run_publisher(fake)
        self.assertEqual(sum(method == 'PATCH' and '/assets/' in url for method, url in fake.calls), 1)
        self.assertTrue(all(method != 'POST' for method, _ in fake.calls))


if __name__ == '__main__':
    unittest.main()
