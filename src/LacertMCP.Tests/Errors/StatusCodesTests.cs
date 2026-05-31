using System.Collections.Generic;
using System.Linq;
using LacertMCP.Server.Errors;
using Xunit;

namespace LacertMCP.Tests.Errors
{
    public class StatusCodesTests
    {
        public static IEnumerable<object[]> AllTAPIStatusCodes =>
            Enumerable.Range(0, 29).Select(i => new object[] { i });

        public static IEnumerable<object[]> AllRegistrationCodes =>
            Enumerable.Range(200, 5).Select(i => new object[] { i });

        public static IEnumerable<object[]> AllSyntheticCodes => new[]
        {
            new object[] { 9001 },
            new object[] { 9002 },
            new object[] { 9003 },
            new object[] { 9004 },
            new object[] { 9005 },
            new object[] { 9006 },
            new object[] { 9007 },
            new object[] { 9008 },
            new object[] { 9009 },
            // P15-022 — 9010/9011 (BridgeUnavailableLegacy/SimulationOnlyLegacy)
            // removed; canonical codes are 9201/9202 in the bridge range.
            new object[] { 9012 },
            // P15-007 — InvalidAppId. Distinct from AppNotRegistered (9006) so
            // bad-shape appIds don't send LLM clients into "rerun register"
            // loops that don't fix the actual problem.
            new object[] { 9103 },
            // P15-035 — InternalServerError. Distinct from StatusCodes.Error (0)
            // so the wrapper doesn't misattribute in-server faults as SDK traces.
            new object[] { 9203 },
        };

        [Theory]
        [MemberData(nameof(AllTAPIStatusCodes))]
        public void StatusTable_ContainsEveryTAPIStatusCode(int code)
        {
            Assert.True(
                StatusCodes.StatusTable.ContainsKey(code),
                $"StatusCodes.StatusTable missing TAPIStatus entry for code {code}.");
        }

        [Theory]
        [MemberData(nameof(AllRegistrationCodes))]
        public void StatusTable_ContainsEveryRegistrationCode(int code)
        {
            Assert.True(
                StatusCodes.StatusTable.ContainsKey(code),
                $"StatusCodes.StatusTable missing TPSDKStatus entry for code {code}.");
        }

        [Theory]
        [MemberData(nameof(AllSyntheticCodes))]
        public void SyntheticStatusTable_ContainsEverySyntheticCode(int code)
        {
            Assert.True(
                SyntheticStatusCodes.StatusTable.ContainsKey(code),
                $"SyntheticStatusCodes.StatusTable missing synthetic entry for code {code}.");
        }

        [Fact]
        public void GetStatusInfo_Success_IsSeveritySuccess()
        {
            var info = StatusCodes.GetStatusInfo(1);
            Assert.Equal("Success", info.Name);
            Assert.Equal(Severity.Success, info.Severity);
        }

        [Fact]
        public void GetStatusInfo_NotExposed_IsNamedAndError()
        {
            var info = StatusCodes.GetStatusInfo(7);
            Assert.Equal("NotExposed", info.Name);
            Assert.Equal(Severity.Error, info.Severity);
        }

        [Fact]
        public void GetStatusInfo_ExposedForRead_IsWarningPerAgent05ThirdSection()
        {
            // Per agent 05 §3 justification: access-map informational answers are warning at the
            // table level so the access-map tool doesn't surface isError on a normal answer.
            var info = StatusCodes.GetStatusInfo(8);
            Assert.Equal("ExposedForRead", info.Name);
            Assert.Equal(Severity.Warning, info.Severity);
        }

        [Fact]
        public void GetStatusInfo_AccessGranted_IsSeveritySuccess()
        {
            var info = StatusCodes.GetStatusInfo(201);
            Assert.Equal("AccessGranted", info.Name);
            Assert.Equal(Severity.Success, info.Severity);
        }

        [Fact]
        public void GetStatusInfo_UnknownCode_FallsBackToUnknownErrorEntry()
        {
            var info = StatusCodes.GetStatusInfo(99999);
            Assert.Equal("Unknown(99999)", info.Name);
            Assert.Equal(Severity.Error, info.Severity);
            Assert.Contains("99999", info.UserMessage);
            Assert.NotNull(info.Remediation);
        }

        [Fact]
        public void GetStatusInfo_RegVersionBlocked_UsesPrefixedNameToAvoidTAPICollision()
        {
            // Per agent 05 §1 VersionBlocked collision note + follow-up F2: TPSDKStatus 202 is
            // registered as RegVersionBlocked to disambiguate from TAPIStatus 11 VersionBlocked.
            var reg = StatusCodes.GetStatusInfo(202);
            var tapi = StatusCodes.GetStatusInfo(11);

            Assert.Equal("RegVersionBlocked", reg.Name);
            Assert.Equal("VersionBlocked", tapi.Name);
        }

        public static IEnumerable<object[]> AllDBxErrorCodes => new[]
        {
            new object[] { StatusCodes.DB1Error },
            new object[] { StatusCodes.DB2Error },
            new object[] { StatusCodes.DB3Error },
            new object[] { StatusCodes.DB4Error },
            new object[] { StatusCodes.DB5Error },
            new object[] { StatusCodes.DB6Error },
            new object[] { StatusCodes.DB7Error },
            new object[] { StatusCodes.DB8Error },
        };

        [Theory]
        [MemberData(nameof(AllDBxErrorCodes))]
        public void DBxError_Remediation_MentionsExpectedDbfContextKey_P15_036(int code)
        {
            // P15-036: DBxError remediations must name the offending file via the
            // context.expectedDbf + context.dataPath keys the live bridge populates,
            // not the old generic "check the file on disk" string that named no file.
            var info = StatusCodes.GetStatusInfo(code);
            Assert.NotNull(info.Remediation);
            Assert.Contains("expectedDbf", info.Remediation!);
            Assert.Contains("dataPath", info.Remediation);
        }

        [Theory]
        [MemberData(nameof(AllDBxErrorCodes))]
        public void DBxError_Remediation_DoesNotUseOldGenericStringWithNoFilename_P15_036(int code)
        {
            // Regression guard for P15-036: the pre-fix string was identical across all 8
            // entries and named no file. If anyone reverts to that wording, this fails.
            var info = StatusCodes.GetStatusInfo(code);
            Assert.NotEqual(
                "Check the table file on disk; run Lacerte's index-repair on the affected module/year.",
                info.Remediation);
        }
    }
}
