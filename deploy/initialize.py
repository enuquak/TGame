"""Create development credentials once, without printing them. Run on the server."""
from pathlib import Path
import os
import secrets

root = Path(__file__).resolve().parent
env_file = root / '.env'
if env_file.exists():
    raise SystemExit('Refusing to replace existing credentials')
os.umask(0o077)
keys = ['MONGO_ADMIN_PASSWORD', 'MONGO_DEV_PASSWORD', 'MONGO_TEST_PASSWORD',
        'REDIS_ADMIN_PASSWORD', 'REDIS_DEV_PASSWORD', 'REDIS_TEST_PASSWORD',
        'GRAFANA_ADMIN_PASSWORD', 'PORTAINER_ADMIN_PASSWORD']
values = {key: secrets.token_hex(24) for key in keys}
env_file.write_text(''.join(f'{key}={value}\n' for key, value in values.items()))
secret_dir = root / 'secrets'
secret_dir.mkdir(mode=0o700, exist_ok=True)
acl = secret_dir / 'redis.acl'
lines = ['user default off', f'user admin on >{values["REDIS_ADMIN_PASSWORD"]} ~* &* +@all']
for namespace in ('dev', 'test'):
    password = values[f'REDIS_{namespace.upper()}_PASSWORD']
    lines.append(f'user tgame_{namespace} on >{password} ~tgame:{namespace}:* &tgame:{namespace}:* '
                 '+@read +@write +@connection +@transaction +@scripting +@pubsub -flushall -flushdb')
acl.write_text('\n'.join(lines) + '\n')
# Parent directory is private; Docker's non-root Redis process must read the mounted file.
acl.chmod(0o444)
print('Created .env and Redis ACL; credential values were not displayed.')
