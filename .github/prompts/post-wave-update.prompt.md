---
description: "Post-wave maintenance: update STATUS.md, wave files, README badges, and push."
---

# Post-Wave Update

Run this after reviewing and approving a wave's work.

## Steps

1. **Read current state**:
   ```
   dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo --no-build --no-restore
   ```
   Note: test count, failures, skips.

2. **Run coverage** (if code changed):
   ```
   Remove-Item -Recurse -Force TestResults -ErrorAction SilentlyContinue
   dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo --collect:"XPlat Code Coverage" --results-directory TestResults
   ```
   Parse:
   ```
   $xml = [xml](Get-Content TestResults/*/coverage.cobertura.xml)
   Write-Host "Line: $([math]::Round([float]$xml.coverage.'line-rate' * 100, 1))%  Branch: $([math]::Round([float]$xml.coverage.'branch-rate' * 100, 1))%"
   ```

3. **Update the completed wave file** (`.project/waves/wave-N.md`):
   - Set status to `✅ **Done**`
   - Mark all tasks as ✅ in the table
   - Append a dated log entry summarising what was built

4. **Update STATUS.md** (`.project/STATUS.md`):
   - Update test count and coverage in the metrics table
   - Move completed wave to ✅ in the roadmap table
   - Set next wave as active
   - Clear or update blockers

5. **Update README badges** (if metrics changed significantly):
   - Test count badge: `[![Tests](https://img.shields.io/badge/tests-{N}_passing-brightgreen)](#testing)`
   - Coverage badge: `[![Coverage](https://img.shields.io/badge/coverage-{N}%25_line-yellow)](#testing)`
   - Update the Testing table (tests, line coverage, branch coverage)

6. **Archive completed wave prompt** (if a standalone prompt was used):
   ```
   Move-Item .github/prompts/wave{N}-*.prompt.md .project/archive/
   ```

7. **Commit and push**:
   ```
   git add -A
   git commit -m "docs: post-wave update — Wave {N} complete, {test_count} tests, {coverage}% line"
   git push
   ```

## Checklist

- [ ] Tests pass (count + 0 failures)
- [ ] Coverage measured
- [ ] Wave file updated (status + log)
- [ ] STATUS.md updated (metrics + roadmap)
- [ ] README badges current
- [ ] Old prompt archived (if applicable)
- [ ] Committed and pushed
