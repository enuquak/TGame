"""Initialize Portainer through loopback; pin its locally generated certificate."""

from pathlib import Path
from http import HTTPStatus
import json
import re
import ssl
import subprocess
import urllib.error
import urllib.parse
import urllib.request

PORTAINER_HOST = '127.0.0.1'
PORTAINER_PORT = 9443
REQUEST_TIMEOUT_SECONDS = 20
LOCAL_DOCKER_ENDPOINT_TYPE = 1

root = Path(__file__).resolve().parent
env = dict(line.split('=', 1) for line in (root / '.env').read_text().splitlines() if '=' in line)
certificate = ssl.get_server_certificate((PORTAINER_HOST, PORTAINER_PORT))
(root / 'config' / 'portainer.crt').write_text(certificate)
context = ssl.create_default_context(cadata=certificate)
context.check_hostname = False  # Trust this exact locally issued certificate, accessed over loopback.
base = f'https://{PORTAINER_HOST}:{PORTAINER_PORT}/api'


def request(path, data=None, token=None, form=False, setup_token=None):
    headers = {}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    if setup_token:
        headers['X-Setup-Token'] = setup_token
    if data is not None:
        headers['Content-Type'] = 'application/x-www-form-urlencoded' if form else 'application/json'
        data = (urllib.parse.urlencode(data) if form else json.dumps(data)).encode()

    req = urllib.request.Request(base + path, data=data, headers=headers)
    with urllib.request.urlopen(req, context=context, timeout=REQUEST_TIMEOUT_SECONDS) as response:
        body = response.read()
        return json.loads(body) if body else None


credentials = {'Username': 'admin', 'Password': env['PORTAINER_ADMIN_PASSWORD']}
try:
    request('/users/admin/check')
except urllib.error.HTTPError as error:
    if error.code != HTTPStatus.NOT_FOUND:
        raise

    logs = subprocess.run(['docker', 'compose', 'logs', '--no-color', 'portainer'],
                          cwd=root, check=True, capture_output=True, text=True).stdout
    tokens = re.findall(r'setup_token=([a-fA-F0-9]+)', logs)
    if not tokens:
        raise RuntimeError('No Portainer setup token found; restart Portainer and retry.')

    request('/users/admin/init', credentials, setup_token=tokens[-1])

token = request('/auth', credentials)['jwt']
endpoints = request('/endpoints', token=token)
if not endpoints:
    request('/endpoints', {'Name': 'TGame Docker', 'EndpointCreationType': LOCAL_DOCKER_ENDPOINT_TYPE,
                          'URL': 'unix:///var/run/docker.sock'}, token=token, form=True)

print('Portainer admin login and local Docker endpoint verified.')
