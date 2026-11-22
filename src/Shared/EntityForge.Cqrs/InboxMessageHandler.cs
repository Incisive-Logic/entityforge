using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using EntityForge.Cqrs.Events;
using EntityForge.Cqrs.Exceptions;
using EntityForge.Cqrs.Observability;
using EntityForge.Cqrs.Queue;
using EntityForge.Cqrs.Repository;
using EntityForge.Cqrs.Repository.Inbox;
using EntityForge.Telemetry;

using Microsoft.Extensions.Logging;

namespace EntityForge.Cqrs
{
    /// <summary>Represents an asynchronous callback that processes a dispatched message.</summary>
    public delegate Task MessageHandlerDelegate(IInboxUnitOfWork unitOfWork, IMessage message);

    /// <summary>
    /// Coordinates transactional inbox checks with CQRS message handling.
    /// </summary>
    public static class InboxMessageHandler
    {
        private readonly static ActivitySource _activitySource = new ActivitySource(ActivitySourceNames.ActivitySourceName);

        private static string GenerateInboxKey(string consumerId, Guid messageId)
        {
            return $"{consumerId}:{messageId}";
        }

        private static async Task<bool> TryAddProcessedMessageAsync(
            IInboxUnitOfWork unitOfWork,
            string consumerId,
            Guid messageId,
            CancellationToken token)
        {
            if (unitOfWork == null)
            {
                return true;
            }

            return await unitOfWork.InboxRepository.TryAddProcessedMessageAsync(
                GenerateInboxKey(consumerId, messageId),
                DateTime.UtcNow,
                token);
        }

        private static async Task<MessageHandlingResult> HandleTopicMessageAsync(ILogger logger, string messageSystemDescription, string messageSystemProtocol, string topic, IMessage message, IMessageHandlerHostObject handlerHostObject, Func<IInboxUnitOfWork, Task> handlerAction)
        {
            // Enrich any logging in the handler with key context information
            var loggerTags = new Dictionary<string, object>()
            {
                ["MessageID"] = message.Id,
                ["MessageType"] = message.GetType().Name,
                ["HandlerType"] = handlerHostObject.GetType().Name,
                ["Topic"] = topic,
            };

            using (logger.BeginScope(loggerTags))
            {
                using IDisposable propagationScope =
                    TelemetryPropagationContext.Begin(message.TelemetryActivityId);

                // Start an activity/span for OpenTelemetry
                using Activity activity = CreateMessageHandlingActivity(message);

                activity?.SetTag(ActivityTagNames.MessagingSystem, messageSystemDescription);
                activity?.SetTag(ActivityTagNames.MessagingDestination, topic);
                activity?.SetTag(ActivityTagNames.MessagingDestinationKind, "topic");
                activity?.SetTag(ActivityTagNames.MessagingOperation, "process");
                activity?.SetTag(ActivityTagNames.MessagingConsumerId, handlerHostObject.ConsumerID);
                activity?.SetTag(ActivityTagNames.MessagingMessageId, message.Id.ToString());
                activity?.SetTag(ActivityTagNames.MessagingProtocol, messageSystemProtocol);

                // Create a unit of work for the handler. This will be used by the handler to perform
                // any update operations on the data store. Because the same unit of work also handles
                // the transactional inbox, both the handler operations and processed-message confirmation
                // can be committed atomically.
                await using (var unitOfWork = await handlerHostObject
                    .CreateUnitOfWorkAsync(message.GetType(), CancellationToken.None)
                    .ConfigureAwait(false))
                {
                    bool claimAdded = await TryAddProcessedMessageAsync(
                        unitOfWork,
                        handlerHostObject.ConsumerID,
                        message.Id,
                        CancellationToken.None);
                    if (!claimAdded)
                    {
                        return MessageHandlingResult.AlreadyProcessed;
                    }

                    try
                    {
                        // Pass in the unit of work and message to the handler
                        await handlerAction(unitOfWork);

                        // Commit the inbox claim atomically with business and outbox changes.
                        if (unitOfWork != null)
                        {
                            await unitOfWork.CommitAndDispatchAsync(CancellationToken.None);
                        }

                        return MessageHandlingResult.Processed;
                    }
                    catch (UnitOfWorkException)
                    {
                        activity?.SetError("Message handling was aborted by its unit of work.");
                        // Handler has aborted its processing, so we abort and do not mark as processed
                        // Note: Generally, not calling SaveChangesAsync() should be sufficient but if
                        // there are operations that have already been saved, we need to revert them
                        if (unitOfWork != null)
                        {
                            await unitOfWork.RollbackAsync(CancellationToken.None);
                        }

                        return MessageHandlingResult.Retry;
                    }
                    catch (Exception ex)
                    {
                        activity?.SetError(ex.Message);
                        if (unitOfWork != null)
                        {
                            await unitOfWork.RollbackAsync(CancellationToken.None);
                        }

                        Debug.WriteLine($"Handler {handlerHostObject.GetType().Name} failed to process message {message.Id} on topic {topic}");
                        logger.LogError(
                            ex,
                            "Handler {HandlerType} failed to process message {MessageId} on topic {Topic}; delivery will be retried.",
                            handlerHostObject.GetType().Name,
                            message.Id,
                            topic);
                        return MessageHandlingResult.Retry;
                    }
                }
            }
        }

        private static Activity CreateMessageHandlingActivity(IMessage message)
        {
            if (!EntityForgeTelemetry.IsEnabled(TelemetryLevel.Deep))
            {
                return null;
            }

            return ActivityHelper.TryParseActivityContext(
                message.TelemetryActivityId,
                out ActivityContext parentContext)
                ? _activitySource.StartActivity(
                    $"Handle {message.GetType().Name}",
                    ActivityKind.Consumer,
                    parentContext)
                : _activitySource.StartActivity(
                    $"Handle {message.GetType().Name}",
                    ActivityKind.Consumer);
        }

        /// <summary>
        /// Idempotently handles a message from a topic using the specified message handler.
        /// Handlers should throw <see cref="UnitOfWorkException"/> to indicate that processing should be aborted.
        /// </summary>
        /// <param name="logger">Receives processing and failure diagnostics.</param>
        /// <param name="messageSystemDescription">Identifies the messaging system in diagnostics and telemetry.</param>
        /// <param name="messageSystemProtocol">Identifies the messaging transport protocol in telemetry.</param>
        /// <param name="topic">The name of the message topic from the message comes.</param>
        /// <param name="message">The message.</param>
        /// <param name="handlerHostObject">The message handler host object.</param>
        /// <param name="messageHandlerDelegate">Processes the message within the inbox unit of work.</param>
        /// <returns>The processing outcome, including whether an already processed message was skipped.</returns>
        public static Task<MessageHandlingResult> HandleTopicMessageAsync(ILogger logger, string messageSystemDescription, string messageSystemProtocol, string topic, IMessage message, IMessageHandlerHostObject handlerHostObject, MessageHandlerDelegate messageHandlerDelegate)
        {
            return HandleTopicMessageAsync(logger, messageSystemDescription,
                messageSystemProtocol,
                topic,
                message,
                handlerHostObject,
                (unitOfWork) => messageHandlerDelegate(unitOfWork, message));
        }

        /// <summary>
        /// Idempotently handles an event message from a topic using the specified message handler.
        /// Handlers should throw <see cref="UnitOfWorkException"/> to indicate that processing should be aborted.
        /// </summary>
        /// <param name="logger">Receives processing and failure diagnostics.</param>
        /// <param name="messageSystemDescription">Identifies the messaging system in diagnostics and telemetry.</param>
        /// <param name="messageSystemProtocol">Identifies the messaging transport protocol in telemetry.</param>
        /// <param name="topic">The name of the event topic from the event comes.</param>
        /// <param name="eventMessage">The event message.</param>
        /// <param name="handler">Handles the event within the inbox unit of work.</param>
        /// <returns>The processing outcome, including whether an already processed message was skipped.</returns>
        public static Task<MessageHandlingResult> HandleTopicMessageAsync(ILogger logger, string messageSystemDescription, string messageSystemProtocol, string topic, IEventMessage eventMessage, IEventHandler handler)
        {
            return HandleTopicMessageAsync(logger, messageSystemDescription,
                messageSystemProtocol,
                topic,
                eventMessage,
                handler,
                (unitOfWork) => handler.HandleAsync(eventMessage, unitOfWork));
        }

        /// <summary>Processes a topic message through inbox-aware handling so an already processed identity can be acknowledged without reapplying its work.</summary>
        public static Task<MessageHandlingResult> HandleTopicMessageAsync<TEvent>(
            ILogger logger,
            string messageSystemDescription,
            string messageSystemProtocol,
            string topic,
            TEvent eventMessage,
            IEventHandler<TEvent> handler)
            where TEvent : IEventMessage
        {
            return HandleTopicMessageAsync(
                logger,
                messageSystemDescription,
                messageSystemProtocol,
                topic,
                eventMessage,
                handler,
                unitOfWork => handler.HandleAsync(eventMessage, unitOfWork));
        }

        /// <summary>
        /// Idempotently handles a message from a topic using the specified message handler.
        /// Handlers should throw <see cref="UnitOfWorkException"/> to indicate that processing should be aborted.
        /// </summary>
        /// <typeparam name="T">The type of message being handled (usually an event, or command).</typeparam>
        /// <param name="logger">Receives processing and failure diagnostics.</param>
        /// <param name="messageSystemDescription">Identifies the messaging system in diagnostics and telemetry.</param>
        /// <param name="messageSystemProtocol">Identifies the messaging transport protocol in telemetry.</param>
        /// <param name="topic">The name of the message topic from the message comes.</param>
        /// <param name="message">The message.</param>
        /// <param name="handler">The message handler.</param>
        /// <returns>The processing outcome, including whether an already processed message was skipped.</returns>
        public static Task<MessageHandlingResult> HandleTopicMessageAsync<T>(ILogger logger, string messageSystemDescription, string messageSystemProtocol, string topic, T message, IMessageHandler<T> handler)
            where T : IMessage
        {
            return HandleTopicMessageAsync(logger, messageSystemDescription,
                messageSystemProtocol,
                topic,
                message,
                handler,
                (unitOfWork) => handler.HandleAsync(message, unitOfWork));
        }
    }
}
