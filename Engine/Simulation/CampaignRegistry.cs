using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace ExoStudio.Simulation
{
    public sealed class CampaignRegistry
    {
        private readonly ConcurrentDictionary<string, Campaign> campaigns = new();

        public Campaign Add(Campaign c)
        {
            campaigns[c.Id] = c;
            PruneOld();
            return c;
        }

        public Campaign Get(string id) =>
            id != null && campaigns.TryGetValue(id, out Campaign c) ? c : null;

        public IEnumerable<Campaign> All => campaigns.Values;

        public bool Remove(string id) => campaigns.TryRemove(id, out _);

        /// <summary>Single-process demo server, so campaigns live in memory; drop finished ones after an hour.</summary>
        private void PruneOld()
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (Campaign c in campaigns.Values.Where(c => c.State == CampaignState.Finished && c.CreatedUtc < cutoff).ToList())
            {
                campaigns.TryRemove(c.Id, out _);
            }
        }
    }

    /// <summary>
    /// The heartbeat. One thread advances every running campaign against measured real time,
    /// which is the only place wall-clock time enters the system at all: it is converted to
    /// simulated seconds immediately (SimulationClock.Advance) and nothing downstream ever
    /// sees it again. That is what makes the warp invariant hold.
    /// </summary>
    public sealed class CampaignTicker : BackgroundService
    {
        private const int TickHz = 20;

        /// <summary>
        /// Ceiling on a single slice. If the process is descheduled (GC pause, laptop sleep,
        /// a debugger breakpoint), the elapsed wall time should not be multiplied by the warp
        /// rate and teleport the simulation years into the future. Time simply does not
        /// advance while we are not looking.
        /// </summary>
        private const double MaxSliceSeconds = 0.25;

        private readonly CampaignRegistry registry;

        public CampaignTicker(CampaignRegistry registry) => this.registry = registry;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var sw = Stopwatch.StartNew();
            double last = sw.Elapsed.TotalSeconds;
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / TickHz));

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                double now = sw.Elapsed.TotalSeconds;
                double slice = Math.Min(now - last, MaxSliceSeconds);
                last = now;

                foreach (Campaign c in registry.All)
                {
                    try
                    {
                        c.Tick(slice);
                    }
                    catch (Exception ex)
                    {
                        c.Stop("Simulation error: " + ex.Message);
                    }
                }
            }
        }
    }
}
