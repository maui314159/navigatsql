# Security Policy

## Supported versions

navigaT-SQL is pre-1.0. Security fixes land on `main` and the latest release.

## Reporting a vulnerability

Please **do not** open a public issue for security reports.

Use GitHub's [private vulnerability reporting](https://github.com/maui314159/navigatsql/security/advisories/new)
to disclose privately. We'll acknowledge receipt, investigate, and coordinate a fix and a
disclosure timeline with you.

## Scope

navigaT-SQL is a static analyzer: it **parses** T-SQL and C# source and emits JSON. It does
not connect to databases, execute SQL, or run the code it analyzes. The most relevant risks
are around parsing untrusted input — crashes, hangs, or excessive memory/CPU on specific
pathological files. Reports of such inputs are in scope and welcome.
