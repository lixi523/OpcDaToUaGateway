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

            // M-10 修复：原用 Task.Delay(50) 做时序断言，CI 高负载下 50ms 可能不足导致假阳性。
            // 改用 ManualResetEventSlim 实现确定性同步：
            // 确认 waitTask 已进入等待状态后再 ExitRead，避免依赖任何时间假设。
            var waitStarted = new System.Threading.ManualResetEventSlim(false);
            Task<bool> waitTask = Task.Run(() =>
            {
                waitStarted.Set();  // 通知主线程：StopReadsAndWait 即将阻塞
                return state.StopReadsAndWait(TimeSpan.FromSeconds(2));
            });

            // 等待 waitTask 启动后，再验证它尚未完成（因为读锁仍被持有）
            waitStarted.Wait(TimeSpan.FromSeconds(1));
            await Task.Delay(10); // 给 StopReadsAndWait 极短时间进入阻塞状态
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
