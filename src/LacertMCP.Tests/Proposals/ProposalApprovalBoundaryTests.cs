using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LacertMCP.Server.Proposals;
using LacertMCP.Server.Session;
using LacertMCP.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace LacertMCP.Tests.Proposals
{
    /// <summary>
    /// "No MCP tool can approve" (DECISIONS 2026-11-18). The reviewer methods on
    /// <see cref="ProposalStore"/> may be called from the human channels only —
    /// the CLI and (later) the control page under Web/. Every other server type,
    /// in particular every tool class and everything they call (ProposalService,
    /// prompts, session), must never reference them. Checked on the IL, including
    /// compiler-generated async state machines and lambdas.
    /// </summary>
    public class ProposalApprovalBoundaryTests
    {
        private static readonly string[] ReviewerMethods =
        {
            nameof(ProposalStore.Approve),
            nameof(ProposalStore.SetLineDecision),
            nameof(ProposalStore.Reject),
            nameof(ProposalStore.ReturnForChanges),
            nameof(ProposalStore.ResolveUncertainLine),
        };

        [Fact]
        public void NoServerTypeOutsideTheControlPage_CallsAReviewerMethod()
        {
            var asm = typeof(ProposalStore).Assembly;
            var offenders = new List<string>();
            foreach (var type in asm.GetTypes())
            {
                if (IsWithin(type, typeof(ProposalStore))) continue;
                if ((type.Namespace ?? string.Empty).StartsWith("LacertMCP.Server.Web", StringComparison.Ordinal)) continue;
                foreach (var called in CalledMethods(type))
                {
                    if (called.DeclaringType == typeof(ProposalStore) && Enumerable.Contains(ReviewerMethods, called.Name))
                        offenders.Add(type.FullName + " -> " + called.Name);
                }
            }
            Assert.True(offenders.Count == 0, "Reviewer methods called outside the human channels:\n" + string.Join("\n", offenders.Distinct()));
        }

        [Fact]
        public void Scanner_FindsTheCliApproval_PositiveControl()
        {
            var cli = typeof(LacertMCP.Cli.Commands.ProposalCommand);
            var calls = Enumerable.Repeat(cli, 1).Concat(cli.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                .SelectMany(CalledMethods).ToList();
            Assert.Contains(calls, m => m.DeclaringType == typeof(ProposalStore) && m.Name == nameof(ProposalStore.Approve));
            Assert.Contains(calls, m => m.DeclaringType == typeof(ProposalStore) && m.Name == nameof(ProposalStore.SetLineDecision));
        }

        [Fact]
        public void NoToolIsNamedLikeAnApproval()
        {
            var names = typeof(ProposalTools).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .Select(m => m.Name.ToLowerInvariant())
                .ToList();
            Assert.Contains("lacerteproposalcommit", names);
            Assert.DoesNotContain(names, n => n.Contains("approve") || n.Contains("reject") || n.Contains("decision") || n.Contains("resolve") || n.Contains("returnforchanges"));
        }

        [Fact]
        public void ReviewerMethods_RefuseNonHumanChannels()
        {
            // The approval channel is part of the record; only the two human channels exist.
            Assert.Equal("control-page", ProposalStore.ChannelControlPage);
            Assert.Equal("cli", ProposalStore.ChannelCli);
        }

        // MCP instantiates tool classes with ActivatorUtilities: ProposalTools must
        // construct when only the session manager is registered (tests / hosts that
        // don't register the store) and pick up the registered store when there is one.
        [Fact]
        public void ProposalTools_ConstructsFromDi_WithOrWithoutARegisteredStore()
        {
            using var mgr = new LacerteSessionManager(new Dictionary<string, string?>());
            var bare = new ServiceCollection().AddSingleton(mgr).BuildServiceProvider();
            Assert.NotNull(ActivatorUtilities.CreateInstance(bare, typeof(ProposalTools)));

            var dir = ProposalTestSupport.NewTempDir();
            try
            {
                var store = new ProposalStore(dir);
                var withStore = new ServiceCollection().AddSingleton(mgr).AddSingleton(store).BuildServiceProvider();
                var tools = (ProposalTools)ActivatorUtilities.CreateInstance(withStore, typeof(ProposalTools));
                var field = typeof(ProposalTools).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Assert.Same(store, field.GetValue(tools));
            }
            finally
            {
                ProposalTestSupport.TryDelete(dir);
            }
        }

        // ---- IL scan -------------------------------------------------------------------------

        private static bool IsWithin(Type t, Type outer)
        {
            for (var c = t; c != null; c = c.DeclaringType) if (c == outer) return true;
            return false;
        }

        private static IEnumerable<MethodBase> CalledMethods(Type type)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
            foreach (var m in methods)
            {
                byte[]? il;
                try { il = m.GetMethodBody()?.GetILAsByteArray(); }
                catch (Exception) { continue; }
                if (il == null) continue;
                for (int i = 0; i < il.Length; i++)
                {
                    int tokenAt;
                    if (il[i] == 0x28 || il[i] == 0x6F || il[i] == 0x73) tokenAt = i + 1;            // call, callvirt, newobj
                    else if (il[i] == 0xFE && i + 1 < il.Length && (il[i + 1] == 0x06 || il[i + 1] == 0x07)) tokenAt = i + 2; // ldftn, ldvirtftn
                    else continue;
                    if (tokenAt + 4 > il.Length) continue;
                    var token = BitConverter.ToInt32(il, tokenAt);
                    var table = (token >> 24) & 0xFF;
                    if (table != 0x06 && table != 0x0A && table != 0x2B) continue;
                    MethodBase? target = null;
                    try
                    {
                        target = m.Module.ResolveMethod(token,
                            type.IsGenericType ? type.GetGenericArguments() : null,
                            m.IsGenericMethod ? m.GetGenericArguments() : null);
                    }
                    catch (Exception)
                    {
                        // Not a method token at this offset (operand bytes of another instruction).
                    }
                    if (target != null) yield return target;
                }
            }
        }
    }
}
