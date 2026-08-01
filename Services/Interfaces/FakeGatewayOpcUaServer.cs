using System;
using System.Collections.Generic;
using System.Threading;
using Opc.Ua;
using OpcDaToUaGateway.Models;
using OpcDaToUaGateway.Services.Interfaces;

namespace OpcDaToUaGateway.Services.Interfaces
{
    /// <summary>
    /// Fake UA server for unit tests.
    /// 
    /// Usage:
    ///   var fake = new FakeGatewayOpcUaServer();
    ///   fake.StartAsync().Wait();
    ///   fake.AddVariableNode("Tag1", "Item1", "Display1", BuiltInType.Int32);
    ///   fake.UpdateValue("Tag1", 42, true, DateTime.UtcNow);
    ///   fake.Dispose();
    /// </summary>
    public class FakeGatewayOpcUaServer : IGatewayOpcUaServer
    {
        private int _disposedInt;
        private bool _isRunning = false;
        private readonly Dictionary<string, FakeVariableNode> _variables = new Dictionary<string, FakeVariableNode>();

        public bool IsRunning => _isRunning;
        public ushort NamespaceIndex { get; set; } = 2;
        public string NamespaceUri { get; set; } = "http://example.com/opcua/gateway";
        public int VariableCount => _variables.Count;
        public int DaTagsChildrenCount => -1;
        public int FlatTagsChildrenCount => -1;
        public int FlatTagsBrowseCount => -1;
        public event Action<string> OnStatusChanged;
        public Action OnConfigChanged { get; set; }

        public IReadOnlyDictionary<string, FakeVariableNode> Variables => _variables;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int AddNodeCount { get; private set; }
        public int UpdateCount { get; private set; }
        public bool ThrowOnAddNode { get; set; }
        public bool ThrowOnUpdate { get; set; }

        public System.Threading.Tasks.Task StartAsync()
        {
            if (Volatile.Read(ref _disposedInt) == 1)
                throw new ObjectDisposedException(nameof(FakeGatewayOpcUaServer));
            _isRunning = true;
            StartCount++;
            OnStatusChanged?.Invoke("[Fake] Simulated UA server started");
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public System.Threading.Tasks.Task StopAsync()
        {
            if (Volatile.Read(ref _disposedInt) == 1)
                throw new ObjectDisposedException(nameof(FakeGatewayOpcUaServer));
            _isRunning = false;
            StopCount++;
            OnStatusChanged?.Invoke("[Fake] Simulated UA server stopped");
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public void AddVariableNode(string tagKey, string itemId, string displayName, BuiltInType dataType, string nodeId = null)
        {
            if (Volatile.Read(ref _disposedInt) == 1)
                throw new ObjectDisposedException(nameof(FakeGatewayOpcUaServer));
            if (!_isRunning)
                throw new InvalidOperationException("UA server is not running");
            if (ThrowOnAddNode)
                throw new InvalidOperationException("[Fake] Simulated node creation failure");
            if (_variables.ContainsKey(tagKey))
                throw new ArgumentException($"TagKey '{tagKey}' already exists", nameof(tagKey));

            _variables[tagKey] = new FakeVariableNode
            {
                TagKey = tagKey, ItemId = itemId, DisplayName = displayName,
                DataType = dataType, NodeId = nodeId ?? $"ns={NamespaceIndex};s={tagKey}",
                Value = null, IsGood = false, Timestamp = DateTime.MinValue
            };
            AddNodeCount++;
        }

        public void UpdateValue(string tagKey, object value, bool isGood, DateTime sourceTimestamp)
        {
            if (Volatile.Read(ref _disposedInt) == 1)
                throw new ObjectDisposedException(nameof(FakeGatewayOpcUaServer));
            if (ThrowOnUpdate)
                throw new InvalidOperationException("[Fake] Simulated update failure");
            if (!_variables.TryGetValue(tagKey, out var node))
                throw new KeyNotFoundException($"TagKey '{tagKey}' does not exist");

            node.Value = value;
            node.IsGood = isGood;
            node.Timestamp = sourceTimestamp;
            UpdateCount++;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedInt, 1) == 0)
            {
                _isRunning = false;
                _variables.Clear();
                OnStatusChanged = null;
            }
        }

        public class FakeVariableNode
        {
            public string TagKey { get; set; }
            public string ItemId { get; set; }
            public string DisplayName { get; set; }
            public BuiltInType DataType { get; set; }
            public string NodeId { get; set; }
            public object Value { get; set; }
            public bool IsGood { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}