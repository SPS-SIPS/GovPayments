# Third-Party Notices

This project uses third-party software and platform components. Before any
public open-source release, SPS should verify the complete dependency graph,
generated artifacts, schemas, and deployment materials.

## Direct .NET Dependencies

| Component | Version | License review status |
| --- | --- | --- |
| Microsoft.AspNetCore.Authentication.JwtBearer | 9.0.7 | Verify before release |
| Microsoft.AspNetCore.OpenApi | 9.0.7 | Verify before release |
| Microsoft.EntityFrameworkCore | 9.0.7 | Verify before release |
| Microsoft.EntityFrameworkCore.Design | 9.0.7 | Verify before release |
| Microsoft.EntityFrameworkCore.Relational | 9.0.7 | Verify before release |
| Npgsql.EntityFrameworkCore.PostgreSQL | 9.0.4 | Verify before release |

## Operational Components

| Component | Use | License review status |
| --- | --- | --- |
| PostgreSQL | Database runtime | Verify before release |
| Keycloak | OIDC identity provider | Verify before release |
| OpenBao | Secrets management | Verify before release |
| Docker and Docker Compose | Local and production packaging | Verify before release |

## External Material To Review

- ISO 20022 schemas, generated types, examples, or documentation if added.
- SmartVista, BPC, SIPS Connect, or bank-provided proprietary materials.
- Test fixtures, screenshots, logs, and sample payloads that may contain
  confidential or production-derived data.
- Generated OpenAPI artifacts published with a release.
