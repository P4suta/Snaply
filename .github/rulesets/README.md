# Branch rulesets (source of truth)

These JSON files are the version-controlled definition of this repo's branch
[rulesets](https://docs.github.com/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets).
`main`'s protection is meant to live entirely in rulesets — there is no classic
branch protection.

| File | Target | Enforces |
|---|---|---|
| `protect-default-branch.json` | `refs/heads/main` | PR required, `ci-required` + `analyze` status checks (strict), linear history, conversation resolution, no force-push, no deletion |
| `require-signed-commits.json` | all branches except `gh-pages` | signed commits |

> **Application is opt-in and manual.** GitHub does not auto-apply rulesets
> from files in the tree (only org-level rulesets can be imported). These are
> the canonical record and a disaster-recovery template.
>
> ⚠️ **Solo-maintainer note:** `require-signed-commits` will reject any unsigned
> push — configure commit signing (SSH or GPG) **before** applying it, or you
> will lock yourself out of pushing to `main`. `protect-default-branch` requires
> all changes to go through a PR. Apply these only when you are ready to work
> PR-first with signed commits.

Apply a ruleset with `gh`:

```sh
gh api -X POST repos/P4suta/Snaply/rulesets \
  --input .github/rulesets/protect-default-branch.json
gh api -X POST repos/P4suta/Snaply/rulesets \
  --input .github/rulesets/require-signed-commits.json
```

Re-export after a settings change (strips volatile fields), so the tree stays
the source of truth:

```sh
gh api repos/P4suta/Snaply/rulesets/<id> \
  --jq 'del(.id,.node_id,.created_at,.updated_at,._links,.current_user_can_bypass,.source,.source_type)'
```
