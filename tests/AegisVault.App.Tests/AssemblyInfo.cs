using Xunit;

// P-35: the Avalonia headless session owns a single dispatcher thread. xUnit runs
// test collections in parallel by default, so two collections can race to create
// the isolated application and one of them lands on a foreign thread
// ("The calling thread cannot access this object because a different thread owns it").
// UI tests are cheap enough to run sequentially; correctness wins over speed.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
