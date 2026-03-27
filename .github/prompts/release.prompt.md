---
description: "Full release ceremony: analyze scope, changelog, version bump, tag, push."
---

# Release Workflow

## Steps

1. **Analyze Scope**: Review commits since last tag:

    ```
    git log $(git describe --tags --abbrev=0)..HEAD --oneline
    ```

2. **Determine Version**: Based on conventional commits:
    - `feat:` with breaking changes → major bump
    - `feat:` → minor bump
    - `fix:`, `docs:`, `refactor:`, `test:` → patch bump

3. **Update Changelog** (`CHANGELOG.md`):
    - Add new section with version and date
    - Group by: Added, Changed, Fixed, Removed
    - Each entry is one line, links to relevant commit or issue

4. **Verify README**: Ensure README accurately reflects current state.

5. **Quality Gate**: `dotnet build` and `dotnet test` must pass.

6. **Commit**: `git commit -am "chore: prepare release vX.Y.Z"`

7. **Tag**: `git tag vX.Y.Z`

8. **Push**: `git push && git push --tags`

9. **Create Release**:
    ```
    gh release create vX.Y.Z --title "vX.Y.Z" --generate-notes
    ```
