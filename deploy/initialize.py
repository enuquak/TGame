"""Create development credentials once, without printing them. Run on the server."""

from pathlib import Path
import os
import secrets

PASSWORD_BYTES = 24
PRIVATE_UMASK = 0o077
PRIVATE_DIRECTORY_MODE = 0o700
READ_ONLY_FILE_MODE = 0o444
CREDENTIAL_KEYS = [
    'MONGO_ADMIN_PASSWORD', 'MONGO_DEV_PASSWORD', 'MONGO_TEST_PASSWORD',
    'REDIS_ADMIN_PASSWORD', 'REDIS_DEV_PASSWORD', 'REDIS_TEST_PASSWORD',
    'GRAFANA_ADMIN_PASSWORD', 'PORTAINER_ADMIN_PASSWORD',
]
REDIS_NAMESPACES = ('dev', 'test')

root = Path(__file__).resolve().parent
env_file = root / '.env'
if env_file.exists():
    raise SystemExit('Refusing to replace existing credentials')

os.umask(PRIVATE_UMASK)
values = {key: secrets.token_hex(PASSWORD_BYTES) for key in CREDENTIAL_KEYS}
env_file.write_text(''.join(f'{key}={value}\n' for key, value in values.items()))

secret_dir = root / 'secrets'
secret_dir.mkdir(mode=PRIVATE_DIRECTORY_MODE, exist_ok=True)
acl = secret_dir / 'redis.acl'
lines = ['user default off', f'user admin on >{values["REDIS_ADMIN_PASSWORD"]} ~* &* +@all']
for namespace in REDIS_NAMESPACES:
    password = values[f'REDIS_{namespace.upper()}_PASSWORD']
    lines.append(f'user tgame_{namespace} on >{password} ~tgame:{namespace}:* &tgame:{namespace}:* '
                 '+@read +@write +@connection +@transaction +@scripting +@pubsub -flushall -flushdb')

acl.write_text('\n'.join(lines) + '\n')
# Parent directory is private; Docker's non-root Redis process must read the mounted file.
acl.chmod(READ_ONLY_FILE_MODE)
print('Created .env and Redis ACL; credential values were not displayed.')
