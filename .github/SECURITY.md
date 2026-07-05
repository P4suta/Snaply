# Security Policy

## Reporting a vulnerability

Please report security vulnerabilities **privately** via
[GitHub Security Advisories](https://github.com/P4suta/Snaply/security/advisories/new).
**Do not open a public issue for a vulnerability.**

We aim to acknowledge a report within a few days and to ship a fix or
mitigation as quickly as the severity warrants.

## Supported versions

Snaply is pre-1.0; only the latest release receives security fixes.

| Version | Supported |
| ------- | --------- |
| latest  | ✅        |
| older   | ❌        |

## Scope

Snaply is an unpackaged, self-contained desktop app that runs with the
launching user's privileges. Examples of in-scope reports:

- A crafted image or window title leading to code execution during capture,
  compositing, or PNG export.
- The capture pipeline reading or writing outside the user's expected files.
- A tampered release artifact passing signature / attestation verification.

Out of scope: issues that require an attacker who already controls the local
user account, and the residual limitations documented in the README.
