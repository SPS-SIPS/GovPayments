# Contributing

Thank you for contributing to the Government Payments Project.

## License

Unless explicitly stated otherwise, contributions intentionally submitted for
inclusion in this repository are provided under the Apache License, Version 2.0.

Contributors must have the right to submit their work. Do not contribute code,
configuration, schemas, documents, logs, data, or generated artifacts copied
from proprietary systems unless SPS has confirmed the right to publish them.

## Development

Use the repository workflows where possible:

```bash
make restore
make build
make test
make verify
```

Keep changes focused, include tests for behavior changes, and avoid committing
credentials, production data, private certificates, or environment files.

## Ownership And Review

SPS institutional maintainers review and approve changes. Security-sensitive
changes, authentication/authorization changes, payment lifecycle changes, and
deployment changes require maintainer review before release.
