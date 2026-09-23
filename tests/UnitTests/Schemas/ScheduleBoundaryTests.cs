using Common.SeedWork;
using Common.Workflow;
using Execution.Application.Commands;
using Execution.Domain.Aggregates;
using Execution.Domain.Events;
using Execution.Domain.Schemas;
using Execution.Infrastructure.Schemas;
using UnitTests.Support;
using WorkflowDocument = Utos.Workflows.V1.WorkflowDocument;

namespace UnitTests.Schemas;

/// <summary>
/// The schedule boundaries, <c>UTOS-H101</c> and <c>UTOS-H102</c>. Distinct from the interpreter's
/// four in when they fire and in what a failure means: no run exists yet, so a violation is
/// <c>INVALID_ARGUMENT</c> and nothing is recorded.
/// <para>
/// These also pin the thing that decided <em>where</em> the check lives — that the defaults a
/// declaration promises are filled into what is <strong>recorded</strong>, not merely into what
/// the first activity happens to read. A FluentValidation rule could not do that, because a
/// validator cannot mutate the command it inspects.
/// </para>
/// </summary>
public class ScheduleBoundaryTests
{
    [Fact]
    public async Task A_run_input_the_start_activity_refuses_is_rejected_before_anything_is_recorded()
    {
        var store = new RecordingEventStore();
        var command = Command(store, input: new Dictionary<string, object> { ["wrong"] = "x" });

        var error = await Assert.ThrowsAsync<SchemaValidationFailedException>(() =>
            new StartExecutionHandler(store, new JsonSchemaValidator())
                .Handle(command, CancellationToken.None).AsTask());

        Assert.Equal(SchemaValidation.RunInput, error.Code);
        Assert.False(store.Saved, "nothing may be recorded when the input is refused");
    }

    [Fact]
    public async Task A_missing_required_environment_variable_is_rejected()
    {
        var store = new RecordingEventStore();
        var command = Command(store,
            input: new Dictionary<string, object> { ["orderId"] = "A-1" },
            env: new Dictionary<string, string>());

        var error = await Assert.ThrowsAsync<SchemaValidationFailedException>(() =>
            new StartExecutionHandler(store, new JsonSchemaValidator())
                .Handle(command, CancellationToken.None).AsTask());

        Assert.Equal(SchemaValidation.RunEnv, error.Code);
        Assert.Equal(SchemaBoundary.Env, error.Boundary);
    }

    [Fact]
    public async Task Declared_defaults_are_filled_into_what_is_recorded()
    {
        var store = new RecordingEventStore();
        var command = Command(store,
            input: new Dictionary<string, object> { ["orderId"] = "A-1" },
            env: new Dictionary<string, string> { ["API_BASE"] = "https://example.test" });

        await new StartExecutionHandler(store, new JsonSchemaValidator())
            .Handle(command, CancellationToken.None);

        var created = Assert.IsType<WorkflowExecutionCreatedEvent>(
            Assert.Single(store.Events, e => e is WorkflowExecutionCreatedEvent));

        // A replay reads this event, so the default has to be here rather than applied on the way
        // out — otherwise the run and its record would disagree about what it was given.
        Assert.Equal(80L, Convert.ToInt64(created.Input["quality"]));
        Assert.Equal("us-east-1", created.AmbientEnv!["REGION"]);
    }

    private static StartExecutionCommand Command(
        RecordingEventStore store,
        Dictionary<string, object> input,
        Dictionary<string, string>? env = null)
    {
        _ = store;

        var start = new WorkflowActivity(
            new TimerActivityConfig("1s"),
            Schema: new ActivitySchema(Wf.Fields(
                ("type", "object"),
                ("required", new object[] { "orderId" }),
                ("properties", Wf.Fields(
                    ("orderId", Wf.Fields(("type", "string"))),
                    ("quality", Wf.Fields(("type", "integer"), ("default", 80))))),
                ("unevaluatedProperties", false))));

        var workflow = new Workflow(
            WorkflowDocument.ApiVersion,
            WorkflowDocument.Kind,
            new WorkflowMetadata("test", "1.0.0"),
            new WorkflowSpec(
                new Dictionary<string, WorkflowActivity> { ["start"] = start },
                Env: Wf.Fields(
                    ("type", "object"),
                    ("required", new object[] { "API_BASE" }),
                    ("properties", Wf.Fields(
                        ("API_BASE", Wf.Fields(("type", "string"))),
                        ("REGION", Wf.Fields(("type", "string"), ("default", "us-east-1"))))))));

        var bundle = new WorkflowBundle(Wf.SelfKey,
            new Dictionary<string, Workflow> { [Wf.SelfKey] = workflow });

        return new StartExecutionCommand(
            WorkflowIdentity.Create("test", SemanticVersion.Parse("1.0.0")),
            "sha256:test",
            bundle,
            input,
            "start",
            env ?? new Dictionary<string, string> { ["API_BASE"] = "https://example.test" });
    }

    /// <summary>
    /// A hand-written stand-in — the repo uses no mocking library — that records what it was
    /// asked to persist, so a test can assert that a refused schedule persisted nothing.
    /// </summary>
    private sealed class RecordingEventStore : IEventStoreRepository
    {
        public bool Saved { get; private set; }

        public List<IDomainEvent> Events { get; } = [];

        public Task SaveAsync<T, TId>(T aggregate) where T : AggregateRoot<TId>, new() where TId : notnull
        {
            Saved = true;
            Events.AddRange(aggregate.GetDomainEvents());
            return Task.CompletedTask;
        }

        public Task<T> LoadAsync<T, TId>(TId aggregateId, CancellationToken cancellationToken = default)
            where T : AggregateRoot<TId>, new() where TId : notnull =>
            throw new NotSupportedException();

        public Task AppendEventAsync<T, TId>(TId aggregateId, IDomainEvent @event)
            where T : AggregateRoot<TId>, new() where TId : notnull =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync<T, TId>(TId aggregateId, CancellationToken cancellationToken = default)
            where T : AggregateRoot<TId>, new() where TId : notnull =>
            throw new NotSupportedException();

        public Task AppendToStreamAsync(string streamId, IDomainEvent @event) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string streamId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteStreamAsync(string streamId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<EventWithNumber> SubscribeToStream<T, TId>(
            TId aggregateId, long? fromVersion = null, CancellationToken cancellationToken = default)
            where T : AggregateRoot<TId>, new() where TId : notnull =>
            throw new NotSupportedException();
    }
}
