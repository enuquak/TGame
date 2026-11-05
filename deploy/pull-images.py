"""Pull only digests independently checked against the upstream registries."""
import concurrent.futures
import json
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parent
images = json.loads((root / 'images.lock.json').read_text())

def pull(item):
    key, image = item
    if 'imported_image_id' in image:
        reference = image['imported_image_id']
        subprocess.run(['docker', 'image', 'inspect', reference], check=True, stdout=subprocess.DEVNULL)
        print(f'Using verified imported image {key}: {reference}', flush=True)
        return key, reference
    repo = image['upstream'].removeprefix('docker.io/').rsplit(':', 1)[0]
    # Cloud networking cannot reach Docker Hub. Content is pinned to its verified digest.
    prefix = 'public.ecr.aws/docker/' if repo.startswith('library/') else 'docker.m.daocloud.io/'
    reference = prefix + repo + '@' + image['digest']
    log_path = root / f'pull-{key.lower()}.log'
    existing = subprocess.run(['docker', 'image', 'inspect', reference],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if existing.returncode:
        with log_path.open('w') as log:
            subprocess.run(['docker', 'pull', '--platform', 'linux/amd64', reference],
                           check=True, stdout=log, stderr=subprocess.STDOUT, timeout=900)
    print(f'Pulled {key}: {reference}', flush=True)
    return key, reference

with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
    references = dict(pool.map(pull, images.items()))
env = root / '.env'
lines = [line for line in env.read_text().splitlines() if line.split('=', 1)[0] not in references]
lines.extend(f'{key}={value}' for key, value in references.items())
env.write_text('\n'.join(lines) + '\n')
subprocess.run(['docker', 'run', '--rm', references['HELLO_IMAGE']], check=True)
