---
description: "Structured investigation of a test failure."
---

# Debug Test Failure

## Steps

1. **Reproduce**: Run the failing test in isolation:

    ```
    dotnet test --filter "FullyQualifiedName~<test_name>"
    ```

2. **Classify**:
    - **Build error**: compilation issue — read the error, fix the source
    - **Assertion failure**: logic bug — read the test to understand the expectation
    - **Timeout / hang**: async issue — check for deadlocks, missing cancellation tokens
    - **Environment**: missing config, database, Ollama unavailable

3. **Read the Test**: Understand what the test expects. Read the test file.

4. **Read the Source**: Navigate to the code under test. Understand what it actually does.

5. **Identify the Gap**: What's the difference between expected and actual behaviour?

6. **Fix**: Apply the minimal fix. If the test expectation is wrong, fix the test with a comment explaining why.

7. **Verify**: Run the full test suite to ensure no regressions:

    ```
    dotnet test
    ```

8. **Update Register**: If the test was modified, update `.project/testing-register.md`.
