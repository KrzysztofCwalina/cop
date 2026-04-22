using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Cop.Driver.Models;

namespace Cop.Driver.Services
{
    /// <summary>
    /// Exception thrown when a task is not found.
    /// </summary>
    public class TaskNotFoundException : Exception
    {
        public TaskNotFoundException(string message) : base(message) { }
        public TaskNotFoundException(string message, Exception innerException) 
            : base(message, innerException) { }
    }

    /// <summary>
    /// Exception thrown when attempting to submit a duplicate task.
    /// </summary>
    public class DuplicateTaskException : Exception
    {
        public DuplicateTaskException(string message) : base(message) { }
        public DuplicateTaskException(string message, Exception innerException) 
            : base(message, innerException) { }
    }

    /// <summary>
    /// Manages the lifecycle of agent tasks in the driver.
    /// Tracks all tasks, handles submissions, and provides query/control operations.
    /// Thread-safe using ConcurrentDictionary.
    /// </summary>
    public class TaskManager
    {
        private readonly ConcurrentDictionary<string, DriverTask> _tasks;

        public TaskManager()
        {
            _tasks = new ConcurrentDictionary<string, DriverTask>();
        }

        /// <summary>
        /// Submits a new task or returns an existing non-terminal task with the same content hash.
        /// </summary>
        /// <param name="specPath">Path to the specification file.</param>
        /// <param name="specContent">Content of the specification.</param>
        /// <param name="force">If true, creates a new task even if a duplicate exists. If false, checks for duplicates.</param>
        /// <returns>The newly created task or existing task if duplicate found.</returns>
        /// <exception cref="DuplicateTaskException">Thrown when a non-terminal task with the same content hash exists and force is false.</exception>
        public DriverTask Submit(string specPath, string specContent, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(specPath))
                throw new ArgumentException("Spec path cannot be null or whitespace.", nameof(specPath));
            if (string.IsNullOrWhiteSpace(specContent))
                throw new ArgumentException("Spec content cannot be null or whitespace.", nameof(specContent));

            // Compute content hash
            string contentHash = DriverTask.ComputeContentHash(specContent);

            // Check for existing non-terminal task with same content hash
            if (!force)
            {
                var existingTask = _tasks.Values.FirstOrDefault(t =>
                    t.ContentHash == contentHash && !t.IsTerminal);

                if (existingTask != null)
                {
                    throw new DuplicateTaskException(
                        $"A non-terminal task with the same content already exists. Task ID: {existingTask.Id}");
                }
            }

            // Create new task
            var task = new DriverTask
            {
                Id = DriverTask.GenerateId(),
                SpecPath = specPath,
                SpecContent = specContent,
                Phase = TaskPhase.Pending,
                CreatedAt = DateTime.UtcNow,
                ContentHash = contentHash,
                VerifyAttempts = 0,
                MaxVerifyAttempts = 10,
                IsPaused = false,
                PendingFeedback = null
            };

            task.AddLog($"Task created for spec: {specPath}");

            // Add to dictionary
            if (!_tasks.TryAdd(task.Id, task))
            {
                // Highly unlikely due to unique ID generation, but handle gracefully
                throw new InvalidOperationException(
                    $"Failed to add task {task.Id} to the task manager.");
            }

            return task;
        }

        /// <summary>
        /// Retrieves a task by its ID.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <returns>The task if found; null otherwise.</returns>
        public DriverTask? GetTask(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                return null;

            _tasks.TryGetValue(taskId, out var task);
            return task;
        }

        /// <summary>
        /// Gets all tasks sorted by creation time (most recent first).
        /// </summary>
        /// <returns>List of all tasks sorted by CreatedAt descending.</returns>
        public List<DriverTask> GetAllTasks()
        {
            return _tasks.Values
                .OrderByDescending(t => t.CreatedAt)
                .ToList();
        }

        /// <summary>
        /// Gets all non-terminal tasks (tasks that are still in progress).
        /// </summary>
        /// <returns>List of active tasks sorted by CreatedAt descending.</returns>
        public List<DriverTask> GetActiveTasks()
        {
            return _tasks.Values
                .Where(t => !t.IsTerminal)
                .OrderByDescending(t => t.CreatedAt)
                .ToList();
        }

        /// <summary>
        /// Cancels a task by setting its phase to Cancelled and recording completion time.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <exception cref="TaskNotFoundException">Thrown if the task is not found.</exception>
        public void CancelTask(string taskId)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                throw new TaskNotFoundException($"Task not found: {taskId}");
            }

            task.Phase = TaskPhase.Cancelled;
            task.CompletedAt = DateTime.UtcNow;
            task.AddLog("Task cancelled");
        }

        /// <summary>
        /// Sends feedback to a task, which will be processed in the next cycle.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="message">The feedback message.</param>
        /// <exception cref="TaskNotFoundException">Thrown if the task is not found.</exception>
        public void SendFeedback(string taskId, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("Feedback message cannot be null or whitespace.", nameof(message));

            if (!_tasks.TryGetValue(taskId, out var task))
            {
                throw new TaskNotFoundException($"Task not found: {taskId}");
            }

            task.PendingFeedback = message;
            task.AddLog($"Feedback queued: {message}");
        }

        /// <summary>
        /// Pauses a task, preventing further processing until resumed.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <exception cref="TaskNotFoundException">Thrown if the task is not found.</exception>
        public void PauseTask(string taskId)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                throw new TaskNotFoundException($"Task not found: {taskId}");
            }

            task.IsPaused = true;
            task.AddLog("Task paused");
        }

        /// <summary>
        /// Resumes a paused task, allowing processing to continue.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="message">Optional feedback message to include when resuming.</param>
        /// <exception cref="TaskNotFoundException">Thrown if the task is not found.</exception>
        public void ResumeTask(string taskId, string? message = null)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                throw new TaskNotFoundException($"Task not found: {taskId}");
            }

            task.IsPaused = false;

            if (!string.IsNullOrWhiteSpace(message))
            {
                task.PendingFeedback = message;
                task.AddLog($"Task resumed with feedback: {message}");
            }
            else
            {
                task.AddLog("Task resumed");
            }
        }

        /// <summary>
        /// Updates the phase of a task.
        /// Sets StartedAt timestamp on first transition from Pending phase.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="phase">The new phase.</param>
        /// <exception cref="TaskNotFoundException">Thrown if the task is not found.</exception>
        public void UpdatePhase(string taskId, TaskPhase phase)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                throw new TaskNotFoundException($"Task not found: {taskId}");
            }

            // Set StartedAt on first transition from Pending
            if (task.Phase == TaskPhase.Pending && phase != TaskPhase.Pending && task.StartedAt == null)
            {
                task.StartedAt = DateTime.UtcNow;
            }

            task.Phase = phase;
            task.AddLog($"Phase updated to {phase}");
        }
    }
}
