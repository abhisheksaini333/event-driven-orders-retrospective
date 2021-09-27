#!/usr/bin/env python3
"""Generate local demo credentials without leaving a partially initialized environment."""
import json
import os
from pathlib import Path
import secrets

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

def initialize(root):
    env = root / '.env'; local = root / '.local'; realm_path = local / 'realm.json'
    if local.is_symlink() or env.is_symlink() or realm_path.is_symlink():
        raise ValueError('Credential paths must not be symbolic links.')
    if local.exists() and (not local.is_dir() or local.stat().st_mode & 0o077):
        raise PermissionError('Existing .local directory must be private to its owner.')
    if env.exists() or realm_path.exists():
        raise FileExistsError('Existing credential files are retained; reconcile them before initializing.')
    local.mkdir(mode=0o700, exist_ok=True)
    values = {key: secrets.token_urlsafe(32) for key in ('KEYCLOAK_ADMIN_PASSWORD', 'ORDERS_CLIENT_SECRET', 'READER_CLIENT_SECRET', 'OPERATOR_CLIENT_SECRET', 'DAPR_API_TOKEN', 'WORKER_CALLBACK_TOKEN')}
    realm = {'realm': 'orders', 'enabled': True, 'sslRequired': 'none', 'accessTokenLifespan': 300, 'bruteForceProtected': True,
             'clients': [client('orders-cli', values['ORDERS_CLIENT_SECRET'], 'orders_writer'), client('reader-cli', values['READER_CLIENT_SECRET'], 'orders_reader'), client('operator-cli', values['OPERATOR_CLIENT_SECRET'], 'orders_operator')]}
    created = []
    try:
        for path, content in [(env, ''.join(f'{key}={value}\n' for key, value in values.items())), (realm_path, json.dumps(realm, indent=2) + '\n')]:
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            created.append(path)
            with os.fdopen(fd, 'w') as target:
                target.write(content)
                target.flush(); os.fsync(target.fileno())
        os.chmod(realm_path, 0o644)  # Private parent directory; container UID needs read access to the bind-mounted file.
    except BaseException:
        for path in reversed(created):
            path.unlink(missing_ok=True)
        raise

def main():
    initialize(Path(__file__).resolve().parents[1])
    print('Generated local demo credentials; values are not printed.')

if __name__ == '__main__':
    main()
