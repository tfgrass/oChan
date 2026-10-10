using System.Collections.Concurrent;
using oChan.Notifications;

namespace oChan.Tests;

public sealed class ConsumerNotificationTests
{
    [Fact]
    public void FailureCategoriesDistinguishNetworkReadsFromStorageAndRequestTimeouts()
    {
        Assert.Equal(FailureKind.Network, FailureInfo.FromException(
            new System.Net.Http.HttpIOException(System.Net.Http.HttpRequestError.ResponseEnded, "Transfer ended"), media: true).Kind);
        Assert.Equal(FailureKind.Storage, FailureInfo.FromException(new IOException("Disk full"), media: true).Kind);
        Assert.Equal(FailureKind.Timeout, FailureInfo.FromException(new TaskCanceledException("HTTP timeout"), media: false).Kind);
        Assert.Equal(FailureKind.InvalidData, FailureInfo.FromException(new FormatException("Invalid media size"), media: false).Kind);
    }

    [Fact]
    public async Task BlockedSubscriberHasOnePendingDeliveryAndDoesNotBlockAnotherSubscriber()
    {
        using ManualResetEventSlim release = new ManualResetEventSlim();
        TaskCompletionSource entered = NewCompletion();
        TaskCompletionSource healthy = NewCompletion();
        TaskCompletionSource second = NewCompletion();
        int deliveries = 0;
        using CoalescedEvent<Action> notifications = new CoalescedEvent<Action>(handler => handler());
        notifications.Add(() =>
        {
            int delivery = Interlocked.Increment(ref deliveries);
            if (delivery == 1)
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TestWait.Timeout));
            }
            else second.TrySetResult();
        });
        notifications.Add(() => healthy.TrySetResult());

        try
        {
            notifications.Signal();
            await entered.Task.WaitAsync(TestWait.Timeout);
            await healthy.Task.WaitAsync(TestWait.Timeout);
            for (int index = 0; index < 10_000; index++) notifications.Signal();
            Assert.Equal(1, Volatile.Read(ref deliveries));
            release.Set();
            await second.Task.WaitAsync(TestWait.Timeout);
            Assert.Equal(2, Volatile.Read(ref deliveries));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ThrowingSubscriberAndThrowingDiagnosticSinkDoNotFaultOtherDelivery()
    {
        TaskCompletionSource healthy = NewCompletion();
        TaskCompletionSource recorded = NewCompletion();
        using CoalescedEvent<Action> notifications = new CoalescedEvent<Action>(handler => handler(), _ =>
        {
            recorded.TrySetResult();
            throw new InvalidOperationException("Synthetic logging failure");
        });
        notifications.Add(() => throw new InvalidOperationException("Synthetic observer failure"));
        notifications.Add(() => healthy.TrySetResult());
        notifications.Signal();
        await healthy.Task.WaitAsync(TestWait.Timeout);
        await recorded.Task.WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public async Task MulticastRegistrationIsolatesAThrowingHandlerFromTheHealthyHandler()
    {
        TaskCompletionSource healthy = NewCompletion();
        TaskCompletionSource recorded = NewCompletion();
        using CoalescedEvent<Action> notifications = new CoalescedEvent<Action>(handler => handler(),
            _ => recorded.TrySetResult());
        Action throwing = () => throw new InvalidOperationException("Synthetic multicast observer failure");
        Action successful = () => healthy.TrySetResult();
        notifications.Add(throwing + successful);

        notifications.Signal();

        await healthy.Task.WaitAsync(TestWait.Timeout);
        await recorded.Task.WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public async Task RemovingOneComponentOfAMulticastRegistrationPreservesTheOtherComponent()
    {
        TaskCompletionSource retained = NewCompletion();
        using CoalescedEvent<Action> notifications = new CoalescedEvent<Action>(handler => handler());
        Action first = () => retained.TrySetResult();
        Action second = () => { };
        notifications.Add(first + second);
        notifications.Remove(second);
        // The full sequence no longer exists after removing its second component.
        // A successful removal here would also remove the retained first handler.
        notifications.Remove(first + second);

        notifications.Signal();

        await retained.Task.WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public async Task RemovingAMulticastSequenceRemovesOnlyItsLastContiguousOccurrence()
    {
        TaskCompletionSource firstDelivered = NewCompletion();
        TaskCompletionSource secondDelivered = NewCompletion();
        TaskCompletionSource middleDelivered = NewCompletion();
        using CoalescedEvent<Action> notifications = new CoalescedEvent<Action>(handler => handler());
        Action first = () => firstDelivered.TrySetResult();
        Action second = () => secondDelivered.TrySetResult();
        Action middle = () => middleDelivered.TrySetResult();
        notifications.Add(first + second + middle + first + second);
        notifications.Remove(first + second);
        // Correct subtraction leaves first + second + middle. This reverse pair
        // does not occur there; removing an earlier match would make it exist.
        notifications.Remove(middle + first);

        notifications.Signal();

        await Task.WhenAll(firstDelivered.Task, secondDelivered.Task, middleDelivered.Task)
            .WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public async Task DisposeReturnsWhileCallbackIsBlockedAndDiscardsItsPendingDelivery()
    {
        using ManualResetEventSlim release = new ManualResetEventSlim();
        TaskCompletionSource entered = NewCompletion();
        TaskCompletionSource exited = NewCompletion();
        int deliveries = 0;
        CoalescedEvent<Action> notifications = new CoalescedEvent<Action>(handler => handler());
        notifications.Add(() =>
        {
            Interlocked.Increment(ref deliveries);
            entered.TrySetResult();
            try { Assert.True(release.Wait(TestWait.Timeout)); }
            finally { exited.TrySetResult(); }
        });
        try
        {
            notifications.Signal();
            await entered.Task.WaitAsync(TestWait.Timeout);
            notifications.Signal();
            notifications.Dispose();
            notifications.Signal();
            release.Set();
            await exited.Task.WaitAsync(TestWait.Timeout);
            Assert.Equal(1, Volatile.Read(ref deliveries));
        }
        finally { notifications.Dispose(); release.Set(); }
    }

    [Fact]
    public async Task MutationInsideCallbackSchedulesTheFinalSnapshotWithoutReentrancy()
    {
        ConcurrentQueue<int> snapshots = new ConcurrentQueue<int>();
        TaskCompletionSource final = NewCompletion();
        int state = 1;
        int active = 0;
        using CoalescedEvent<Action> notifications = new CoalescedEvent<Action>(handler => handler());
        notifications.Add(() =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            try
            {
                int snapshot = Volatile.Read(ref state);
                snapshots.Enqueue(snapshot);
                if (snapshot == 1)
                {
                    Volatile.Write(ref state, 2);
                    notifications.Signal();
                }
                else final.TrySetResult();
            }
            finally { Interlocked.Decrement(ref active); }
        });
        notifications.Signal();
        await final.Task.WaitAsync(TestWait.Timeout);
        Assert.Equal(new int[] { 1, 2 }, snapshots.ToArray());
    }

    [Fact]
    public void ControlplaneOwnershipIsExclusiveAndAnOldLeaseCannotClearANewOwner()
    {
        SingleOwnerCallback<Action> callback = new SingleOwnerCallback<Action>();
        Action first = () => { };
        Action second = () => { };
        IDisposable firstLease = callback.Register(first);
        Assert.Same(first, callback.Current);
        Assert.Throws<InvalidOperationException>(() => callback.Register(second));
        firstLease.Dispose();
        using IDisposable secondLease = callback.Register(second);
        firstLease.Dispose();
        Assert.Same(second, callback.Current);
    }

    private static TaskCompletionSource NewCompletion() =>
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
}
