#!/usr/bin/env python3
"""Generate ignored loopback-only demo credentials; never overwrite an existing environment."""
import json
import os
from pathlib import Path
import secrets

ROOT = Path(__file__).resolve().parents[1]
env = ROOT / '.env'
if env.exists():
    raise SystemExit('Existing .env retained. See docs/runbook.md before resetting credentials or volumes.')
values = {key: secrets.token_urlsafe(32) for key in ('KEYCLOAK_ADMIN_PASSWORD', 'ORDERS_CLIENT_SECRET', 'READER_CLIENT_SECRET', 'OPERATOR_CLIENT_SECRET', 'DAPR_API_TOKEN', 'WORKER_CALLBACK_TOKEN')}
local = ROOT / '.local'
local.mkdir(mode=0o700, exist_ok=True)
def client(name, secret, role):
    return {
        'clientId': name, 'enabled': True, 'protocol': 'openid-connect', 'publicClient': False,
        'secret': secret, 'serviceAccountsEnabled': True, 'standardFlowEnabled': False, 'directAccessGrantsEnabled': False,
        'protocolMappers': [
            {'name': 'orders-audience', 'protocol': 'openid-connect', 'protocolMapper': 'oidc-audience-mapper',
             'config': {'included.custom.audience': 'orders-api', 'access.token.claim': 'true', 'id.token.claim': 'false'}},
            {'name': 'orders-role', 'protocol': 'openid-connect', 'protocolMapper': 'oidc-hardcoded-claim-mapper',
             'config': {'claim.name': 'roles', 'claim.value': role, 'jsonType.label': 'String', 'access.token.claim': 'true', 'id.token.claim': 'false'}}
        ]
    }
realm = {'realm': 'orders', 'enabled': True, 'sslRequired': 'none', 'accessTokenLifespan': 300,
         'bruteForceProtected': True, 'clients': [client('orders-cli', values['ORDERS_CLIENT_SECRET'], 'orders_writer'), client('reader-cli', values['READER_CLIENT_SECRET'], 'orders_reader'), client('operator-cli', values['OPERATOR_CLIENT_SECRET'], 'orders_operator')]}
for path, content in [(env, ''.join(f'{key}={value}\n' for key, value in values.items())), (local / 'realm.json', json.dumps(realm, indent=2) + '\n')]:
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w') as target:
        target.write(content)
os.chmod(local / 'realm.json', 0o644)  # Parent directory remains private; Keycloak's container UID must read the mount.
print('Generated .env and .local/realm.json with local demo credentials (not printed).')
