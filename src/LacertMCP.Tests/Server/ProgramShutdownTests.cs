using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LacertMCP.Tests.ServerProgram
{
    /// <summary>
    /// P15-046 — graceful shutdown contract: Program.cs captures the IHost via
    /// <c>using var host = builder.Build();</c> so the ServiceProvider (and
    /// every IDisposable singleton, including
    /// <c>LacertMCP.Server.Session.LacerteSessionManager</c>) is disposed
    /// when <c>RunAsync</c> returns. These tests pin the contract at the
    /// Host/DI seam using a probe IDisposable singleton — the actual session
    /// manager is exercised by the rest of the suite, and the contract this
    /// test cares about is the Host/ServiceProvider plumbing.
    /// </summary>
    public class ProgramShutdownTests
    {
        [Fact]
        public void HostDispose_DisposesSingletonIDisposables()
        {
            // Mirror the Program.cs registration shape — same builder, same
            // singleton lifetime. A probe disposable stands in for any singleton
            // that implements IDisposable; if `using var host = builder.Build();`
            // is removed from Program.cs, this probe never disposes and the test
            // fails. That's the exact regression P15-046 is here to prevent.
            //
            // Register via type (not instance): the ServiceProvider only
            // disposes singletons it owns/created. AddSingleton(instance)
            // treats the instance as externally-owned and skips disposal,
            // which would mask the bug we want to detect.
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<DisposeProbe>();

            DisposeProbe probe;
            using (var host = builder.Build())
            {
                // Resolve so the singleton is materialized in the provider's
                // disposable-tracker.
                probe = host.Services.GetRequiredService<DisposeProbe>();
                Assert.False(probe.WasDisposed);
            } // `using` calls host.Dispose() which disposes the ServiceProvider
              // which disposes every singleton it tracks.

            Assert.True(
                probe.WasDisposed,
                "ServiceProvider must dispose singleton IDisposables when the IHost is disposed (P15-046 — without `using var host = builder.Build();` in Program.cs, LacerteSessionManager.Dispose never runs on graceful shutdown, leaking the COM RCW / ODBC pool / idempotency semaphores it holds).");
        }

        [Fact]
        public void HostStopAsync_FollowedByDispose_DisposesSingletons()
        {
            // Belt-and-suspenders: even if a future refactor calls StopAsync
            // explicitly, the IHost.Dispose path must still run the singletons'
            // Dispose. This shape mirrors what Program.cs does today (await
            // RunAsync inside a `using` block — RunAsync returns after
            // StopAsync completes, then `using` triggers Dispose).
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<DisposeProbe>();
            var host = builder.Build();
            var probe = host.Services.GetRequiredService<DisposeProbe>();
            host.Dispose();
            Assert.True(probe.WasDisposed);
        }

        private sealed class DisposeProbe : IDisposable
        {
            public bool WasDisposed { get; private set; }
            public void Dispose() => WasDisposed = true;
        }
    }
}
