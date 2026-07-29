using Xunit;

// Several test classes still share process-wide state; the full suite is fast enough
// that parallel execution buys nothing here.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
