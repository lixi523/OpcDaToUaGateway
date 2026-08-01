using System;
using System.IO;
using OpcDaToUaGateway.Services;
using Xunit;

namespace OpcDaToUaGateway.Tests
{
    public class TrialStateStoreTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "OpcDaToUaGateway.Tests", Guid.NewGuid().ToString("N"));

        [Fact]
        public void SaveThenLoad_RoundTripsElapsedSeconds()
        {
            string path = Path.Combine(_directory, "trial.dat");
            var store = new TrialStateStore(path);

            Assert.True(store.TrySave(125));
            Assert.Equal(TrialStateLoadResult.Success, store.TryLoad(out long elapsedSeconds));
            Assert.Equal(125, elapsedSeconds);
        }

        [Fact]
        public void Load_WhenFileDoesNotExist_ReturnsNotFound()
        {
            var store = new TrialStateStore(Path.Combine(_directory, "missing.dat"));

            Assert.Equal(TrialStateLoadResult.NotFound, store.TryLoad(out long elapsedSeconds));
            Assert.Equal(0, elapsedSeconds);
        }

        [Fact]
        public void Initialize_WhenFileDoesNotExist_CreatesVerifiedZeroState()
        {
            string path = Path.Combine(_directory, "trial.dat");
            var store = new TrialStateStore(path);

            Assert.Equal(TrialStateLoadResult.Success, store.Initialize(out long elapsedSeconds));
            Assert.Equal(0, elapsedSeconds);
            Assert.True(File.Exists(path));
            Assert.Equal(TrialStateLoadResult.Success, store.TryLoad(out long persistedSeconds));
            Assert.Equal(0, persistedSeconds);
        }

        [Fact]
        public void Load_WhenFileIsCorrupt_ReturnsInvalid()
        {
            string path = Path.Combine(_directory, "trial.dat");
            Directory.CreateDirectory(_directory);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            var store = new TrialStateStore(path);

            Assert.Equal(TrialStateLoadResult.Invalid, store.TryLoad(out long elapsedSeconds));
            Assert.Equal(0, elapsedSeconds);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }
    }
}
