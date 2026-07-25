# Government Payments Project OpenBao Security Tier

This folder adapts the Helios OpenBao pattern for the Government Payments Project.

OpenBao is used for deployment-grade runtime configuration and secret handling. The gateway can load KV v2 values from OpenBao during startup when `OpenBao__Enabled=true`.

## One-Command Startup

The root compose stack starts OpenBao, runs an idempotent bootstrap job, and starts the gateway after bootstrap succeeds:

```bash
docker compose up -d
```

OpenBao listens on `8200` inside Docker and is exposed on host port `8201` by default. Override the host binding with `OPENBAO_HOST_PORT=8200 docker compose up -d` when needed.

The bootstrap job:

- initializes OpenBao when needed
- unseals OpenBao when needed
- enables KV v2, transit, and audit logging
- writes gateway runtime configuration to `secret/sps-p2g-gateway/runtime`
- writes the gateway token to `security/openbao/runtime/gateway_token`

## Manual OpenBao Operations

```bash
make security-prepare
make security-up
```

The project intentionally has one Docker Compose file at the repository root. The OpenBao service mounts its config/data/audit/log paths from this folder.

## Manual Initialize and Unseal

```bash
make security-init
make security-unseal
```

The init script writes unseal and root-token files under `security/openbao/keys`. Those files are ignored by git and must be protected by operators.

## Manual Bootstrap P2G Policy

```bash
make security-bootstrap
```

The bootstrap enables:

- KV v2 mount: `secret`
- Runtime secret path: `secret/sps-p2g-gateway/runtime`
- Transit key: `transit/sps-p2g-runtime-kek`
- Least-privilege token policy: `sps-p2g-gateway-runtime`

Store gateway runtime settings in OpenBao using the root/admin token:

```bash
export BAO_ADDR=http://127.0.0.1:8200
export BAO_TOKEN="$(cat security/openbao/keys/root_token)"

docker compose exec -T openbao bao kv put secret/sps-p2g-gateway/runtime \
  ConnectionStrings__P2G='Host=postgres;Database=sps_p2g_gateway;Username=p2g;Password=<secret>' \
  P2G__DescriptorHmacSecret='<at-least-32-char-secret>' \
  P2G__InternalApiKey='' \
  Keycloak__Authority='https://keycloak.example/realms/sps' \
  Keycloak__Audience='sps-p2g-gateway' \
  Keycloak__RequireHttpsMetadata='true'
```

The gateway accepts flat environment-style keys using `__`, or nested JSON keys. Both map to normal ASP.NET configuration keys.

## Gateway Environment

At runtime, the gateway still needs enough non-secret configuration to reach OpenBao:

```bash
OpenBao__Enabled=true
OpenBao__BaseUrl=http://openbao:8200
OpenBao__TokenFile=/run/openbao/gateway_token
OpenBao__KvMount=secret
OpenBao__SecretPath=sps-p2g-gateway/runtime
DataProtection__PersistKeysToFileSystemPath=/var/lib/sps-p2g-gateway/data-protection-keys
Auth__AllowAuthorityApiKeyFallback=false
Auth__AllowSipsConnectInternalKeyFallback=false
Keycloak__Enabled=true
```

Production startup validation requires OpenBao to be enabled and Data Protection keys to be persisted.

## Optional Staging Timers

The `systemd` folder contains optional host units for staging/UAT:

- `openbao-auto-unseal.*`
- `openbao-token-renew.*`

They assume the security directory is `/home/ubuntu/sps-p2g/security/openbao`. Review paths before installing them.

For production, prefer an external KMS/HSM-backed seal and a formal token lifecycle instead of storing unseal material on the same host.
