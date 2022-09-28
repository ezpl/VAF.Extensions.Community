﻿using MFiles.VAF.AppTasks;
using MFiles.VAF.Common;
using MFiles.VAF.Configuration.AdminConfigurations;
using MFiles.VAF.Configuration.Domain;
using MFiles.VAF.Configuration.Domain.Dashboards;
using MFiles.VAF.Core;
using MFiles.VAF.Extensions.Dashboards;
using MFiles.VAF.Extensions.ScheduledExecution;
using MFilesAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MFiles.VAF.Extensions
{
	public partial class TaskManagerEx<TConfiguration>
	{
		/// <summary>
		/// The number of waiting tasks in a single queue at which point the dashboard is shown degraded.
		/// </summary>
		private const int DegradedDashboardThreshold = 3000;

		/// <summary>
		/// Returns some dashboard content that shows the background operations and their current status.
		/// </summary>
		/// <returns>The dashboard content.</returns>
		public virtual IEnumerable<DashboardListItem> GetDashboardContent(TaskQueueResolver taskQueueResolver)
		{
			if (null == taskQueueResolver)
				yield break;

			foreach (var queue in taskQueueResolver.GetQueues())
			{
				// Sanity.
				if (string.IsNullOrWhiteSpace(queue))
					continue;

				// Get information about the queues.
				System.Reflection.FieldInfo fieldInfo = null;
				try
				{
					fieldInfo = taskQueueResolver.GetQueueFieldInfo(queue);
				}
				catch(Exception e)
				{
					// Throws if the queue is incorrect.
					this.Logger?.Warn
					(
						e,
						$"Cannot load details for queue {queue}; is there a static field with the [TaskQueue] attribute?"
					);
					continue;
				}
				

				// Skip anything broken.
				if (null == fieldInfo)
					continue;

				// If it's marked as hidden then skip.
				{
					var attributes = fieldInfo.GetCustomAttributes(typeof(HideOnDashboardAttribute), true)
						?? new HideOnDashboardAttribute[0];
					if (attributes.Length != 0)
						continue;
				}

				// Get the number of items in the queue.
				var waitingTasks = this.GetTaskCountInQueue(queue, MFTaskState.MFTaskStateWaiting);
				var showDegraded = waitingTasks > DegradedDashboardThreshold;

				// Get each task processor.
				foreach (var processor in taskQueueResolver.GetTaskProcessors(queue))
				{
					// Sanity.
					if (null == processor)
						continue;

					// Get information about the processor..
					TaskProcessorAttribute taskProcessorSettings = null;
					System.Reflection.MethodInfo methodInfo = null;
					try
					{
						taskProcessorSettings = taskQueueResolver.GetTaskProcessorSettings(queue, processor.Type);
						methodInfo = taskQueueResolver.GetTaskProcessorMethodInfo(queue, processor.Type);
					}
					catch
					{
						// Throws if the task processor is not found.
						this.Logger?.Warn
						(
							$"Cannot load processor details for task type {processor.Type} on queue {queue}."
						);
						continue;
					}


					// Skip anything broken.
					if (null == taskProcessorSettings || null == methodInfo)
						continue;

					// If it's marked as hidden then skip.
					{
						var attributes = methodInfo.GetCustomAttributes(typeof(HideOnDashboardAttribute), true)
							?? new HideOnDashboardAttribute[0];
						if (attributes.Length != 0)
							continue;
					}

					// This should be shown.  Do we have any extended details?
					var showOnDashboardAttribute = methodInfo.GetCustomAttributes(typeof(ShowOnDashboardAttribute), true)?
						.FirstOrDefault() as ShowOnDashboardAttribute;

					// Generate the dashboard content.
					yield return this.GenerateDashboardContentForQueueAndTask
					(
						queue,
						processor.Type,
						showOnDashboardAttribute?.Name,
						showOnDashboardAttribute?.Description,
						showOnDashboardAttribute?.ShowRunCommand ?? false
					);

				}
			}

		}

		/// <summary>
		/// Returns a dashboard list item that represents the task processor associated 
		/// with the <paramref name="queueId"/> and <paramref name="taskType"/>.
		/// </summary>
		/// <param name="queueId">The ID of the queue (used in combination with <paramref name="taskType"/> to retrieve the tasks to show).</param>
		/// <param name="taskType">The type of task (used in combination with <paramref name="queueId"/> to retrieve the tasks to show).</param>
		/// <param name="displayName">The display name to show, or null to use the <paramref name="taskType"/>.</param>
		/// <param name="description">The description to show, or null to not show a description.</param>
		/// <param name="showRunCommand">Whether the "run" command should be shown or not.</param>
		/// <returns>The list item, or null if nothing should be shown.</returns>
		public virtual DashboardListItem GenerateDashboardContentForQueueAndTask
		(
			string queueId,
			string taskType,
			string displayName = null,
			string description = null,
			bool showRunCommand = false
		)
		{
			// Get the number of items in the queue.
			var waitingTasks = this.GetTaskCountInQueue(queueId, MFTaskState.MFTaskStateWaiting);
			var showDegraded = waitingTasks > DegradedDashboardThreshold;

			// Show the description?
			var htmlString = "";
			if (false == string.IsNullOrWhiteSpace(description))
			{
				htmlString += new DashboardCustomContent($"<p><em>{description.EscapeXmlForDashboard()}</em></p>").ToXmlString();
			}

			// If we are running degraded then highlight that.
			if (showDegraded)
			{
				htmlString += "<p style='background-color: red; font-weight: bold; color: white; padding: 5px 10px;'>";
				htmlString += String.Format
				(
					Resources.AsynchronousOperations.DegradedQueueDashboardNotice,
					waitingTasks,
					DegradedDashboardThreshold
				).EscapeXmlForDashboard();
				htmlString += "</p>";
			}

					// Does it have any configuration instructions?
					IRecurrenceConfiguration recurrenceConfiguration = null;
					if (this
						.VaultApplication?
						.RecurringOperationConfigurationManager?
						.TryGetValue(queueId, taskType, out recurrenceConfiguration) ?? false)
					{
						htmlString += recurrenceConfiguration.ToDashboardDisplayString();
					}
					else
					{
						htmlString += $"<p>{Resources.AsynchronousOperations.RepeatType_RunsOnDemandOnly.EscapeXmlForDashboard()}<br /></p>";
					}

			// Get known executions (prior, running and future).
			var executions = showDegraded
				? this.GetExecutions<TaskDirective>(queueId, taskType, MFTaskState.MFTaskStateInProgress)
				: this.GetAllExecutions<TaskDirective>(queueId, taskType)
				.ToList();
			var isRunning = executions.Any(e => e.State == MFilesAPI.MFTaskState.MFTaskStateInProgress);
			var isScheduled = executions.Any(e => e.State == MFilesAPI.MFTaskState.MFTaskStateWaiting);

			// Create the (basic) list item.
			var listItem = new DashboardListItemEx()
			{
				ID = $"{queueId}-{taskType}",
				Title = string.IsNullOrWhiteSpace(displayName) ? taskType : displayName,
				StatusSummary = new DomainStatusSummary()
				{
					Label = isRunning || showDegraded
					? Resources.AsynchronousOperations.Status_Running
					: isScheduled ? Resources.AsynchronousOperations.Status_Scheduled : Resources.AsynchronousOperations.Status_Stopped
				}
			};

			// Should we show the run command?
			if (showRunCommand)
			{
				var key = $"{queueId}-{taskType}";
				lock (this._lock)
				{
					if (this.TaskQueueRunCommands.ContainsKey(key))
					{
						var cmd = new DashboardDomainCommand
						{
							DomainCommandID = this.TaskQueueRunCommands[key].ID,
							Title = this.TaskQueueRunCommands[key].DisplayName,
							Style = DashboardCommandStyle.Link
						};
						listItem.Commands.Add(cmd);
					}
				}
			}

					// Set the list item content.
					listItem.InnerContent = new DashboardCustomContent
					(
						htmlString
						+ executions?
							.AsDashboardContent(recurrenceConfiguration)?
							.ToXmlString()
					);

			return listItem;
		}
	}
}
