using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Opc.Ua;
using OpcDaToUaGateway.Services.Interfaces;
using Xunit;

namespace OpcDaToUaGateway.Tests
{
    public class FakeGatewayOpcUaServerTests : IDisposable
    {
        private readonly FakeGatewayOpcUaServer _fake;

        public FakeGatewayOpcUaServerTests()
        {
            _fake = new FakeGatewayOpcUaServer();
        }

        public void Dispose()
        {
            try { _fake.Dispose(); } catch { }
        }

        [Fact]
        public async Task StartAsync_SetsRunningState()
        {
            await _fake.StartAsync();
            Assert.True(_fake.IsRunning);
        }

        [Fact]
        public async Task StopAsync_SetsStoppedState()
        {
            await _fake.StartAsync();
            await _fake.StopAsync();
            Assert.False(_fake.IsRunning);
        }

        [Fact]
        public async Task AddNode_AddsToInternalCollection()
        {
            await _fake.StartAsync();
            _fake.AddVariableNode("Tag1", "Item1", "Display1", BuiltInType.Int32);
            Assert.Equal(1, _fake.VariableCount);
        }

        [Fact]
        public async Task UpdateValue_UpdatesNodeData()
        {
            await _fake.StartAsync();
            _fake.AddVariableNode("Tag1", "Item1", "Display1", BuiltInType.Float);
            _fake.UpdateValue("Tag1", 42.5f, true, DateTime.UtcNow);
            Assert.True(_fake.Variables["Tag1"].IsGood);
            Assert.Equal(42.5f, _fake.Variables["Tag1"].Value);
        }

        [Fact]
        public async Task FaultInjection_ThrowOnAddNode_RaisesException()
        {
            _fake.ThrowOnAddNode = true;
            await _fake.StartAsync();
            // H-03 修复：AddVariableNode 是同步方法，ThrowsAnyAsync 无法捕获同步异常
            // （同步异常在 lambda 里抛出但不进入 Task，ThrowsAnyAsync 捕获的是 Task 内异常）。
            // 改为 Assert.ThrowsAny<Exception>() 捕获同步异常。
            Assert.ThrowsAny<Exception>(() =>
                _fake.AddVariableNode("Fail", "Item", "Disp", BuiltInType.String));
        }

        [Fact]
        public async Task FaultInjection_ThrowOnUpdate_RaisesException()
        {
            _fake.ThrowOnUpdate = true;
            await _fake.StartAsync();
            _fake.AddVariableNode("Tag1", "Item1", "Display1", BuiltInType.Int32);
            // H-03 修复：UpdateValue 是同步方法，改为 Assert.ThrowsAny<Exception>。
            Assert.ThrowsAny<Exception>(() =>
                _fake.UpdateValue("Tag1", 0, true, DateTime.UtcNow));
        }

        [Fact]
        public async Task GetVariable_ReturnsCorrectData()
        {
            await _fake.StartAsync();
            _fake.AddVariableNode("K1", "I1", "D1", BuiltInType.Boolean);
            _fake.UpdateValue("K1", true, true, DateTime.UtcNow);
            var node = _fake.Variables["K1"];
            Assert.Equal("K1", node.TagKey);
            Assert.Equal("I1", node.ItemId);
            Assert.Equal("D1", node.DisplayName);
            Assert.True(node.IsGood);
        }

        [Fact]
        public void VariableCount_Zero_ByDefault()
        {
            Assert.Equal(0, _fake.VariableCount);
        }

        [Fact]
        public async Task NamespaceIndex_DefaultValue()
        {
            await _fake.StartAsync();
            Assert.Equal(2, _fake.NamespaceIndex);
        }

        [Fact]
        public async Task AddNode_ThrowsWhenNotRunning()
        {
            // Do NOT call StartAsync - server is not running
            // H-03 修复：AddVariableNode 是同步方法，改为 Assert.ThrowsAny<Exception>。
            Assert.ThrowsAny<Exception>(() =>
                _fake.AddVariableNode("X", "Y", "Z", BuiltInType.String));
        }

        [Fact]
        public async Task AddNode_DuplicateKey_Throws()
        {
            await _fake.StartAsync();
            _fake.AddVariableNode("Dup", "I1", "D1", BuiltInType.Int32);
            // H-03 修复：改为同步 Assert.ThrowsAny。
            Assert.ThrowsAny<Exception>(() =>
                _fake.AddVariableNode("Dup", "I2", "D2", BuiltInType.Int32));
        }

        [Fact]
        public async Task UpdateValue_NonExistentKey_Throws()
        {
            await _fake.StartAsync();
            // H-03 修复：UpdateValue 是同步方法，改为 Assert.ThrowsAny<Exception>。
            Assert.ThrowsAny<Exception>(() =>
                _fake.UpdateValue("NoKey", 0, true, DateTime.UtcNow));
        }

        [Fact]
        public async Task Disposed_StartAsync_ThrowsObjectDisposed()
        {
            _fake.Dispose();
            // M-09 修复：.Wait() 将异步异常包装为 AggregateException，Assert.Throws<ObjectDisposedException>
            // 无法匹配内层异常。改用 await Assert.ThrowsAsync<ObjectDisposedException>。
            await Assert.ThrowsAsync<ObjectDisposedException>(() => _fake.StartAsync());
        }

        [Fact]
        public void Disposed_AddNode_ThrowsObjectDisposed()
        {
            _fake.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _fake.AddVariableNode("X", "Y", "Z", BuiltInType.String));
        }
    }
}
