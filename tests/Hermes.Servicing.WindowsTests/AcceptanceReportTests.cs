using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

public sealed class AcceptanceReportTests
{
    [Fact]
    public void No_evidence_is_not_success() =>
        Assert.Equal(1, ReportContract.ExitCode([]));

    [Fact]
    public void A_blocked_old_engine_outcome_cannot_be_promoted_by_other_passes() =>
        Assert.Equal(3, ReportContract.ExitCode(
            [CaseState.Passed, CaseState.Passed, CaseState.Blocked]));

    [Fact]
    public void Failure_dominates_blocked_evidence() =>
        Assert.Equal(1, ReportContract.ExitCode(
            [CaseState.Passed, CaseState.Blocked, CaseState.Failed]));

    [Fact]
    public void Only_all_completed_component_cases_have_success_status() =>
        Assert.Equal(0, ReportContract.ExitCode(
            [CaseState.Passed, CaseState.Passed]));
}
