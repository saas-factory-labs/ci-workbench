# Security policy

## Reporting a vulnerability

Please report privately via GitHub **Security advisories** on this repository ("Report a vulnerability"), not in public issues. Include reproduction steps and affected versions. We aim to acknowledge within 5 working days.

## Security model of the MVP

- The standalone host is **single-user and intended for localhost**. Anyone who can reach the server can use whatever the server-side token can read. Do not expose it to a network without adding your own authentication in front of it.
- The GitHub token is read from server configuration (`GitHub:Token`: user-secrets or environment variable) and attached only to requests to the configured GitHub API host. It is never rendered, logged, put in a URL or stored in browser storage.
- Log downloads follow GitHub's redirect to blob storage **without** credentials.
- The tool is read-only. Use fine-grained tokens with read-only `Metadata`, `Actions` and `Contents`.
- "Copy context for coding agent" redacts common token/secret patterns and bounds output, but redaction is best-effort: review before pasting elsewhere. Nothing is sent to any external service.
- Browser storage holds only non-sensitive UI preferences (density, inspector state, expanded groups).

## Secret scanning

The repository is protected by a GitGuardian `ggshield` pre-commit hook, GitGuardian and Gitleaks CI workflows and GitHub secret scanning with push protection (see CONTRIBUTING.md). All GitHub Actions are pinned to commit SHAs.

## Not yet implemented

Hosted multi-user mode, per-user repository authorization and GitHub App installation tokens. The credential seam (`IGitHubCredentialSource`) exists, but treat multi-user hosting as unsupported.

