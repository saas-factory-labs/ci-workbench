# Workflow grouping: `.ciworkbench.yml`

CIWorkbench organizes a repository's GitHub Actions workflows into collapsible groups. Groups come from an optional file named **`.ciworkbench.yml`** in the root of the repository's default branch. CIWorkbench only ever **reads** it (it needs `Contents: Read`); it never writes configuration back.

## Quick start

Create `.ciworkbench.yml`. The first line makes editors validate and autocomplete the file against the published schema, so nobody has to copy a schema file into their repository:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/saas-factory-labs/ci-workbench/main/schemas/ciworkbench.schema.json
version: 1
groups:
  - name: Build & Test
    description: Compile, unit and integration tests
    workflows:
      - .github/workflows/ci.yml
      - .github/workflows/*-tests.yml
  - name: Security/Scanning        # "/" nests a group inside "Security"
    workflows:
      - .github/workflows/codeql.yml
  - name: AI Agents
    description: 'Filled by "# ci-workbench-group: AI Agents" comments in the workflow files'
```

### Editor support

The `# yaml-language-server: $schema=...` directive is understood by the YAML Language Server (Red Hat YAML extension for VS Code, JetBrains IDEs with the YAML plugin, Neovim `yamlls`, Zed and others). If you prefer settings over a comment, map the same URL in VS Code `settings.json`:

```json
{
  "yaml.schemas": {
    "https://raw.githubusercontent.com/saas-factory-labs/ci-workbench/main/schemas/ciworkbench.schema.json": ".ciworkbench.yml"
  }
}
```

> **Availability:** the URL is served by GitHub Raw from the `main` branch of `saas-factory-labs/ci-workbench`. It resolves only after `schemas/ciworkbench.schema.json` has been merged to `main`. Until then editors show a "could not load schema" notice and the file is still read normally by CIWorkbench (the schema is a convenience for editors; CIWorkbench validates the file itself).

## Precedence

For every workflow, the first rule that applies wins:

1. **Central configuration**: the first group in `.ciworkbench.yml` (in file order) whose `workflows` list matches the workflow's path.
2. **Workflow comment**: a line `# ci-workbench-group: <name>` anywhere in the workflow file.
3. **`Ungrouped`**.

Workflows are matched by their **file path**, never by display name, so renaming a workflow's `name:` does not move it. A configuration problem can never hide a workflow: anything not placed by a valid entry falls through to rule 2 and then rule 3.

## The contract

| Field | Type | Required | Rules |
|---|---|---|---|
| `version` | integer | no | Must be `1`. Omitted means `1`, so existing files keep working unchanged. A larger version is reported and the file is ignored (CIWorkbench does not guess). |
| `groups` | list | no | Groups in display order. |
| `groups[].name` | text | **yes** | 1-200 characters. `/` nests groups (`Security/Scanning`). Must not start or end with `/` or whitespace and must not contain `//`. |
| `groups[].description` | text | no | Up to 500 characters. |
| `groups[].workflows` | list of text | no | Workflow file paths, e.g. `.github/workflows/ci.yml`. Case-insensitive, full path. `*` matches characters inside **one** path segment (it never crosses `/`). `**`, `?`, `[...]`, `{...}` and whitespace are not allowed. May be empty. |

No other fields are allowed at any level. Values must have the stated type: put quotes around a name that looks like a number or boolean (`name: "2024"`). An empty or comment-only file means "no configuration".

The machine-readable form of this table is [`schemas/ciworkbench.schema.json`](../../schemas/ciworkbench.schema.json) (JSON Schema draft 2020-12). CIWorkbench's own validation and that schema are tested against the same set of valid and invalid files, so they cannot drift apart.

## What CIWorkbench reports

Every message names the file, the line and the problem. They appear in the repository workspace above the groups.

**Errors** are violations of the contract above (the JSON Schema rejects the same files). The affected group is skipped, the other groups still apply, and a `group-skipped` warning names the skipped group.

| Code | Meaning | Example |
|---|---|---|
| `syntax-error` | Not valid YAML; the whole file is ignored | `.ciworkbench.yml line 3: is not valid YAML and was ignored entirely: ...` |
| `invalid-type` | Wrong kind of value | `groups[2].name must be text (found a number); put quotes around it ...` |
| `unknown-field` | A field that is not part of the contract, with a suggestion when it looks like a typo | `unknown field 'workflow' in groups[1] (did you mean 'workflows'?)` |
| `missing-field` | A group without `name` | `groups[3] is missing the required field 'name'` |
| `invalid-value` | Right type, not allowed (bad name, `**`, whitespace, too long) | `workflow pattern '.github/**/*.yml' ... is not valid: '**' is not supported` |
| `unsupported-version` | `version` is not `1`; the whole file is ignored | `'version: 2' is not supported by this CIWorkbench (supported: 1); the file was ignored` |

**Warnings** are valid but suspicious. They need the repository's actual workflows or compare entries with each other, which JSON Schema cannot express, so they only appear at run time.

| Code | Meaning |
|---|---|
| `duplicate-group` | The same group name is defined twice; the entries are merged and the first description wins. |
| `duplicate-pattern` | The same path or pattern is listed twice, or in two groups (the first group wins). |
| _(grouping check)_ | A path or pattern matches **no** workflow in the repository (typo or renamed file). |
| _(grouping check)_ | One workflow matches **several** groups; it is placed in the first and the others are named. |
| _(grouping check)_ | A group matched no workflows at all. |
| `group-skipped` | A group was skipped because of an error. |

## Versioning policy

`version: 1` is the current and only format. Changes that keep existing files valid (new optional fields, new optional values) stay in version 1. Anything that would change the meaning of an existing file gets `version: 2`; files that declare a version CIWorkbench does not know are ignored with an explicit error rather than half-interpreted.

## Not included

CIWorkbench does not edit or generate this file, has no endpoint that writes it, and never requests write permissions. Edit it in your repository like any other file.