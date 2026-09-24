using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Deferred job registry for VRChat avatar analysis and SDK builds (vrc/build).
    ///
    /// Why these routes cannot answer synchronously: an NDMF bake blocks Unity's main thread
    /// for 20-100s (on a real avatar the texture-compression pass alone runs ~10s). The bridge
    /// serves HTTP from that same main thread, so for the whole bake it answers nothing — the
    /// server's queue poll times out and the ticket is evicted before the result can be read,
    /// even though Unity finished the work.
    ///
    /// So the route submits instead: it registers a job, schedules the work on the next editor
    /// tick, and returns a jobId straight away. The bake still blocks the main thread when it
    /// runs, but the result now lives here rather than in the queue's completed-ticket cache,
    /// so a poll that arrives after the block still finds it.
    ///
    /// Mirrors the testing/run-tests + testing/get-job pair already used for test runs.
    /// </summary>
    public static class MCPVRChatJobs
    {
        public const string StatusRunning = "running";
        public const string StatusCompleted = "completed";
        public const string StatusFailed = "failed";

        /// <summary>Finished jobs are dropped this long after they complete.</summary>
        private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

        private class Job
        {
            public string JobId;
            public string Route;
            public string Status;
            public object Result;
            public string Error;
            public DateTime StartedAt;
            public DateTime? FinishedAt;
        }

        private static readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>();

        /// <summary>
        /// Run <paramref name="work"/> on a later editor tick and return a jobId immediately.
        /// Poll vrc/avatar/job with that id for the result.
        /// </summary>
        public static object Start(string route, Func<object> work)
        {
            return StartAsync(route, () => Task.FromResult(work()));
        }

        /// <summary>
        /// As <see cref="Start"/>, for work that awaits (a VRChat SDK build): the job completes when the
        /// task does. Continuations resume on the main thread through Unity's synchronization context.
        /// </summary>
        public static object StartAsync(string route, Func<Task<object>> work)
        {
            CleanupExpired();

            var job = new Job
            {
                JobId = Guid.NewGuid().ToString("N").Substring(0, 12),
                Route = route,
                Status = StatusRunning,
                StartedAt = DateTime.UtcNow,
            };
            _jobs[job.JobId] = job;

            // A one-shot update, not delayCall: delayCall does not fire while the editor is in the
            // background (verified live), which is exactly when an agent is driving it. Either way
            // the work runs on a later tick, after this HTTP response has been sent.
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                EditorApplication.update -= tick;
                Run(job, work);
            };
            EditorApplication.update += tick;

            return new Dictionary<string, object>
            {
                { "success", true },
                { "jobId", job.JobId },
                { "status", StatusRunning },
                { "route", route },
                { "hint", "Poll vrc/avatar/job with this jobId until status is no longer 'running'." }
            };
        }

        // async void is safe here: every exception is caught and recorded on the job.
        private static async void Run(Job job, Func<Task<object>> work)
        {
            try
            {
                job.Result = await work();
                job.Status = StatusCompleted;
            }
            catch (Exception ex)
            {
                // A bake failure must surface as an error, never as partial or
                // scene-derived figures.
                job.Error = ex.Message;
                job.Status = StatusFailed;
                Debug.LogWarning($"[MCP VRChat] Job {job.JobId} ({job.Route}) failed: {ex}");
            }
            finally
            {
                job.FinishedAt = DateTime.UtcNow;
            }
        }

        /// <summary>Route: vrc/avatar/job — status, and the result once finished.</summary>
        public static object GetJob(Dictionary<string, object> args)
        {
            string jobId = args != null && args.TryGetValue("jobId", out var j) && j != null
                ? j.ToString()
                : null;

            if (string.IsNullOrEmpty(jobId))
            {
                // No id: report the most recent job, matching testing/get-job.
                if (_jobs.Count == 0)
                    return new Dictionary<string, object> { { "error", "No VRChat analysis jobs found." } };
                jobId = _jobs.Values.OrderByDescending(x => x.StartedAt).First().JobId;
            }

            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return new Dictionary<string, object>
                {
                    { "error", $"VRChat analysis job '{jobId}' not found or expired." },
                    { "availableJobs", _jobs.Keys.ToArray() }
                };
            }

            var payload = new Dictionary<string, object>
            {
                { "success", job.Status != StatusFailed },
                { "jobId", job.JobId },
                { "route", job.Route },
                { "status", job.Status },
                { "elapsedMs", (long)((job.FinishedAt ?? DateTime.UtcNow) - job.StartedAt).TotalMilliseconds }
            };

            if (job.Status == StatusCompleted) payload["result"] = job.Result;
            if (job.Status == StatusFailed) payload["error"] = job.Error;

            return payload;
        }

        private static void CleanupExpired()
        {
            var now = DateTime.UtcNow;
            var stale = _jobs.Values
                .Where(x => x.FinishedAt.HasValue && now - x.FinishedAt.Value > Retention)
                .Select(x => x.JobId)
                .ToList();
            foreach (var id in stale) _jobs.Remove(id);
        }
    }
}
