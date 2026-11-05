"""Download the pinned Apache archive, or verify an already supplied copy."""
import hashlib
from pathlib import Path
import tempfile
import urllib.request

target = Path(__file__).resolve().parent / 'zookeeper/apache-zookeeper-3.8.8-bin.tar.gz'
url = 'https://archive.apache.org/dist/zookeeper/zookeeper-3.8.8/' + target.name
expected = 'c8fea10468ce7f590bcf7e961ae20f4e969ddbc938cd9c32e48d2bc1e82b95e9d9186007f03877726584daf1655f93a23c23b501bab95c8e03d11324135dbc34'

def digest(path):
    result = hashlib.sha512()
    with path.open('rb') as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b''):
            result.update(chunk)
    return result.hexdigest()

if target.exists():
    if digest(target) != expected:
        raise SystemExit('Existing ZooKeeper archive failed SHA-512 verification; refusing to replace it.')
else:
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=target.parent, delete=False) as output:
        temporary = Path(output.name)
        try:
            with urllib.request.urlopen(url, timeout=60) as source:
                for chunk in iter(lambda: source.read(1024 * 1024), b''):
                    output.write(chunk)
        except BaseException:
            output.close()
            temporary.unlink(missing_ok=True)
            raise
    try:
        if digest(temporary) != expected:
            raise SystemExit('Downloaded ZooKeeper archive failed SHA-512 verification.')
        temporary.replace(target)
    finally:
        temporary.unlink(missing_ok=True)
print('ZooKeeper 3.8.8 archive SHA-512 verified.')
