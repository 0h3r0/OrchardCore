using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OrchardCore.Modules;
using OrchardCore.Workflows.Activities;
using OrchardCore.Workflows.Helpers;
using OrchardCore.Workflows.Models;

namespace OrchardCore.Workflows.Services
{
    public class WorkflowManager : IWorkflowManager
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IActivityLibrary _activityLibrary;
        private readonly IWorkflowDefinitionRepository _workflowDefinitionRepository;
        private readonly IWorkflowInstanceRepository _workInstanceRepository;
        private readonly IWorkflowExpressionEvaluator _expressionEvaluator;
        private readonly IWorkflowScriptEvaluator _scriptEvaluator;
        private readonly Resolver<IEnumerable<IWorkflowContextHandler>> _workflowContextHandlers;
        private readonly Resolver<IEnumerable<IWorkflowValueSerializer>> _workflowValueSerializers;
        private readonly ILogger<WorkflowManager> _logger;
        private readonly ILogger<WorkflowContext> _workflowContextLogger;
        private readonly ILogger<MissingActivity> _missingActivityLogger;
        private readonly IStringLocalizer<MissingActivity> _missingActivityLocalizer;
        private readonly IClock _clock;

        public WorkflowManager
        (
            IServiceProvider serviceProvider,
            IActivityLibrary activityLibrary,
            IWorkflowDefinitionRepository workflowDefinitionRepository,
            IWorkflowInstanceRepository workflowInstanceRepository,
            IWorkflowExpressionEvaluator expressionEvaluator,
            IWorkflowScriptEvaluator scriptEvaluator,
            Resolver<IEnumerable<IWorkflowContextHandler>> workflowContextHandlers,
            Resolver<IEnumerable<IWorkflowValueSerializer>> workflowValueSerializers,
            ILogger<WorkflowManager> logger,
            ILogger<WorkflowContext> workflowContextLogger,
            ILogger<MissingActivity> missingActivityLogger,
            IStringLocalizer<MissingActivity> missingActivityLocalizer,
            IClock clock
        )
        {
            _serviceProvider = serviceProvider;
            _activityLibrary = activityLibrary;
            _workflowDefinitionRepository = workflowDefinitionRepository;
            _workInstanceRepository = workflowInstanceRepository;
            _expressionEvaluator = expressionEvaluator;
            _scriptEvaluator = scriptEvaluator;
            _workflowContextHandlers = workflowContextHandlers;
            _workflowValueSerializers = workflowValueSerializers;
            _logger = logger;
            _workflowContextLogger = workflowContextLogger;
            _missingActivityLogger = missingActivityLogger;
            _missingActivityLocalizer = missingActivityLocalizer;
            _clock = clock;
        }

        public async Task<WorkflowContext> CreateWorkflowContextAsync(WorkflowDefinitionRecord workflowDefinitionRecord, WorkflowInstanceRecord workflowInstanceRecord, IDictionary<string, object> input = null)
        {
            var activityQuery = await Task.WhenAll(workflowDefinitionRecord.Activities.Select(async x => await CreateActivityContextAsync(x)));
            var state = workflowInstanceRecord.State.ToObject<WorkflowState>();
            var mergedInput = (await DeserializeAsync(state.Input)).Merge(input ?? new Dictionary<string, object>());
            var properties = await DeserializeAsync(state.Properties);
            var output = await DeserializeAsync(state.Output);
            var lastResult = await DeserializeAsync(state.LastResult);
            return new WorkflowContext(workflowDefinitionRecord, workflowInstanceRecord, _serviceProvider, mergedInput, output, properties, lastResult, activityQuery, _workflowContextHandlers.Resolve(), _expressionEvaluator, _scriptEvaluator, _workflowContextLogger);
        }

        public Task<ActivityContext> CreateActivityContextAsync(ActivityRecord activityRecord)
        {
            var activity = _activityLibrary.InstantiateActivity<IActivity>(activityRecord.Name, activityRecord.Properties);

            if (activity == null)
            {
                _logger.LogWarning($"Requested activity '{activityRecord.Name}' does not exist in the library. This could indicate a changed name or a missing feature. Replacing it with MissingActivity.");
                activity = new MissingActivity(_missingActivityLocalizer, _missingActivityLogger, activityRecord);
            }

            var context = new ActivityContext
            {
                ActivityRecord = activityRecord,
                Activity = activity
            };

            return Task.FromResult(context);
        }

        public async Task TriggerEventAsync(string name, IDictionary<string, object> input = null, string correlationId = null)
        {
            var activity = _activityLibrary.GetActivityByName(name);

            if (activity == null)
            {
                _logger.LogError("Activity {0} was not found", name);
                return;
            }

            // Look for workflow definitions with a corresponding starting activity.
            var workflowsToStart = await _workflowDefinitionRepository.GetWorkflowDefinitionsByStartActivityAsync(name);

            // And any running workflow paused on this kind of activity for the specified target.
            // When an activity is restarted, all the other ones of the same workflow are cancelled.
            var awaitingWorkflowInstances = await _workInstanceRepository.GetWaitingWorkflowInstancesAsync(name, correlationId);

            // If no activity record is matching the event, do nothing.
            if (!workflowsToStart.Any() && !awaitingWorkflowInstances.Any())
            {
                return;
            }

            // Resume pending workflows.
            foreach (var workflowInstance in awaitingWorkflowInstances)
            {
                await ResumeWorkflowAsync(workflowInstance, input);
            }

            // Start new workflows.
            foreach (var workflowToStart in workflowsToStart)
            {
                var startActivity = workflowToStart.Activities.FirstOrDefault(x => x.IsStart && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

                if (startActivity != null)
                {
                    await StartWorkflowAsync(workflowToStart, startActivity, input, correlationId);
                }
            }
        }

        public async Task<IList<WorkflowContext>> ResumeWorkflowAsync(WorkflowInstanceRecord workflowInstance, IDictionary<string, object> input)
        {
            var workflowContexts = new List<WorkflowContext>();
            foreach (var awaitingActivity in workflowInstance.AwaitingActivities.ToList())
            {
                var context = await ResumeWorkflowAsync(workflowInstance, awaitingActivity, input);
                workflowContexts.Add(context);
            }

            return workflowContexts;
        }

        public async Task<WorkflowContext> ResumeWorkflowAsync(WorkflowInstanceRecord workflowInstance, AwaitingActivityRecord awaitingActivity, IDictionary<string, object> input = null)
        {
            var workflowDefinition = await _workflowDefinitionRepository.GetWorkflowDefinitionAsync(workflowInstance.DefinitionId);
            var activityRecord = workflowDefinition.Activities.SingleOrDefault(x => x.Id == awaitingActivity.ActivityId);
            var workflowContext = await CreateWorkflowContextAsync(workflowDefinition, workflowInstance, input);

            workflowContext.Status = WorkflowStatus.Resuming;

            // Signal every activity that the workflow is about to be resumed.
            var cancellationToken = new CancellationToken();

            await InvokeActivitiesAsync(workflowContext, async x => await x.Activity.OnInputReceivedAsync(workflowContext, input));
            await InvokeActivitiesAsync(workflowContext, async x => await x.Activity.OnWorkflowResumingAsync(workflowContext, cancellationToken));

            if (cancellationToken.IsCancellationRequested)
            {
                // Workflow is aborted.
                workflowContext.Status = WorkflowStatus.Aborted;
            }
            else
            {
                // Check if the current activity can execute.
                var activityContext = workflowContext.GetActivity(activityRecord.Id);
                if (await activityContext.Activity.CanExecuteAsync(workflowContext, activityContext))
                {
                    // Signal every activity that the workflow is resumed.
                    await InvokeActivitiesAsync(workflowContext, async x => await x.Activity.OnWorkflowResumedAsync(workflowContext));

                    // Remove the blocking activity.
                    workflowContext.WorkflowInstance.AwaitingActivities.Remove(awaitingActivity);

                    // Resume the workflow at the specified blocking activity.
                    await ExecuteWorkflowAsync(workflowContext, activityRecord);
                }
                else
                {
                    workflowContext.Status = WorkflowStatus.Halted;
                    return workflowContext;
                }
            }

            if (workflowContext.Status == WorkflowStatus.Finished)
            {
                await _workInstanceRepository.DeleteAsync(workflowContext.WorkflowInstance);
            }
            else
            {
                await PersistAsync(workflowContext);
            }

            return workflowContext;
        }

        public async Task<WorkflowContext> StartWorkflowAsync(WorkflowDefinitionRecord workflowDefinition, ActivityRecord startActivity = null, IDictionary<string, object> input = null, string correlationId = null)
        {
            if (startActivity == null)
            {
                startActivity = workflowDefinition.Activities.FirstOrDefault(x => x.IsStart);

                if (startActivity == null)
                {
                    throw new InvalidOperationException($"Workflow with ID {workflowDefinition.Id} does not have a start activity.");
                }
            }

            // Create a new workflow instance.
            var workflowInstance = new WorkflowInstanceRecord
            {
                DefinitionId = workflowDefinition.Id,
                Uid = Guid.NewGuid().ToString("N"),
                State = JObject.FromObject(new WorkflowState()),
                CorrelationId = correlationId,
                CreatedUtc = _clock.UtcNow
            };

            // Create a workflow context.
            var workflowContext = await CreateWorkflowContextAsync(workflowDefinition, workflowInstance, input);
            workflowContext.Status = WorkflowStatus.Starting;

            // Signal every activity about available input.
            await InvokeActivitiesAsync(workflowContext, async x => await x.Activity.OnInputReceivedAsync(workflowContext, input));

            // Signal every activity that the workflow is about to start.
            var cancellationToken = new CancellationToken();
            await InvokeActivitiesAsync(workflowContext, async x => await x.Activity.OnWorkflowStartingAsync(workflowContext, cancellationToken));

            if (cancellationToken.IsCancellationRequested)
            {
                // Workflow is aborted.
                workflowContext.Status = WorkflowStatus.Aborted;
                return workflowContext;
            }
            else
            {
                // Check if the current activity can execute.
                var activityContext = workflowContext.GetActivity(startActivity.Id);
                if (await activityContext.Activity.CanExecuteAsync(workflowContext, activityContext))
                {
                    // Signal every activity that the workflow has started.
                    await InvokeActivitiesAsync(workflowContext, async x => await x.Activity.OnWorkflowStartedAsync(workflowContext));

                    // Execute the activity.
                    await ExecuteWorkflowAsync(workflowContext, startActivity);
                }
                else
                {
                    workflowContext.Status = WorkflowStatus.Idle;
                    return workflowContext;
                }
            }

            if (workflowContext.Status != WorkflowStatus.Finished)
            {
                // Serialize state.
                await PersistAsync(workflowContext);
            }

            return workflowContext;
        }

        public async Task<IEnumerable<ActivityRecord>> ExecuteWorkflowAsync(WorkflowContext workflowContext, ActivityRecord activity)
        {
            var definition = workflowContext.WorkflowDefinition;
            var scheduled = new Stack<ActivityRecord>();
            var blocking = new List<ActivityRecord>();
            var isResuming = workflowContext.Status == WorkflowStatus.Resuming;
            var isFirstPass = true;

            workflowContext.Status = WorkflowStatus.Executing;
            scheduled.Push(activity);

            while (scheduled.Count > 0)
            {
                activity = scheduled.Pop();

                var activityContext = workflowContext.GetActivity(activity.Id);

                // Signal every activity that the activity is about to be executed.
                var cancellationToken = new CancellationToken();
                await InvokeActivitiesAsync(workflowContext, async x => await x.Activity.OnActivityExecutingAsync(workflowContext, activityContext, cancellationToken));

                if (cancellationToken.IsCancellationRequested)
                {
                    // Activity is aborted.
                    workflowContext.Status = WorkflowStatus.Aborted;
                    break;
                }

                // Execute the current activity.
                IList<string> outcomes;

                try
                {
                    ActivityExecutionResult result;

                    if (isResuming)
                    {
                        result = await activityContext.Activity.ResumeAsync(workflowContext, activityContext);
                        isResuming = false;
                    }
                    else
                    {
                        result = await activityContext.Activity.ExecuteAsync(workflowContext, activityContext);
                    }

                    if (result.IsHalted)
                    {
                        if (isFirstPass)
                        {
                            // Resume immediately when this is the first pass.
                            result = await activityContext.Activity.ResumeAsync(workflowContext, activityContext);
                            isFirstPass = false;
                            outcomes = result.Outcomes;
                        }
                        else
                        {
                            // Block on this activity.
                            blocking.Add(activity);

                            continue;
                        }
                    }
                    else
                    {
                        outcomes = result.Outcomes;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"An error occurred while executing an activity. Workflow ID: {definition.Id}. Activity: {activityContext.ActivityRecord.Id}, {activityContext.ActivityRecord.Name}");
                    workflowContext.Fault(ex, activityContext);
                    return blocking.Distinct();
                }

                // Signal every activity that the activity is executed.
                await InvokeActivitiesAsync(workflowContext, async x => await x.Activity.OnActivityExecutedAsync(workflowContext, activityContext));

                foreach (var outcome in outcomes)
                {
                    // Look for next activity in the graph.
                    var transition = definition.Transitions.FirstOrDefault(x => x.SourceActivityId == activity.Id && x.SourceOutcomeName == outcome);

                    if (transition != null)
                    {
                        var destinationActivity = workflowContext.WorkflowDefinition.Activities.SingleOrDefault(x => x.Id == transition.DestinationActivityId);
                        scheduled.Push(destinationActivity);
                    }
                }
            }

            // Apply Distinct() as two paths could block on the same activity.
            var blockingActivities = blocking.Distinct().ToList();
            workflowContext.Status = blockingActivities.Any() || workflowContext.WorkflowInstance.AwaitingActivities.Any() ? WorkflowStatus.Halted : WorkflowStatus.Finished;

            foreach (var blockingActivity in blockingActivities)
            {
                workflowContext.WorkflowInstance.AwaitingActivities.Add(AwaitingActivityRecord.FromActivity(blockingActivity));
            }

            return blockingActivities;
        }

        private async Task PersistAsync(WorkflowContext workflowContext)
        {
            var state = workflowContext.WorkflowInstance.State.ToObject<WorkflowState>();

            state.Input = await SerializeAsync(workflowContext.Input);
            state.Output = await SerializeAsync(workflowContext.Output);
            state.Properties = await SerializeAsync(workflowContext.Properties);
            state.LastResult = await SerializeAsync(workflowContext.LastResult);

            workflowContext.WorkflowInstance.State = JObject.FromObject(state);
            await _workInstanceRepository.SaveAsync(workflowContext.WorkflowInstance);
        }

        /// <summary>
        /// Executes a specific action on all the activities of a workflow.
        /// </summary>
        private async Task InvokeActivitiesAsync(WorkflowContext workflowContext, Func<ActivityContext, Task> action)
        {
            await workflowContext.Activities.InvokeAsync(x => action(x), _logger);
        }

        private async Task<IDictionary<string, object>> SerializeAsync(IDictionary<string, object> dictionary)
        {
            var copy = new Dictionary<string, object>(dictionary.Count);
            foreach (var item in dictionary)
            {
                copy[item.Key] = await SerializeAsync(item.Value);
            }
            return copy;
        }

        private async Task<IDictionary<string, object>> DeserializeAsync(IDictionary<string, object> dictionary)
        {
            var copy = new Dictionary<string, object>(dictionary.Count);
            foreach (var item in dictionary)
            {
                copy[item.Key] = await DeserializeAsync(item.Value);
            }
            return copy;
        }

        private async Task<object> SerializeAsync(object value)
        {
            var context = new SerializeWorkflowValueContext(value);
            await _workflowValueSerializers.Resolve().InvokeAsync(async x => await x.SerializeValueAsync(context), _logger);
            return context.Output;
        }

        private async Task<object> DeserializeAsync(object value)
        {
            var context = new SerializeWorkflowValueContext(value);
            await _workflowValueSerializers.Resolve().InvokeAsync(async x => await x.DeserializeValueAsync(context), _logger);
            return context.Output;
        }
    }
}
