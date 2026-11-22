using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Utility.Tests;

[TestClass]
public sealed class TimedLockTests
{
    [TestMethod]
    public void Shared_monitor_excludes_other_threads_and_is_released_on_disposal()
    {
        object gate = new();
        using (var held = new TimedLock(TimeSpan.Zero, gate))
        {
            Assert.IsTrue(Monitor.IsEntered(gate));
            Exception? failure = OnOtherThread(() =>
            {
                using var blocked = new TimedLock(TimeSpan.Zero, gate);
            });
            Assert.IsInstanceOfType<TimedLockException>(failure);
            Assert.IsTrue(Monitor.IsEntered(gate), "A failed contender must not release the holder's monitor.");
        }

        Assert.IsFalse(Monitor.IsEntered(gate));
        Assert.IsNull(OnOtherThread(() =>
        {
            using var acquired = new TimedLock(TimeSpan.FromSeconds(2), gate);
            Assert.IsTrue(Monitor.IsEntered(gate));
        }));
    }

    [TestMethod]
    public void Existing_monitor_ownership_is_reentrant_and_each_scope_releases_one_entry()
    {
        object gate = new();
        lock (gate)
        {
            using (var nested = new TimedLock(TimeSpan.Zero, gate))
            {
                Assert.IsTrue(Monitor.IsEntered(gate));
            }
            Assert.IsTrue(Monitor.IsEntered(gate));
        }
        Assert.IsFalse(Monitor.IsEntered(gate));
    }

    [TestMethod]
    public void Null_monitor_is_rejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new TimedLock(TimeSpan.Zero, null!));
    }

    private static Exception? OnOtherThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The monitor operation did not finish within the test bound.");
        return failure;
    }
}
