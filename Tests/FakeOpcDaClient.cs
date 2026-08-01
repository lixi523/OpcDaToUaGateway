using System;
using System.Collections.Generic;
using OpcDaToUaGateway.Models;
using OpcDaToUaGateway.Services.Interfaces;

namespace OpcDaToUaGateway.Tests
{
    /// <summary>
    /// IOpcDaClient 的 Fake 实现，用于 DataBridge 单元测试。
    /// 允许测试代码手动触发 OnDataChanged 事件。
    /// </summary>
    public class FakeOpcDaClient : IOpcDaClient
    {
        public bool IsConnected { get; private set; }

        public event Action<string, object, bool, DateTime> OnDataChanged;
        public event Action<string> OnStatusChanged;

        private readonly List<TagConfig> _tags;
        private int _updateRateMs;
        private DaAcquisitionMode _mode;

        public FakeOpcDaClient(List<TagConfig> tags = null)
        {
            _tags = tags ?? new List<TagConfig>();
        }

        public void Start(int updateRateMs, DaAcquisitionMode mode)
        {
            _updateRateMs = updateRateMs;
            _mode = mode;
            IsConnected = true;
            OnStatusChanged?.Invoke("[FakeDA] 已连接");
        }

        public void Stop()
        {
            IsConnected = false;
            OnStatusChanged?.Invoke("[FakeDA] 已断开");
        }

        public bool TryReconnect(int updateRateMs)
        {
            Start(updateRateMs, _mode);
            return true;
        }

        /// <summary>模拟 DA 数据变化事件，触发所有订阅者的回调。</summary>
        public void SimulateDataChanged(string tagKey, object value, bool isGood = true, DateTime timestamp = default)
        {
            if (timestamp == default) timestamp = DateTime.UtcNow;
            OnDataChanged?.Invoke(tagKey, value, isGood, timestamp);
        }

        /// <summary>批量模拟多个标签的数据变化。</summary>
        public void SimulateAllDataChanged()
        {
            // 占位，实际由测试代码逐个调用 SimulateDataChanged
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
