﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Helsenorge.Messaging.Abstractions;
using Helsenorge.Messaging.ServiceBus.Receivers;
using Microsoft.Extensions.Logging;

namespace Helsenorge.Messaging.ServiceBus.Senders
{
	/// <summary>
	/// Handles synchronous sending
	/// </summary>
	internal class SynchronousSender
	{
		private readonly ConcurrentDictionary<string, MessageEntry> _pendingSynchronousRequests = new ConcurrentDictionary<string, MessageEntry>();

		private class MessageEntry
		{
			public XDocument Payload { get; set; }
			/// <summary>
			/// time it was added to our list
			/// </summary>
			public DateTime AddedUtc { get; set; }
			public bool TimedOut { get; set; }
			public int ToHerId { get; set; }
			/// <summary>
			/// Time the reply was added to the queue
			/// </summary>
			public DateTime ReplyEnqueuedTimeUtc { get; set; }
		}
		private readonly ServiceBusCore _core;
	
		public SynchronousSender(ServiceBusCore core)
		{
			_core = core;
		}

		public async Task<XDocument> SendAsync(ILogger logger, OutgoingMessage message)
		{
			await _core.Send(logger, message, QueueType.Synchronous, _core.Settings.Synchronous.FindReplyQueueForMe()).ConfigureAwait(false);

			var listener = new SynchronousReplyListener(_core, logger);
			var start = DateTime.UtcNow;
			var correlationId = message.MessageId;

			// the name of the game is to get messages off the reply queue as fast as possible since multiple threads may be waiting
			// therefore all threads take part in the processing. If we read a message from a queue and the process dies,
			// it's not a big deal since the caller gets an error message; we can then store retrieved messages in an in-memory cache

			// 1. check if reply message is in in-memory cache
			// 2. read message and validate it
			// 3. If nobody is no longer waiting for it, discard it
			// 4. If message is not the one we are waiting for, place it in the in-memory cache so another thread can read it

			_pendingSynchronousRequests.TryAdd(correlationId, new MessageEntry()
			{
				Payload = null,
				AddedUtc = DateTime.UtcNow,
				ToHerId = message.ToHerId,
				TimedOut = false
			});

			RemoveExpiredSynchronousEntries(logger);

			while (true)
			{
				var payload = CheckPendingEntries(correlationId);
				if (payload != null)
				{
					return payload;
				}

				var incomingMessage = await listener.ReadAndProcessMessage(true).ConfigureAwait(false);
				if (incomingMessage != null)
				{
					// see if this is a message we are waiting for
					// if that is not the case, the information in incoming message enters the great beyond
					MessageEntry messageEntry;
					if (_pendingSynchronousRequests.TryGetValue(incomingMessage.CorrelationId, out messageEntry))
					{
						// if we receive a message a bit late, log it. 
						if (messageEntry.TimedOut)
						{
							// enqueue time should be later than when we added it
							logger.LogWarning(EventIds.SynchronousCallDelayed,
								"MessageId: {1} was received after {2} seconds from HerId: {3}. Added at {4} Enqueued at: {5}",
								incomingMessage.CorrelationId,
								(incomingMessage.EnqueuedTimeUtc - messageEntry.AddedUtc).TotalSeconds,
								messageEntry.ToHerId,
								messageEntry.AddedUtc,
								incomingMessage.EnqueuedTimeUtc);

							_pendingSynchronousRequests.TryRemove(incomingMessage.CorrelationId, out messageEntry);
						}
						else
						{
							// update information for existing entry
							messageEntry.Payload = incomingMessage.Payload;
							messageEntry.ReplyEnqueuedTimeUtc = incomingMessage.EnqueuedTimeUtc;
						}
					}
				}

				if (DateTime.UtcNow <= (start + _core.Settings.Synchronous.CallTimeout)) continue;
				
				// if the request times out, we mark it. That way we can report if the message is received right after a timeout
				MessageEntry entry;
				if (_pendingSynchronousRequests.TryGetValue(correlationId, out entry))
				{
					entry.TimedOut = true;
				}
				var error = $"Synchronous call {message.MessageId} timed out against HerId: {message.ToHerId}.";
				logger.LogError(EventIds.SynchronousCallTimeout, error);
				throw new MessagingException(error)
				{
					EventId = EventIds.SynchronousCallTimeout
				};
			}
		}
		private XDocument CheckPendingEntries(string correlationId)
		{
			MessageEntry entry;
			if (_pendingSynchronousRequests.TryGetValue(correlationId, out entry) != true) return null;
			if (entry.Payload == null) return null;

			var response = entry.Payload;
			// since we have data, remove the entry
			_pendingSynchronousRequests.TryRemove(correlationId, out entry);
			return response;
		}
		private void RemoveExpiredSynchronousEntries(ILogger logger)
		{
			var limit = DateTime.UtcNow.AddSeconds(-10) - _core.Settings.Synchronous.CallTimeout;

			var array = (
				from kvp in _pendingSynchronousRequests
				where kvp.Value.AddedUtc < limit
				select kvp.Key).ToArray();

			foreach (var item in array)
			{
				MessageEntry entry;

				if (_pendingSynchronousRequests.TryRemove(item, out entry) != true) continue;
				if (entry.TimedOut != true) continue;
				
				// if we never received a reply, we cannot calculate the time it took
				// enqueued time should be later than when we added it
				if (entry.ReplyEnqueuedTimeUtc > DateTime.MinValue)
				{
					logger.LogWarning(EventIds.SynchronousCallDelayed, 
						"MessageId: {1} was received after {2} seconds from HerId: {3}. Added at {4} Enqueued at: {5}",
						item,
						(entry.ReplyEnqueuedTimeUtc - entry.AddedUtc).TotalSeconds,
						entry.ToHerId,
						entry.AddedUtc,
						entry.ReplyEnqueuedTimeUtc);
				}
			}
		}
	}
}
