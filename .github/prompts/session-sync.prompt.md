---
description: "End-of-session sync: update STATUS.md, README.md, testing-register.md to match reality."
---

# Session Sync

Run this at the end of each coding session to keep project docs accurate.

## 1. Gather current metrics

```bash
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo -v q
cd src/Hermes.Web && npx playwright test tests/smoke.spec.ts --reporter=line
```

Note: F# test count, Playwright test count, any failures.

## 2. Update `.project/STATUS.md`

- Update the **Current State** date and metrics table (test count, branch, docs processed)
- Update **Active Work** to reflect what was done and what's next
- Update **Roadmap** priorities and statuses
- Update **Key Design Docs** if any were added/archived

## 3. Update `README.md`

- Update test count badge: `![Tests](https://img.shields.io/badge/tests-NNN_passing-brightgreen)`
- Update **Solution Structure** if folders changed
- Update **Documentation** table if design docs changed
- Verify dev commands still work

## 4. Update `.project/testing-register.md`

Regenerate the test catalog from source:

```bash
# List all test functions
Select-String -Path tests/Hermes.Tests/*.fs -Pattern '\[<Fact>\]|\[<Property>\]' | 
  Select-Object Filename, LineNumber
```

Update the register with:
- Total test count per file
- New test files added
- Test files removed
- Summary counts table

## 5. Commit

```bash
git add .project/STATUS.md README.md .project/testing-register.md
git commit -m "docs: session sync — update status, readme, testing register"
```

## Checklist

- [ ] Test counts match reality
- [ ] STATUS.md date is current
- [ ] Active work reflects actual next step
- [ ] README badges match test count
- [ ] No references to archived/obsolete docs
- [ ] No references to Avalonia, stage queue tables, file moves, or separate classify stage
