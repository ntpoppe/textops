using TextOps.Contracts.Orchestration;
using TextOps.Contracts.Parsing;
using TextOps.Contracts.Runs;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;

namespace TextOps.Orchestrator.Tests.Orchestration;

/// <summary>
/// Tests proving that concurrent operations are handled correctly via atomic state transitions.
/// These tests verify that race conditions are prevented by the TryTransition mechanism.
/// </summary>
[TestFixture]
public sealed class ConcurrencyTests
{
    private InMemoryRunOrchestrator _orchestrator = null!;
    private DeterministicIntentParser _parser = null!;

    [SetUp]
    public void SetUp()
    {
        _orchestrator = new InMemoryRunOrchestrator();
        _parser = new DeterministicIntentParser();
    }

    [Test]
    public void ConcurrentApprovals_OnlyOneSucceeds()
    {
        // Arrange: create a run in AwaitingApproval state
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "create-1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        // Verify initial state
        Assert.That(_orchestrator.GetTimeline(runId).Run.Status, Is.EqualTo(RunStatus.AwaitingApproval));

        // Act: attempt concurrent approvals with different message IDs
        var approveResults = new List<(string msgId, bool dispatched)>();
        var barrier = new Barrier(10);
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            var msgId = $"approve-{i}";
            tasks.Add(Task.Run(() =>
            {
                var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", msgId);
                var approveIntent = TestHelpers.Parse(_parser, approveMsg);
                
                // Synchronize all threads to start at the same time
                barrier.SignalAndWait();
                
                var result = _orchestrator.HandleInbound(approveMsg, approveIntent);
                lock (approveResults)
                {
                    approveResults.Add((msgId, result.DispatchedExecution));
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert: exactly one dispatch occurred
        var dispatchCount = approveResults.Count(r => r.dispatched);
        Assert.That(dispatchCount, Is.EqualTo(1), 
            $"Exactly one approval should dispatch. Got {dispatchCount}. Results: {string.Join(", ", approveResults.Select(r => $"{r.msgId}:{r.dispatched}"))}");

        // Assert: run is in Dispatching state (or further if worker ran)
        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.AnyOf(RunStatus.Dispatching, RunStatus.Running, RunStatus.Succeeded, RunStatus.Failed),
            "Run should be in Dispatching or later state");

        // Assert: exactly one RunApproved event
        var approvedEvents = timeline.Events.Where(e => e.Type == "RunApproved").ToList();
        Assert.That(approvedEvents, Has.Count.EqualTo(1), "Should have exactly one RunApproved event");

        // Assert: exactly one ExecutionDispatched event
        var dispatchedEvents = timeline.Events.Where(e => e.Type == "ExecutionDispatched").ToList();
        Assert.That(dispatchedEvents, Has.Count.EqualTo(1), "Should have exactly one ExecutionDispatched event");
    }

    [Test]
    public void ConcurrentDenials_OnlyOneSucceeds()
    {
        // Arrange: create a run in AwaitingApproval state
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "create-1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        // Act: attempt concurrent denials
        var barrier = new Barrier(10);
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            var msgId = $"deny-{i}";
            tasks.Add(Task.Run(() =>
            {
                var denyMsg = TestHelpers.CreateInboundMessage($"no {runId}", msgId);
                var denyIntent = TestHelpers.Parse(_parser, denyMsg);
                
                barrier.SignalAndWait();
                
                _orchestrator.HandleInbound(denyMsg, denyIntent);
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert: run is in Denied state
        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Denied));

        // Assert: exactly one RunDenied event (only one denial actually transitioned the state)
        var deniedEvents = timeline.Events.Where(e => e.Type == "RunDenied").ToList();
        Assert.That(deniedEvents, Has.Count.EqualTo(1), "Should have exactly one RunDenied event");
    }

    [Test]
    public void ConcurrentExecutionStarted_OnlyOneTransitions()
    {
        // Arrange: create and approve a run (now in Dispatching)
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "create-1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", "approve-1");
        var approveIntent = TestHelpers.Parse(_parser, approveMsg);
        _orchestrator.HandleInbound(approveMsg, approveIntent);

        Assert.That(_orchestrator.GetTimeline(runId).Run.Status, Is.EqualTo(RunStatus.Dispatching));

        // Act: attempt concurrent execution started calls
        var barrier = new Barrier(10);
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            var workerId = $"worker-{i}";
            tasks.Add(Task.Run(() =>
            {
                barrier.SignalAndWait();
                _orchestrator.OnExecutionStarted(runId, workerId);
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert: run is in Running state
        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.EqualTo(RunStatus.Running));

        // Assert: exactly one ExecutionStarted event (only one worker succeeded in transitioning)
        var startedEvents = timeline.Events.Where(e => e.Type == "ExecutionStarted").ToList();
        Assert.That(startedEvents, Has.Count.EqualTo(1), "Should have exactly one ExecutionStarted event");
    }

    [Test]
    public void ConcurrentExecutionCompleted_OnlyOneTransitions()
    {
        // Arrange: create, approve, and start a run
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "create-1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        var approveMsg = TestHelpers.CreateInboundMessage($"yes {runId}", "approve-1");
        var approveIntent = TestHelpers.Parse(_parser, approveMsg);
        _orchestrator.HandleInbound(approveMsg, approveIntent);

        _orchestrator.OnExecutionStarted(runId, "worker-0");
        Assert.That(_orchestrator.GetTimeline(runId).Run.Status, Is.EqualTo(RunStatus.Running));

        // Act: attempt concurrent execution completed calls with different outcomes
        var barrier = new Barrier(10);
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            var workerId = $"worker-{i}";
            var success = i % 2 == 0; // Alternate success/failure
            tasks.Add(Task.Run(() =>
            {
                barrier.SignalAndWait();
                _orchestrator.OnExecutionCompleted(runId, workerId, success, $"Result from {workerId}");
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert: run is in a terminal state
        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.AnyOf(RunStatus.Succeeded, RunStatus.Failed),
            "Run should be in a terminal state");

        // Assert: exactly one terminal event (either ExecutionSucceeded or ExecutionFailed)
        var terminalEvents = timeline.Events.Where(e => e.Type == "ExecutionSucceeded" || e.Type == "ExecutionFailed").ToList();
        Assert.That(terminalEvents, Has.Count.EqualTo(1), "Should have exactly one terminal event");
    }

    [Test]
    public void ConcurrentApproveAndDeny_OnlyOneSucceeds()
    {
        // Arrange: create a run in AwaitingApproval state
        var createMsg = TestHelpers.CreateInboundMessage("run demo", "create-1");
        var createIntent = TestHelpers.Parse(_parser, createMsg);
        var createResult = _orchestrator.HandleInbound(createMsg, createIntent);
        var runId = TestHelpers.ExtractRunIdFromResult(createResult);

        // Act: attempt concurrent approve and deny
        var barrier = new Barrier(2);
        var approveTask = Task.Run(() =>
        {
            var msg = TestHelpers.CreateInboundMessage($"yes {runId}", "approve-1");
            var intent = TestHelpers.Parse(_parser, msg);
            barrier.SignalAndWait();
            return _orchestrator.HandleInbound(msg, intent);
        });

        var denyTask = Task.Run(() =>
        {
            var msg = TestHelpers.CreateInboundMessage($"no {runId}", "deny-1");
            var intent = TestHelpers.Parse(_parser, msg);
            barrier.SignalAndWait();
            return _orchestrator.HandleInbound(msg, intent);
        });

        Task.WaitAll(approveTask, denyTask);

        // Assert: run is in either Dispatching or Denied state (not both)
        var timeline = _orchestrator.GetTimeline(runId);
        Assert.That(timeline.Run.Status, Is.AnyOf(RunStatus.Dispatching, RunStatus.Denied, RunStatus.Running, RunStatus.Succeeded, RunStatus.Failed),
            "Run should be in a valid terminal-path state");

        // Assert: exactly one of RunApproved or RunDenied events
        var approvedEvents = timeline.Events.Count(e => e.Type == "RunApproved");
        var deniedEvents = timeline.Events.Count(e => e.Type == "RunDenied");
        
        Assert.That(approvedEvents + deniedEvents, Is.EqualTo(1), 
            $"Should have exactly one approval or denial event. Got {approvedEvents} approved, {deniedEvents} denied");
    }
}

