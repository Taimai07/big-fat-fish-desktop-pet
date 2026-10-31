import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import publish_release as publisher
import prepare_release


class FakeGitHub:
    def __init__(self, existing=False, collision=False):
        self.existing = existing
        self.collision = collision
        self.calls = []
        self.assets = []
        _, executable, checksum, _ = publisher.inputs()
        self.draft = not existing
        self.contents = {'BigFatFish.exe': executable.read_bytes(), 'SHA256SUMS.txt': checksum.read_bytes()}
        if existing:
            for name, data in self.contents.items():
                self.assets.append(self.asset(name, data, 'v0.7.2'))
            if collision:
                self.assets[0]['digest'] = 'sha256:' + '0' * 64

    def asset(self, name, data, tag):
        from urllib.parse import quote
        return {'id': 7 + len(self.assets), 'name': name, 'size': len(data), 'state': 'uploaded',
                'digest': 'sha256:' + hashlib.sha256(data).hexdigest(),
                'browser_download_url': 'https://github.com/test/releases/download/' + tag + '/' + quote(name)}

    def release(self, draft):
        return {'id': 123, 'draft': draft, 'assets': self.assets, 'html_url': 'https://github.com/test/releases/tag/v0.7.2'}

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
        elif method == 'PATCH':
            self.draft = False
            self.assets = [dict(asset, browser_download_url=asset['browser_download_url'].replace('untagged-draft', 'v0.7.2')) for asset in self.assets]
            result = self.release(False)
        else:
            if 'untagged-draft' in url:
                raise AssertionError('A published download used a stale draft URL.')
            return io.BytesIO(self.contents[unquote(url.rsplit('/', 1)[1])])
        return io.BytesIO(json.dumps(result).encode('utf-8'))


class ReleaseContractTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        (self.root / 'src').mkdir()
        (self.root / 'dist').mkdir()
        (self.root / 'src/Runtime.cs').write_text('[assembly: AssemblyFileVersion("0.7.2.0")]', encoding='utf-8')
        (self.root / 'RELEASE_NOTES.md').write_text('Version 0.7.2\nFixture notes', encoding='utf-8')
        (self.root / 'dist' / '\u5927\u80a5\u9c7c\u684c\u5ba0.exe').write_bytes(b'controlled build fixture')
        with contextlib.redirect_stdout(io.StringIO()):
            prepare_release.prepare(self.root)
        root_patch = patch.object(publisher, 'ROOT', self.root)
        root_patch.start()
        self.addCleanup(root_patch.stop)

    def run_publisher(self, fake):
        with patch.dict(os.environ, {'GITHUB_ACTIONS': 'true', 'GITHUB_REPOSITORY': publisher.REPOSITORY,
                                     'GITHUB_SHA': '1' * 40, 'GITHUB_RUN_ID': '1234', 'GH_TOKEN': 'local-fixture', 'GITHUB_REF_TYPE': 'branch'}), \
                patch.object(publisher, 'build_opener', return_value=fake), \
                patch.object(publisher.sys, 'argv', ['publish_release.py']), contextlib.redirect_stdout(io.StringIO()):
            publisher.main()

    def test_new_release_verifies_final_urls(self):
        fake = FakeGitHub()
        self.run_publisher(fake)
        self.assertEqual(sum(method == 'POST' and '/assets?' in url for method, url in fake.calls), 2)
        self.assertTrue(any(method == 'GET' and '/download/v0.7.2/' in url for method, url in fake.calls))

    def test_existing_identical_version_is_read_only(self):
        fake = FakeGitHub(existing=True)
        self.run_publisher(fake)
        self.assertTrue(all(method == 'GET' for method, _ in fake.calls))

    def test_existing_different_version_bytes_are_never_overwritten(self):
        fake = FakeGitHub(existing=True, collision=True)
        with self.assertRaises(ValueError):
            self.run_publisher(fake)
        self.assertTrue(all(method == 'GET' for method, _ in fake.calls))

    def test_checksum_names_actual_public_asset(self):
        _, executable, checksum, _ = publisher.inputs()
        self.assertEqual(executable.name, 'BigFatFish.exe')
        self.assertEqual(checksum.read_text(encoding='utf-8').split('  ')[1].strip(), executable.name)
        self.assertEqual(executable.read_bytes(), b'controlled build fixture')

    def test_correct_digest_with_wrong_filename_is_rejected(self):
        _, executable, checksum, _ = publisher.inputs()
        checksum.write_text(hashlib.sha256(executable.read_bytes()).hexdigest() + '  old-name.exe\n', encoding='utf-8')
        with self.assertRaises(ValueError):
            publisher.inputs()

    def test_changed_executable_is_rejected_before_publication(self):
        _, executable, _, _ = publisher.inputs()
        executable.write_bytes(b'changed bytes')
        with self.assertRaises(ValueError):
            publisher.inputs()


if __name__ == '__main__':
    unittest.main()
