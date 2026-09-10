using Xunit;

namespace StuiPodcast.App.Tests;

// Tests that read or write process environment variables must not run in
// parallel with each other; the environment is process-global.
[CollectionDefinition("env", DisableParallelization = true)]
public sealed class EnvCollection { }
