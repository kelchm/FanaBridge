using Xunit;

// Several test classes toggle process-wide statics (DisplayRuleStack.LegacyRuleWrites)
// while others depend on their defaults — parallel collections race on them (surfaced
// as order-dependent failures during S4). The full suite runs in under a second, so
// parallel execution buys nothing here.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
