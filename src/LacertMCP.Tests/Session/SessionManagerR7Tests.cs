using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LacertMCP.Server.Bridge;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Resilience;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Server.Tools;
using Xunit;

namespace LacertMCP.Tests.Session
{
    /// <summary>
    /// Round-7 deep-review regressions for the session / bridge layer.
    /// Each test names the R7-NN finding it pins.
    /// </summary>
    [Collection(LacertMCP.Tests.AccessMapMutationCollection.Name)]
    public class SessionManagerR7Tests
    {
        private const string SeedField = "C1_15";

        public SessionManagerR7Tests()
        {
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(module: 'I', table: 1, field: SeedField);
            AccessMapInMemorySeed.EnsureWritableFieldSeeded(module: 'C', table: 3, field: "C3_5");
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static async Task<LacerteSessionManager> NewRegisteredSimAsync(
            string dataPath = SeedData.DataPathIData2024, int year = 2024, char module = 'I')
        {
            var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(dataPath, year, module);
            return mgr;
        }

        private static LacerteSessionManager NewLiveManagerWithFake(ILacerteBridge bridge)
        {
            var ctor = typeof(LacerteSessionManager).GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(ILacerteBridge), typeof(IDictionary<string, string?>) },
                modifiers: null);
            Assert.NotNull(ctor);
            return (LacerteSessionManager)ctor!.Invoke(
                new object?[] { bridge, new Dictionary<string, string?>() });
        }

        private static DetailRow NewRow(double amount, string description)
            => new DetailRow("11", "", "1", "", "", "", "Source", "T", "", amount, description);

        // -----------------------------------------------------------------
        // R7-01 — live non-Success statuses raise (sim/live parity)
        // -----------------------------------------------------------------

        [Fact]
        public async Task R701_LiveWriteField_NonSuccessStatus_ThrowsStructuredError_NotCached()
        {
            var fake = new ScriptedBridge();
            fake.UpdateTaxReturnStatuses.Enqueue(StatusCodes.DBFNotAccessable);
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetRegisteredForTest(true);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');

            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteField(1, "JOHNS", SeedField, "x", idempotencyKey: "r7-01"));
            Assert.Equal(StatusCodes.DBFNotAccessable, ex.StatusCode);
            Assert.False(mgr.TryGetIdempotencyEntryForTest("r7-01", out _));

            // Retry after the operator closes the return reaches the wire.
            var ok = await mgr.WriteField(1, "JOHNS", SeedField, "x", idempotencyKey: "r7-01");
            Assert.Equal(StatusCodes.Success, ok);
            Assert.Equal(2, fake.UpdateTaxReturnCalls);
        }

        [Fact]
        public async Task R701_LiveFieldWriteBulk_NonSuccessRow_CountedAsFailure_AndStops()
        {
            var fake = new ScriptedBridge();
            fake.UpdateTaxReturnStatuses.Enqueue(StatusCodes.DBFNotAccessable);
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetRegisteredForTest(true);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');
            var tool = new FieldWriteBulkTools(mgr);

            var json = await tool.LacerteFieldWriteBulk(1, new[]
            {
                new FieldWriteBulkTools.BulkWriteEntry { ClientNumber = "JOHNS", Field = SeedField, Value = "a" },
                new FieldWriteBulkTools.BulkWriteEntry { ClientNumber = "SMITH", Field = SeedField, Value = "b" },
            });

            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("result");
            var items = result.GetProperty("perItem").EnumerateArray().ToArray();
            Assert.False(items[0].GetProperty("success").GetBoolean(), json);
            Assert.Equal(StatusCodes.DBFNotAccessable, items[0].GetProperty("statusCode").GetInt32());
            // Default stop-on-error: the second row never reached the wire.
            Assert.Equal(1, fake.UpdateTaxReturnCalls);
        }

        // -----------------------------------------------------------------
        // R7-02 — backend mode is part of the idempotency fingerprint
        // -----------------------------------------------------------------

        [Fact]
        public async Task R702_SimCachedKey_IsNotReplayedByLiveProcess()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lacerte-r702-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "idempotency-cache.json");
            try
            {
                Directory.CreateDirectory(dir);
                var env = new Dictionary<string, string?>
                {
                    ["LACERTE_IDEMPOTENCY_PERSIST"] = "1",
                    ["LACERTE_IDEMPOTENCY_PERSIST_PATH"] = path,
                };
                using (var sim = new LacerteSessionManager(env))
                {
                    await sim.Register();
                    sim.SetContext(SeedData.DataPathIData2024, 2024, 'I');
                    await sim.WriteDetail("1001", NewRow(1.0, "r7-02"), idempotencyKey: "r7-02");
                }

                var fake = new ScriptedBridge();
                var ctor = typeof(LacerteSessionManager).GetConstructor(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(ILacerteBridge), typeof(IDictionary<string, string?>) }, null);
                using var live = (LacerteSessionManager)ctor!.Invoke(new object?[] { fake, env });
                live.SetRegisteredForTest(true);
                live.SetContext(SeedData.DataPathIData2024, 2024, 'I');

                // Same key + byte-identical args + same tuple, different mode:
                // must surface 9102, never a silent cross-mode replay.
                var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                    () => live.WriteDetail("1001", NewRow(1.0, "r7-02"), idempotencyKey: "r7-02"));
                Assert.Equal(SyntheticStatusCodes.IdempotencyConflict, ex.StatusCode);
                Assert.Equal(0, fake.WriteDetailCalls);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void R702_DefaultSimCacheFile_IsSeparateFromLive()
        {
            var appData = Path.Combine(Path.GetTempPath(), "lacerte-r702b-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var sim = new LacerteSessionManager(new Dictionary<string, string?> { ["APPDATA"] = appData });
                var p = sim.IdempotencyCachePath;
                Assert.NotNull(p);
                Assert.EndsWith("idempotency-cache.sim.json", p);
            }
            finally
            {
                try { Directory.Delete(appData, recursive: true); } catch { }
            }
        }

        // -----------------------------------------------------------------
        // R7-05 — persisted cache is capped at load
        // -----------------------------------------------------------------

        [Fact]
        public void R705_Load_CapsAtMaxEntries_KeepingNewest()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lacerte-r705-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "idempotency-cache.json");
            try
            {
                Directory.CreateDirectory(dir);
                int n = LacerteSessionManager.IdempotencyCacheMaxEntries + 50;
                var now = DateTimeOffset.UtcNow;
                var entries = Enumerable.Range(0, n).Select(i => new
                {
                    key = "k" + i,
                    fingerprint = "f" + i,
                    resultType = "int",
                    resultValue = StatusCodes.Success,
                    // k0 is the oldest; the newest MaxEntries survive.
                    createdAt = now.AddSeconds(-(n - i)),
                }).ToArray();
                File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, entries }));

                using var mgr = new LacerteSessionManager(new Dictionary<string, string?>
                {
                    ["LACERTE_IDEMPOTENCY_PERSIST"] = "1",
                    ["LACERTE_IDEMPOTENCY_PERSIST_PATH"] = path,
                });
                Assert.Equal(LacerteSessionManager.IdempotencyCacheMaxEntries, mgr.IdempotencyCacheCount);
                Assert.False(mgr.TryGetIdempotencyEntryForTest("k0", out _));
                Assert.True(mgr.TryGetIdempotencyEntryForTest("k" + (n - 1), out _));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        // -----------------------------------------------------------------
        // R7-06 / R7-14 — clientNumber trim + blank rejection
        // -----------------------------------------------------------------

        [Fact]
        public async Task R706_ReadClient_TrimsLikeReadTable()
        {
            using var mgr = await NewRegisteredSimAsync();
            Assert.NotNull(await mgr.ReadClient("1001 "));
            Assert.NotNull(await mgr.ReadClient(" 1001"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task R714_SingleClientReads_RejectBlankClientNumber(string? clientNumber)
        {
            using var mgr = await NewRegisteredSimAsync();
            foreach (Func<Task> call in new Func<Task>[]
            {
                () => mgr.ReadClient(clientNumber!),
                () => mgr.ReadSubclients(clientNumber!),
                () => mgr.ReadStates(clientNumber!),
                () => mgr.ReadStatuses(clientNumber!),
                () => mgr.ReadForms(clientNumber!),
                () => mgr.ReadDetail(clientNumber!),
            })
            {
                var ex = await Assert.ThrowsAsync<LacerteSDKError>(call);
                Assert.Equal(SyntheticStatusCodes.InvalidArgument, ex.StatusCode);
            }
        }

        [Fact]
        public async Task R714_SubclientTool_BlankClientNumber_DoesNotReturnEveryClient()
        {
            using var mgr = await NewRegisteredSimAsync(SeedData.DataPathPData2024, 2024, 'P');
            var json = await new SubclientsTools(mgr).LacerteSubclientRead("");
            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean(), json);
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, doc.RootElement.GetProperty("statusCode").GetInt32());
        }

        // -----------------------------------------------------------------
        // R7-07 — detail read is safe against a concurrent append
        // -----------------------------------------------------------------

        [Fact]
        public async Task R707_ConcurrentDetailReadAndWrite_NeverThrows()
        {
            using var mgr = await NewRegisteredSimAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            var errors = 0;
            var writer = Task.Run(async () =>
            {
                int i = 0;
                while (!cts.IsCancellationRequested)
                {
                    await mgr.WriteDetail("1001", NewRow(i, "c" + i++));
                }
            });
            var reader = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try { await mgr.ReadDetail("1001"); }
                    catch (Exception) { Interlocked.Increment(ref errors); }
                }
            });
            await Task.WhenAll(writer, reader);
            Assert.Equal(0, errors);
        }

        // -----------------------------------------------------------------
        // R7-08 — lowercase stateAbbrev writes like it reads
        // -----------------------------------------------------------------

        [Fact]
        public async Task R708_LowercaseStateAbbrev_WriteSucceeds()
        {
            using var mgr = await NewRegisteredSimAsync(SeedData.DataPathCData2024, 2024, 'C');
            var status = await mgr.WriteField(3, "1001", "C3_5", "v", stateAbbrev: " ca ");
            Assert.Equal(StatusCodes.Success, status);
            var rows = await mgr.ReadTable(3, "1001", "CA");
            Assert.Equal("v", rows.Single()["C3_5"]);
        }

        // R7-L1 — sim state rows carry the state in C3_G1, the column the
        // live SQL filters on (SqlTemplates.Data3OldFormByState).
        [Fact]
        public async Task R7L1_SimStateRow_CarriesStateInGroupColumn()
        {
            using var mgr = await NewRegisteredSimAsync(SeedData.DataPathCData2024, 2024, 'C');
            var row = (await mgr.ReadTable(3, "1001", "CA")).Single();
            Assert.Equal("CA", row["C3_G1"]);
            Assert.Contains("C3_G1 = 'CA'", LacertMCP.Server.Odbc.SqlTemplates.Data3OldFormByState("1001", "CA"));
        }

        // -----------------------------------------------------------------
        // R7-10 — concurrent same-ClientId creates: exactly one succeeds
        // -----------------------------------------------------------------

        [Fact]
        public async Task R710_ConcurrentCreatesOfSameClientId_ExactlyOneSucceeds()
        {
            for (int trial = 0; trial < 20; trial++)
            {
                using var mgr = await NewRegisteredSimAsync();
                var args = new CreateClientArgs("DUP" + trial, "Individual", "F", "L", "e@x.com");
                using var go = new ManualResetEventSlim(false);
                var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
                {
                    go.Wait();
                    try { return (await mgr.CreateClient(args)).Status; }
                    catch (LacerteSDKError e) { return e.StatusCode; }
                })).ToArray();
                go.Set();
                var results = await Task.WhenAll(tasks);
                Assert.Equal(1, results.Count(r => r == StatusCodes.Success));
            }
        }

        // -----------------------------------------------------------------
        // R7-35 — no sim read observes a dry-run's write→undo window
        // -----------------------------------------------------------------

        private static bool IsDry(IReadOnlyDictionary<string, object?> row)
            => row.TryGetValue(SeedField, out var v) && Equals(v, "DRYRUN");

        [Fact]
        public async Task R735_ConcurrentReads_NeverSeeDryRunFieldValue()
        {
            using var mgr = await NewRegisteredSimAsync();
            await mgr.WriteField(1, "1001", SeedField, "REAL");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            int leaks = 0;
            var dry = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                    await mgr.WriteField(1, "1001", SeedField, "DRYRUN", dryRun: true);
            });
            var read = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var c = await mgr.ReadClient("1001");
                    var t = await mgr.ReadTable(1, "1001");
                    var l = await mgr.ListClients(null, int.MaxValue);
                    if (IsDry(c!) || t.Any(IsDry) || l.Any(IsDry))
                    {
                        Interlocked.Increment(ref leaks);
                    }
                }
            });
            await Task.WhenAll(dry, read);
            Assert.Equal(0, leaks);
        }

        // R7-35b — the reproduced leak: ListClients snapshotted
        // Clients.Values BEFORE entering the gate, so a dry-run create's
        // phantom client was listed (~1-in-80 iterations under contention).
        [Fact]
        public async Task R735b_ConcurrentDryRunCreates_NeverListPhantomClients()
        {
            using var mgr = await NewRegisteredSimAsync();
            var baseline = (await mgr.ListClients(null, int.MaxValue)).Count;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int phantoms = 0, iterations = 0;
            var creators = Enumerable.Range(0, 2).Select(w => Task.Run(async () =>
            {
                int i = 0;
                while (!cts.IsCancellationRequested)
                {
                    await mgr.CreateClient(
                        new CreateClientArgs("PH" + w + (i++ % 1000), "Individual", "F", "L", "e@x.com"),
                        dryRun: true);
                }
            })).ToArray();
            var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    Interlocked.Increment(ref iterations);
                    if ((await mgr.ListClients(null, int.MaxValue)).Count != baseline)
                        Interlocked.Increment(ref phantoms);
                }
            })).ToArray();
            await Task.WhenAll(creators.Concat(readers));
            Assert.True(iterations > 100, "reader loop barely ran: " + iterations);
            Assert.Equal(0, phantoms);
        }

        [Fact]
        public async Task R728_LiveCreate_SuccessWithoutNumber_CarriesHint()
        {
            var fake = new ScriptedBridge { CreateClientNumber = null };
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetRegisteredForTest(true);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');
            var json = await new CreateClientTools(mgr).LacerteClientCreate(
                "NONUM", "Individual", "First", "Last", "e@x.com");
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("result");
            Assert.Equal(StatusCodes.Success, result.GetProperty("statusCode").GetInt32());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("clientNumber").ValueKind);
            Assert.Contains("WAS created", result.GetProperty("clientNumberHint").GetString());
        }

        // -----------------------------------------------------------------
        // R7-11 — client list order is deterministic
        // -----------------------------------------------------------------

        [Fact]
        public async Task R711_ListClients_OrderedByClientNumber()
        {
            using var mgr = await NewRegisteredSimAsync();
            for (int i = 0; i < 30; i++)
            {
                await mgr.CreateClient(new CreateClientArgs("ORD" + i, "Individual", "F", "L", "e@x.com"));
            }
            var numbers = (await mgr.ListClients(null, int.MaxValue))
                .Select(r => (string)r["clientNumber"]!).ToList();
            Assert.Equal(numbers.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), numbers);
        }

        // -----------------------------------------------------------------
        // R7-12 — bridge content rules apply in simulation too
        // -----------------------------------------------------------------

        [Fact]
        public async Task R712_Sim_FieldValueOver255_Rejected_9015()
        {
            using var mgr = await NewRegisteredSimAsync();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteField(1, "1001", SeedField, new string('x', 256)));
            Assert.Equal(SyntheticStatusCodes.FieldWriteInvalid, ex.StatusCode);
        }

        [Fact]
        public async Task R712_Sim_FieldValueControlChar_Rejected_9015()
        {
            using var mgr = await NewRegisteredSimAsync();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteField(1, "1001", SeedField, "a\u0001b"));
            Assert.Equal(SyntheticStatusCodes.FieldWriteInvalid, ex.StatusCode);
        }

        [Fact]
        public async Task R712_Sim_DetailDescriptionOver255_Rejected()
        {
            using var mgr = await NewRegisteredSimAsync();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteDetail("1001", NewRow(1, new string('d', 300))));
            Assert.Equal(SyntheticStatusCodes.DetailWriteInvalid, ex.StatusCode);
        }

        [Fact]
        public async Task R712_Sim_CreateClientNameOver50_Rejected_24()
        {
            using var mgr = await NewRegisteredSimAsync();
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.CreateClient(new CreateClientArgs("LONGN", "Individual", new string('f', 51), "L", "e@x.com")));
            Assert.Equal(StatusCodes.InValidClientName, ex.StatusCode);
        }

        // -----------------------------------------------------------------
        // R7-13 — live dry-run fails where the commit would
        // -----------------------------------------------------------------

        [Fact]
        public async Task R713_LiveDryRun_PreTy21_RefusedLikeCommit()
        {
            var fake = new ScriptedBridge { CanUseNew = false };
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetRegisteredForTest(true);
            mgr.SetContext(@"C:\Lacerte\20data", 2020, 'I');
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteField(1, "JOHNS", SeedField, "x", dryRun: true));
            Assert.Equal(SyntheticStatusCodes.SimulationOnly, ex.StatusCode);
        }

        [Fact]
        public async Task R713_LiveDryRun_BadClientNumberShape_RefusedLikeCommit()
        {
            var fake = new ScriptedBridge();
            using var mgr = NewLiveManagerWithFake(fake);
            mgr.SetRegisteredForTest(true);
            mgr.SetContext(@"C:\Lacerte\24data", 2024, 'I');
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteField(1, "jo-hns", SeedField, "x", dryRun: true));
            Assert.Equal(StatusCodes.InValidClientName, ex.StatusCode);
            Assert.Equal(0, fake.UpdateTaxReturnCalls);
        }

        // -----------------------------------------------------------------
        // R7-15 — sinceUtc honors the full timestamp
        // -----------------------------------------------------------------

        [Fact]
        public async Task R715_EfStatusSince_UsesExactTimestamp()
        {
            using var mgr = await NewRegisteredSimAsync();
            // PEND's seeded StatusDate is 2024-04-15 (00:00 UTC).
            var onBoundary = await mgr.ReadEFStatus(new DateTime(2024, 4, 15, 0, 0, 0, DateTimeKind.Utc));
            Assert.Contains(onBoundary, r => (string?)r["ClientId"] == "1002");
            var afterBoundary = await mgr.ReadEFStatus(new DateTime(2024, 4, 15, 14, 30, 0, DateTimeKind.Utc));
            Assert.DoesNotContain(afterBoundary, r => (string?)r["ClientId"] == "1002");
        }

        // -----------------------------------------------------------------
        // R7-20 — dataPath canonicalization + drive-relative rejection
        // -----------------------------------------------------------------

        [Theory]
        [InlineData(@"C:\Lacerte\24Tax\IData\")]
        [InlineData("C:/Lacerte/24Tax/IData")]
        [InlineData(@"c:\lacerte\24tax\idata\\")]
        public async Task R720_EquivalentDataPathSpellings_OpenTheSameCell(string spelling)
        {
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            await mgr.Register();
            mgr.SetContext(spelling, 2024, 'I');
            Assert.Equal(2, (await mgr.ListClients(null, int.MaxValue)).Count);
            Assert.False(mgr.SessionStatus().Context!.DataPath.EndsWith(@"\", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(@"C:Lacerte\24Tax\IData")]
        [InlineData(@"\Lacerte\24Tax\IData")]
        [InlineData("C:")]
        public void R720_DriveRelativeOrDrivelessPaths_Rejected(string path)
        {
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var ex = Assert.Throws<LacerteSDKError>(() => mgr.SetContext(path, 2024, 'I'));
            Assert.Equal(SyntheticStatusCodes.InvalidDataPath, ex.StatusCode);
        }

        [Fact]
        public void R720_DriveRoot_StaysRooted()
        {
            Assert.Equal(@"C:\", LacerteSessionManager.CanonicalizeDataPath(@"C:\"));
            Assert.Equal(@"C:\Lacerte", LacerteSessionManager.CanonicalizeDataPath(@"C:\Lacerte\\"));
        }

        // -----------------------------------------------------------------
        // R7-22 — can_use_new_apis validates year + module
        // -----------------------------------------------------------------

        [Theory]
        [InlineData(2030, "I", SyntheticStatusCodes.TaxYearTooNew)]
        [InlineData(1999, "I", SyntheticStatusCodes.TaxYearTooOld)]
        [InlineData(2024, "z", StatusCodes.InValidClientName)]
        public async Task R722_CanUseNewApis_RejectsWhatSetContextRejects(int year, string module, int expected)
        {
            using var mgr = await NewRegisteredSimAsync();
            var json = await new SessionTools(mgr).LacerteCanUseNewApis(year, module);
            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean(), json);
            Assert.Equal(expected, doc.RootElement.GetProperty("statusCode").GetInt32());
        }

        // -----------------------------------------------------------------
        // R7-17 — set_context falls back to LACERTE_DEFAULT_TAX_YEAR
        // -----------------------------------------------------------------

        [Fact]
        public async Task R717_SetContext_OmittedTaxYear_UsesEnvDefault()
        {
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?> { ["LACERTE_DEFAULT_TAX_YEAR"] = "2024" });
            await mgr.Register();
            var json = await new SessionTools(mgr).LacerteSetContext(SeedData.DataPathIData2024, module: "I");
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean(), json);
            Assert.Equal(2024, doc.RootElement.GetProperty("result").GetProperty("taxYear").GetInt32());
        }

        [Fact]
        public async Task R717_SetContext_OmittedTaxYear_NoEnvDefault_InvalidArgument()
        {
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var json = await new SessionTools(mgr).LacerteSetContext(SeedData.DataPathIData2024, module: "I");
            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean(), json);
            Assert.Equal(SyntheticStatusCodes.InvalidArgument, doc.RootElement.GetProperty("statusCode").GetInt32());
        }

        // -----------------------------------------------------------------
        // R7-25 — bulk rows pinned to the batch's context
        // -----------------------------------------------------------------

        [Fact]
        public async Task R725_WriteWithStaleExpectedContext_ContextMismatch()
        {
            using var mgr = await NewRegisteredSimAsync();
            var pinned = mgr.SessionStatus().Context!;
            mgr.SetContext(SeedData.DataPathIData2020, 2020, 'I');
            var ex = await Assert.ThrowsAsync<LacerteSDKError>(
                () => mgr.WriteField(1, "1001", SeedField, "x", expectedContext: pinned));
            Assert.Equal(SyntheticStatusCodes.ContextMismatch, ex.StatusCode);
            // Re-setting the identical tuple is NOT a mismatch.
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');
            Assert.Equal(StatusCodes.Success,
                await mgr.WriteField(1, "1001", SeedField, "x", expectedContext: pinned));
        }

        // -----------------------------------------------------------------
        // R7-27 — AutoReconnect sees through the bridge's 9201 wrapper
        // -----------------------------------------------------------------

        [Fact]
        public void R727_IsTransient_UnwrapsBridgeUnavailableWithTransientCom()
        {
            var transient = new COMException("retry later", unchecked((int)0x8001010A));
            var wrapped = new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                new Dictionary<string, object?>(), transient);
            Assert.True(AutoReconnect.IsTransient(wrapped));

            var nonTransient = new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                new Dictionary<string, object?>(), new COMException("boom", unchecked((int)0x80004005)));
            Assert.False(AutoReconnect.IsTransient(nonTransient));

            // Any other TAPIStatus-bearing error is never retried, even with a
            // transient inner exception.
            var notSignedIn = new LacerteSDKError(StatusCodes.NotSignedIn,
                new Dictionary<string, object?>(), transient);
            Assert.False(AutoReconnect.IsTransient(notSignedIn));
        }

        [Fact]
        public async Task R727_WithRetry_RetriesWrappedTransient()
        {
            int calls = 0;
            var result = await AutoReconnect.WithRetry<int>(() =>
            {
                if (Interlocked.Increment(ref calls) < 3)
                {
                    throw new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable,
                        new Dictionary<string, object?>(),
                        new COMException("retry later", unchecked((int)0x8001010A)));
                }
                return Task.FromResult(StatusCodes.Success);
            });
            Assert.Equal(StatusCodes.Success, result);
            Assert.Equal(3, calls);
        }

        // -----------------------------------------------------------------
        // Scripted fake bridge
        // -----------------------------------------------------------------

        private sealed class ScriptedBridge : ILacerteBridge
        {
            public int WriteDetailCalls;
            public int UpdateTaxReturnCalls;
            public bool CanUseNew = true;
            public string? CreateClientNumber = "1";
            public readonly Queue<int> UpdateTaxReturnStatuses = new Queue<int>();

            public int Register(string appName, string appId, int taxYear) => StatusCodes.AccessGranted;
            public bool CanUseNewApis(string appId, int year, char module) => CanUseNew;

            public IReadOnlyList<IReadOnlyDictionary<string, object?>> ListClients(string dataPath, int year, char module)
                => Array.Empty<IReadOnlyDictionary<string, object?>>();

            public IReadOnlyDictionary<string, object?>? ReadClient(string dataPath, int year, char module, string clientNumber)
                => null;

            public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadTable(
                string dataPath, int year, char module, int table,
                string? clientNumber = null, string? stateAbbrev = null)
                => Array.Empty<IReadOnlyDictionary<string, object?>>();

            public int WriteField(string appId, string dataPath, int year, char module,
                int table, int rowNum, string field, object? value) => StatusCodes.Success;

            public int UpdateTaxReturn(string appId, string dataPath, int year, char module,
                int table, string clientNumber, string field, object? value,
                int? subClientId, string? stateAbbrev)
            {
                Interlocked.Increment(ref UpdateTaxReturnCalls);
                lock (UpdateTaxReturnStatuses)
                {
                    return UpdateTaxReturnStatuses.Count > 0 ? UpdateTaxReturnStatuses.Dequeue() : StatusCodes.Success;
                }
            }

            public int WriteDetail(string appId, string dataPath, int year, char module,
                string clientNumber, DetailRow row, int? replaceIndex)
            {
                Interlocked.Increment(ref WriteDetailCalls);
                return StatusCodes.Success;
            }

            public BridgeCreateClientResult CreateClient(string appId, string dataPath, int year, char module,
                string clientId, string taxType, string? firstName, string? lastName, string? emailId)
                => new BridgeCreateClientResult(StatusCodes.Success, CreateClientNumber);

            public void Dispose() { }
        }
    }
}
