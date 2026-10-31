"""Stage the freshly compiled executable under its public release filename."""
import hashlib
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
EXECUTABLE_NAME = 'BigFatFish.exe'


def prepare(root=ROOT):
    source = root / 'dist' / '\u5927\u80a5\u9c7c\u684c\u5ba0.exe'
    destination = root / 'release'
    destination.mkdir(exist_ok=True)
    executable = destination / EXECUTABLE_NAME
    shutil.copyfile(source, executable)
    digest = hashlib.sha256(executable.read_bytes()).hexdigest()
    (destination / 'SHA256SUMS.txt').write_text(digest + '  ' + EXECUTABLE_NAME + '\n', encoding='utf-8')
    print(json.dumps({'sha256': digest, 'filename': EXECUTABLE_NAME, 'source': str(source)}))


if __name__ == '__main__':
    prepare()
