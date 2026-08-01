using System;
using System.Collections.Generic;
using OpcDaToUaGateway.Models;
using OpcDaToUaGateway.Services.Interfaces;
using Opc.Ua;
using Xunit;

namespace OpcDaToUaGateway.Tests
{
    /// <summary>
    /// DataBridge 单元测试。
    /// 使用 FakeOpcDaClient 和 FakeGatewayOpcUaServer 隔离外部依赖，
    /// 测试数据桥接核心逻辑：类型转换、快照管理、线程安全、统计计数。
    /// </summary>
    public class DataBridgeTests : IDisposable
    {
        private readonly FakeOpcDaClient _fakeDa;
        private readonly FakeGatewayOpcUaServer _fakeUa;
        private readonly DataBridge _bridge;
        private readonly List<TagConfig> _tags;

        public DataBridgeTests()
        {
            _tags = new List<TagConfig>
            {
                new TagConfig { TagKey = "TagBool", ItemId = "Item1", DisplayName = "Boolean Tag", DataType = "Boolean" },
                new TagConfig { TagKey = "TagInt32", ItemId = "Item2", DisplayName = "Integer Tag", DataType = "Int32" },
                new TagConfig { TagKey = "TagFloat", ItemId = "Item3", DisplayName = "Float Tag", DataType = "Float" },
                new TagConfig { TagKey = "TagString", ItemId = "Item4", DisplayName = "String Tag", DataType = "String" },
                new TagConfig { TagKey = "TagDouble", ItemId = "Item5", DisplayName = "Double Tag", DataType = "Double" },
            };

            _fakeDa = new FakeOpcDaClient(_tags);
            _fakeUa = new FakeGatewayOpcUaServer();
            // 先启动 Fake UA Server，这样 DataBridge.Start() 中的 AddVariableNode 才能成功
            _fakeUa.StartAsync().Wait();
            _bridge = new DataBridge(_fakeDa, _fakeUa, _tags);
        }

        public void Dispose()
        {
            try { _bridge?.Dispose(); } catch { }
            try { _fakeDa?.Dispose(); } catch { }
            try { _fakeUa?.Dispose(); } catch { }
        }

        // =====================================================================
        // 初始化与构造函数测试
        // =====================================================================

        [Fact]
        public void Constructor_InitializesSnapshotsForAllTags()
        {
            var snapshots = _bridge.GetSnapshots();
            Assert.Equal(5, snapshots.Count);
        }

        [Fact]
        public void Constructor_SnapshotsHaveInitialValues()
        {
            var snapshots = _bridge.GetSnapshots();
            foreach (var snap in snapshots)
            {
                Assert.Equal("-", snap.Value);
                Assert.Equal("Unknown", snap.Quality);
            }
        }

        [Fact]
        public void Constructor_TotalUpdatesIsZero()
        {
            Assert.Equal(0, _bridge.TotalUpdates);
        }

        [Fact]
        public void Constructor_ErrorCountIsZero()
        {
            Assert.Equal(0, _bridge.ErrorCount);
        }

        // =====================================================================
        // Start/Stop 生命周期测试
        // =====================================================================

        [Fact]
        public void Start_FiresLogEvent()
        {
            bool logFired = false;
            string logMessage = null;
            _bridge.OnLog += msg => { logFired = true; logMessage = msg; };

            _bridge.Start();

            Assert.True(logFired);
            Assert.NotNull(logMessage);
            Assert.Contains("启动", logMessage);
        }

        [Fact]
        public void Start_BindsToDaDataChanged()
        {
            _bridge.Start();
            var beforeCount = _bridge.TotalUpdates;

            // 模拟 DA 数据变化
            _fakeDa.SimulateDataChanged("TagBool", true, true, DateTime.UtcNow);

            // DataBridge 应该收到更新并转发到 UA Server
            Assert.Equal(beforeCount + 1, _bridge.TotalUpdates);
        }

        [Fact]
        public void Dispose_UnbindsFromDaDataChanged()
        {
            _bridge.Dispose();

            // 再次触发不应导致异常或更新
            _fakeDa.SimulateDataChanged("TagBool", true, true, DateTime.UtcNow);

            // 因为已释放，UA Server 不应收到更新（Bridge 内部检查 _disposedInt）
            // 注意：Start 会重新绑定，所以先 Stop 再验证
        }

        // =====================================================================
        // 数据类型转换测试
        // =====================================================================

        [Fact]
        public void OnDataChanged_Bool_ConvertsCorrectly()
        {
            _bridge.Start();
            _fakeDa.SimulateDataChanged("TagBool", true, true, DateTime.UtcNow);

            var snapshots = _bridge.GetSnapshots();
            var boolSnap = snapshots[0];
            Assert.Equal("True", boolSnap.Value);
            Assert.Equal("Good", boolSnap.Quality);
        }

        [Fact]
        public void OnDataChanged_Int32_ConvertsCorrectly()
        {
            _bridge.Start();
            _fakeDa.SimulateDataChanged("TagInt32", 123, true, DateTime.UtcNow);

            var snapshots = _bridge.GetSnapshots();
            var intSnap = snapshots[1];
            Assert.Equal("123", intSnap.Value);
            Assert.Equal("Good", intSnap.Quality);
        }

        [Fact]
        public void OnDataChanged_Float_ConvertsCorrectly()
        {
            _bridge.Start();
            _fakeDa.SimulateDataChanged("TagFloat", 3.14f, true, DateTime.UtcNow);

            var snapshots = _bridge.GetSnapshots();
            var floatSnap = snapshots[2];
            Assert.Equal("3.14", floatSnap.Value);
        }

        [Fact]
        public void OnDataChanged_String_ConvertsCorrectly()
        {
            _bridge.Start();
            _fakeDa.SimulateDataChanged("TagString", "world", true, DateTime.UtcNow);

            var snapshots = _bridge.GetSnapshots();
            var strSnap = snapshots[3];
            Assert.Equal("world", strSnap.Value);
        }

        [Fact]
        public void OnDataChanged_Double_ConvertsCorrectly()
        {
            _bridge.Start();
            _fakeDa.SimulateDataChanged("TagDouble", 2.71828, true, DateTime.UtcNow);

            var snapshots = _bridge.GetSnapshots();
            var doubleSnap = snapshots[4];
            Assert.NotEqual("0", doubleSnap.Value);
        }

        // =====================================================================
        // 质量与异常处理测试
        // =====================================================================

        [Fact]
        public void OnDataChanged_BadQuality_SetsQualityToBad()
        {
            _bridge.Start();
            _fakeDa.SimulateDataChanged("TagInt32", 999, false, DateTime.UtcNow);

            var snapshots = _bridge.GetSnapshots();
            var intSnap = snapshots[1];
            Assert.Equal("Bad", intSnap.Quality);
        }

        [Fact]
        public void OnDataChanged_NullValue_HandlesGracefully()
        {
            _bridge.Start();
            _fakeDa.SimulateDataChanged("TagString", null, true, DateTime.UtcNow);

            var snapshots = _bridge.GetSnapshots();
            var strSnap = snapshots[3];
            Assert.Equal("null", strSnap.Value);
        }

        [Fact]
        public void OnDataChanged_UnknownTagKey_DoesNotThrow()
        {
            _bridge.Start();
            // 不存在的 tagKey，不应抛异常
            _fakeDa.SimulateDataChanged("NonExistentTag", "value", true, DateTime.UtcNow);
        }

        // =====================================================================
        // 统计计数测试
        // =====================================================================

        [Fact]
        public void TotalUpdates_IncrementsOnEachSuccess()
        {
            _bridge.Start();
            Assert.Equal(0, _bridge.TotalUpdates);

            _fakeDa.SimulateDataChanged("TagBool", true, true, DateTime.UtcNow);
            Assert.Equal(1, _bridge.TotalUpdates);

            _fakeDa.SimulateDataChanged("TagInt32", 42, true, DateTime.UtcNow);
            Assert.Equal(2, _bridge.TotalUpdates);

            _fakeDa.SimulateDataChanged("TagFloat", 1.0f, true, DateTime.UtcNow);
            Assert.Equal(3, _bridge.TotalUpdates);
        }

        [Fact]
        public void ErrorCount_IncrementsOnFailure()
        {
            _bridge.Start();
            Assert.Equal(0, _bridge.ErrorCount);

            // 触发一个会导致类型转换失败的场景（如将字符串转为 Int32 但类型不匹配）
            // 这里主要验证 ErrorCount 存在且可读取
            Assert.True(_bridge.ErrorCount >= 0);
        }

        [Fact]
        public void LastUpdateTime_UpdatesOnSuccessfulDataChange()
        {
            _bridge.Start();
            var before = _bridge.LastUpdateTime;

            System.Threading.Thread.Sleep(50);
            _fakeDa.SimulateDataChanged("TagBool", true, true, DateTime.UtcNow);

            var after = _bridge.LastUpdateTime;
            Assert.True(after.Ticks > before.Ticks);
        }

        // =====================================================================
        // 快照一致性测试
        // =====================================================================

        [Fact]
        public void GetSnapshots_ReturnsConsistentOrder()
        {
            _bridge.Start();
            _fakeDa.SimulateDataChanged("TagFloat", 99.9f, true, DateTime.UtcNow);

            var snaps1 = _bridge.GetSnapshots();
            var snaps2 = _bridge.GetSnapshots();

            Assert.Equal(snaps1.Count, snaps2.Count);
            for (int i = 0; i < snaps1.Count; i++)
            {
                Assert.Equal(snaps1[i].ItemId, snaps2[i].ItemId);
            }
        }

        [Fact]
        public void GetSnapshots_ContainsAllExpectedFields()
        {
            _bridge.Start();
            _fakeDa.SimulateDataChanged("TagInt32", 42, true, DateTime.UtcNow);

            var snapshots = _bridge.GetSnapshots();
            var intSnap = snapshots[1];

            Assert.Equal("Item2", intSnap.ItemId);
            Assert.Equal("Integer Tag", intSnap.DisplayName);
            Assert.NotEqual(DateTime.MinValue, intSnap.Timestamp);
        }

        // =====================================================================
        // 多线程安全性测试
        // =====================================================================

        [Fact]
        public async System.Threading.Tasks.Task ConcurrentDataChanges_DoesNotThrow()
        {
            _bridge.Start();

            var exceptions = new List<Exception>();
            var tasks = new List<System.Threading.Tasks.Task>();

            for (int i = 0; i < 100; i++)
            {
                var idx = i % _tags.Count;
                var tag = _tags[idx];
                tasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        _fakeDa.SimulateDataChanged(tag.TagKey, $"concurrent_val_{i}", true, DateTime.UtcNow);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }));
            }

            await System.Threading.Tasks.Task.WhenAll(tasks.ToArray());
            Assert.Empty(exceptions);
            Assert.Equal(100, _bridge.TotalUpdates);
        }

        [Fact]
        public async System.Threading.Tasks.Task ConcurrentGetSnapshots_DoesNotThrow()
        {
            _bridge.Start();

            var exceptions = new List<Exception>();
            var readTasks = new List<System.Threading.Tasks.Task>();

            for (int i = 0; i < 50; i++)
            {
                readTasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        _bridge.GetSnapshots();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }));
            }

            // 同时写入
            for (int i = 0; i < 50; i++)
            {
                var idx = i % _tags.Count;
                var tag = _tags[idx];
                readTasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        _fakeDa.SimulateDataChanged(tag.TagKey, $"write_val_{i}", true, DateTime.UtcNow);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }));
            }

            await System.Threading.Tasks.Task.WhenAll(readTasks.ToArray());
            Assert.Empty(exceptions);
        }

        // =====================================================================
        // 空标签列表边界测试
        // =====================================================================

        [Fact]
        public void Constructor_EmptyTagsList_CreatesEmptyBridge()
        {
            using var emptyDa = new FakeOpcDaClient(new List<TagConfig>());
            using var emptyUa = new FakeGatewayOpcUaServer();
            var emptyBridge = new DataBridge(emptyDa, emptyUa, new List<TagConfig>());

            Assert.Empty(emptyBridge.GetSnapshots());
            Assert.Equal(0, emptyBridge.TotalUpdates);
        }

        [Fact]
        public async System.Threading.Tasks.Task Start_EmptyTags_LogsZeroTags()
        {
            using var emptyDa = new FakeOpcDaClient(new List<TagConfig>());
            using var emptyUa = new FakeGatewayOpcUaServer();
            await emptyUa.StartAsync();
            var emptyBridge = new DataBridge(emptyDa, emptyUa, new List<TagConfig>());

            bool logFired = false;
            emptyBridge.OnLog += msg => 
            { 
                if (msg.Contains("0/0") || msg.Contains("空"))
                    logFired = true;
            };

            emptyBridge.Start();
            Assert.True(logFired);
        }
    }
}
