// Load profiles from docs/plans/test-plan.md, section 6. Each returns, for elapsed time t (ms), how many devices are
// signed in, and whether devices are "offline" (queueing without sending), which is how the recovery storm is made.
export const PROFILES = {
  baseline: {
    describe: (o) => `${o.devices} devices signing in over ${o.rampSec}s, then steady for the rest of ${o.durationSec}s`,
    activeAt: (t, o) => Math.min(o.devices, Math.floor((o.devices * t) / Math.max(1, o.rampSec * 1000))),
    offlineAt: () => false,
  },
  soak: {
    describe: (o) => `${o.devices} devices for ${o.durationSec}s (long run: watch for memory growth and slow-downs)`,
    activeAt: (t, o) => Math.min(o.devices, Math.floor((o.devices * t) / Math.max(1, o.rampSec * 1000))),
    offlineAt: () => false,
  },
  step: {
    describe: (o) => `steps of ${o.steps.join(' -> ')} devices, ${o.stepSec}s each`,
    activeAt: (t, o) => o.steps[Math.min(o.steps.length - 1, Math.floor(t / (o.stepSec * 1000)))],
    offlineAt: () => false,
    duration: (o) => o.steps.length * o.stepSec,
    maxDevices: (o) => Math.max(...o.steps),
  },
  bell: {
    describe: (o) => `${o.devices} devices all signing in within ${o.bellSec}s (morning bell), then steady`,
    activeAt: (t, o) => Math.min(o.devices, Math.floor((o.devices * t) / Math.max(1, o.bellSec * 1000))),
    offlineAt: () => false,
  },
  recovery: {
    describe: (o) => `${o.devices} devices; after ${o.rampSec}s ramp + 60s steady they stop sending for ${o.outageSec}s (collector outage), then all reconnect at once with their backlog`,
    activeAt: (t, o) => Math.min(o.devices, Math.floor((o.devices * t) / Math.max(1, o.rampSec * 1000))),
    offlineAt: (t, o) => t >= (o.rampSec + 60) * 1000 && t < (o.rampSec + 60 + o.outageSec) * 1000,
  },
};
