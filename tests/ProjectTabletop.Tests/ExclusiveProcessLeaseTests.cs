using System.Diagnostics;
using System.Text;
using ProjectTabletop.App;

namespace ProjectTabletop.Tests;

public class ExclusiveProcessLeaseTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact]
    public void RejectedAttemptsNeitherReleaseTheOwnerNorKeepItsNameAlive()
    {
        string name = NewName();
        using var owner = ExclusiveProcessLease.TryAcquire(name);
        Assert.NotNull(owner);
        for (int attempt = 0; attempt < 10; attempt++)
            Assert.Null(ExclusiveProcessLease.TryAcquire(name));

        owner.Dispose();
        using var successor = ExclusiveProcessLease.TryAcquire(name);
        Assert.NotNull(successor);
        owner.Dispose();
        Assert.Null(ExclusiveProcessLease.TryAcquire(name));
    }

    [Fact]
    public void DifferentNamesCanBeHeldIndependently()
    {
        string firstName = NewName(), secondName = NewName();
        using var first = ExclusiveProcessLease.TryAcquire(firstName);
        using var second = ExclusiveProcessLease.TryAcquire(secondName);
        Assert.NotNull(first);
        Assert.NotNull(second);
        first.Dispose();
        Assert.Null(ExclusiveProcessLease.TryAcquire(secondName));
        using var nextFirst = ExclusiveProcessLease.TryAcquire(firstName);
        Assert.NotNull(nextFirst);
    }

    [Fact]
    public async Task ConcurrentContendersProduceExactlyOneWinner()
    {
        for (int round = 0; round < 8; round++)
        {
            string name = NewName();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var contenders = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return ExclusiveProcessLease.TryAcquire(name);
            })).ToArray();
            start.SetResult();
            var leases = await Task.WhenAll(contenders).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            try
            {
                Assert.Single(leases, lease => lease is not null);
                Assert.Null(ExclusiveProcessLease.TryAcquire(name));
            }
            finally
            {
                foreach (var lease in leases) lease?.Dispose();
            }
            using var successor = ExclusiveProcessLease.TryAcquire(name);
            Assert.NotNull(successor);
        }
    }

    [Fact]
    public void LeaseCanBeDisposedFromAnotherThread()
    {
        string name = NewName();
        using var owner = ExclusiveProcessLease.TryAcquire(name);
        Assert.NotNull(owner);
        Exception? failure = null;
        var disposer = new Thread(() =>
        {
            try { owner.Dispose(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        disposer.Start();
        Assert.True(disposer.Join(TimeSpan.FromSeconds(5)), "Lease disposal blocked on another thread.");
        Assert.Null(failure);
        using var successor = ExclusiveProcessLease.TryAcquire(name);
        Assert.NotNull(successor);
    }

    [Theory(Skip = "The cross-process helper uses Windows PowerShell.", SkipUnless = nameof(IsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessExitReleasesAnUnownedKernelLease(bool terminateAbruptly)
    {
        string name = NewName();
        // This helper owns only a unique test mutex. It never starts Tabletop or touches a camera.
        // The mutex is deliberately not thread-owned: existence alone must exclude another process.
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $created = $false
            $lease = [System.Threading.Mutex]::new($false, '{{name}}', [ref]$created)
            if (-not $created) { throw 'Unique test mutex already exists.' }
            [Console]::Out.WriteLine('READY')
            [Console]::Out.Flush()
            [Console]::In.ReadLine() | Out-Null
            $lease.Dispose()
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        using var helper = Process.Start(start)!;
        try
        {
            string? ready = await helper.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal("READY", ready);
            for (int attempt = 0; attempt < 3; attempt++)
                Assert.Null(ExclusiveProcessLease.TryAcquire(name));

            if (terminateAbruptly) helper.Kill();
            else await helper.StandardInput.WriteLineAsync("release");
            await helper.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (!terminateAbruptly)
                Assert.True(helper.ExitCode == 0, await helper.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
            // Windows forced-exit kernel-object cleanup can trail WaitForExit; the isolated
            // reproduction observed this for up to 10ms. A live owner's lease must still block.
            ExclusiveProcessLease? successor = null;
            var settling = Stopwatch.StartNew();
            while (successor is null && settling.Elapsed < TimeSpan.FromSeconds(2))
            {
                successor = ExclusiveProcessLease.TryAcquire(name);
                if (successor is null) await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            using (successor) Assert.NotNull(successor);
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill();
                // Cleanup must finish even if the test itself has been cancelled.
                await helper.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        }
    }

    private static string NewName() => (IsWindows ? @"Global\" : "") + "ProjectTabletop.Test." + Guid.NewGuid().ToString("N");
}
