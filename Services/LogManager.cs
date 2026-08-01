using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OpcDaToUaGateway.Services
{
    /// <summary>
    /// 日志管理器 — 双输出通道（UI 实时显示 + 异步文件持久化）。
    /// 使用 BlockingCollection(10000) 作为生产者-消费者队列，后台线程逐条消费并写入按日期命名的日志文件。
    /// </summary>
    public class LogManager : IDisposable
    {
        private readonly TextBox _textBox;
        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(10000);
        private readonly Thread _writerThread;
        private readonly string _logDir;
        private StreamWriter _fileWriter;
        private string _currentDate;
        private volatile bool _disposed;
        private int _disposeGuard;
        private int _droppedCount;

        public LogManager(TextBox textBox)
        {
            _textBox = textBox ?? throw new ArgumentNullException(nameof(textBox));
            _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(_logDir))
                Directory.CreateDirectory(_logDir);

            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "LogWriter"
            };
            _writerThread.Start();
        }

        public void Append(string message)
        {
            if (_disposed) return;

            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string fileLine = $"[{now}] {message}";
            string uiLine = $"[{now.Substring(11)}] {message}\r\n";

            if (_textBox != null && !_textBox.IsDisposed)
            {
                try
                {
                    if (_textBox.InvokeRequired)
                        _textBox.BeginInvoke((Action)(() => UpdateTextBox(uiLine)));
                    else
                        UpdateTextBox(uiLine);
                }
catch (ObjectDisposedException) { /* Form已释放，忽略 */ }
catch (InvalidOperationException) { /* 控件已销毁，忽略 */ }
            }

            try
            {
                if (!_queue.TryAdd(fileLine))
                {
                    int dropped = Interlocked.Increment(ref _droppedCount);
                    if (dropped % 100 == 1)
                    {
                        _queue.TryAdd($"[WARNING] 日志队列已满，累计丢弃 {dropped} 条");
                        try
                        {
                            string warnPath = Path.Combine(_logDir, "dropped_warnings.log");
                            File.AppendAllText(warnPath,
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 日志队列已满，累计丢弃 {dropped} 条\r\n");
                        }
catch (Exception ex) { Debug.WriteLine($"[Log] 写入失败: {ex.Message}"); }
                    }
                }
            }
catch (InvalidOperationException) { /* TextBox已销毁，忽略 */ }
        }

        public void CleanupOldFiles()
        {
            ThreadPool.QueueUserWorkItem(_ => DoCleanup());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;
            _disposed = true;

            _queue.CompleteAdding();

            if (!_writerThread.Join(TimeSpan.FromSeconds(5)))
                System.Diagnostics.Debug.WriteLine("[LogManager] 警告: 写入线程在 5 秒内未结束");

try { _fileWriter?.Flush(); } catch (Exception ex) { Debug.WriteLine($"[Log] Flush失败: {ex.Message}"); }
try { _fileWriter?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[Log] Dispose失败: {ex.Message}"); }
            _fileWriter = null;
            _queue.Dispose();
        }

        private void UpdateTextBox(string text)
        {
            _textBox.AppendText(text);
            if (_textBox.TextLength > AppConstants.LogUiMaxChars)
                _textBox.Text = _textBox.Text.Substring(_textBox.TextLength - AppConstants.LogUiTrimChars);
        }

        private void WriterLoop()
        {
            int writeCount = 0;

            try
            {
                foreach (string line in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        string today = DateTime.Now.ToString("yyyy-MM-dd");
                        if (_currentDate != today)
                        {
                            _fileWriter?.Flush();
                            _fileWriter?.Dispose();
                            string logFile = Path.Combine(_logDir, $"gateway_{today}.log");
                            _fileWriter = new StreamWriter(logFile, true, new UTF8Encoding(true));
                            _fileWriter.AutoFlush = false;
                            _currentDate = today;
                        }

                        // 写入前检查 _fileWriter 是否为 null
                        // 防止 WriterLoop IO 异常重启失败后 _fileWriter 为 null 导致 NRE
                        if (_fileWriter == null) continue;
                        _fileWriter.WriteLine(line);

                        if (++writeCount % 10 == 0)
                            _fileWriter.Flush();

                        if (writeCount >= AppConstants.LogCleanupInterval)
                        {
                            writeCount = 0;
                            DoCleanup();
                        }
                    }
                    catch (IOException)
                    {
                        try
                        {
                            _fileWriter?.Flush();
                            _fileWriter?.Dispose();
                        }
                        catch (Exception ex2)
                        {
                            Debug.WriteLine($"[Log] 文件日志刷新/释放失败: {ex2.Message}");
                        }
                        _fileWriter = null;

                        try
                        {
                            string logFile = Path.Combine(_logDir, $"gateway_{DateTime.Now:yyyy-MM-dd}.log");
                            _fileWriter = new StreamWriter(logFile, true, new UTF8Encoding(true));
                            _fileWriter.AutoFlush = false;
                        }
                        catch (Exception ex2)
                        {
                            Debug.WriteLine($"[LogManager] 无法重新打开日志文件: {ex2.Message}，日志将降级到 Debug 输出");
                            // 文件重建失败时不要丢失日志，降级写入 Debug.WriteLine
                            Debug.WriteLine(line);
                        }
                    }
                    catch (NullReferenceException)
                    {
                        // 预期内：_fileWriter 为 null 时 WriteLine 会抛 NRE
                        // 上面 IOException 块已处理重建，忽略此异常
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Log] 日志写入未知错误: {ex.Message}");
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"[Log] WriterLoop 异常: {ex.Message}");
            }
        }

        private void DoCleanup()
        {
            try
            {
                if (!Directory.Exists(_logDir)) return;

                DateTime cutoff = DateTime.Now.AddDays(-AppConstants.LogRetentionDays);
                foreach (string file in Directory.GetFiles(_logDir, "gateway_*.log"))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.LastWriteTime < cutoff)
                            fi.Delete();
                    }
catch (Exception ex) { Debug.WriteLine($"[Log] 日志旋转失败: {ex.Message}"); }
                }
            }
catch (Exception ex) { Debug.WriteLine($"[Log] 日志清理失败: {ex.Message}"); }
        }
    }
}
