using System;
using System.Threading;
using System.Threading.Tasks;
using OpcDaToUaGateway.Models;
using Xunit;

namespace OpcDaToUaGateway.Tests
{
    public class OpcDaClientLifecycleTests
    {
        [Fact]
        public void AcquisitionMode_DefaultsToAsyncAndKeepsLastRecordedMode()
        {
            var state = new OpcDaClientLifecycleState();
            Assert.Equal(DaAcquisitionMode.Async, state.AcquisitionMode);

            state.RecordAcquisitionMode(DaAcquisitionMode.Sync);
            Assert.Equal(DaAcquisitionMode.Sync, state.AcquisitionMode);

            state.RecordAcquisitionMode(DaAcquisitionMode.Async);
            Assert.Equal(DaAcquisitionMode.Async, state.AcquisitionMode);
        }

        [Fact]
        public void ReadGate_AllowsOnlyOneReaderUntilExit()
        {
            var state = new OpcDaClientLifecycleState();
            state.EnableReads();

            Assert.True(state.TryEnterRead());
            Assert.False(state.TryEnterRead());
            state.ExitRead();
            Assert.True(state.TryEnterRead());
            state.ExitRead();
        }

        [Fact]
        public async Task StopReadsAndWait_WaitsForActiveReader()
        {
            var state = new OpcDaClientLifecycleState();
            state.EnableReads();
            Assert.True(state.TryEnterRead());

            Task<bool> waitTask = Task.Run(() => state.StopReadsAndWait(TimeSpan.FromSeconds(2)));
            await Task.Delay(50);
            Assert.False(waitTask.IsCompleted);

            state.ExitRead();
            Assert.True(await waitTask);
            Assert.False(state.TryEnterRead());
        }

        [Fact]
        public void StopReadsAndWait_ReturnsFalseOnTimeout()
        {
            var state = new OpcDaClientLifecycleState();
            state.EnableReads();
            Assert.True(state.TryEnterRead());

            Assert.False(state.StopReadsAndWait(TimeSpan.FromMilliseconds(20)));
            state.ExitRead();
        }
    }
}
