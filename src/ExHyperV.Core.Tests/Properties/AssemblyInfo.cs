using Microsoft.VisualStudio.TestTools.UnitTesting;

// Culture overrides and host caches deliberately have process-wide ownership.
// Separate architectures run in separate processes; scenarios must not race each other.
[assembly: DoNotParallelize]
