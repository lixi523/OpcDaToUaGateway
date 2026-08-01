using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpcDaToUaGateway.Models;

namespace OpcDaToUaGateway.Services
{
    /// <summary>
    /// 点位配置 CSV 导出/导入服务。
    /// 
    /// CSV 格式规范（6 列，逗号分隔）：
    ///   A1: #不用改        B1: #服务器:        C1: [OPCDA 服务器 ProgId]  D1: ""  E1: ""
    ///   A2: #不用改        B2: #导出时间:      C2: [导出时间 yyyy-MM-dd HH:mm:ss]  D2: ""  E2: ""
    ///   A3: #修改C3        B3: #已选点位:      C3: [标签数量]（用户可手动修改）  D3: ""  E3: ""
    ///   A4-E4: 空行 (,,,,)
    ///   A5-E5: 空行 (,,,,)
    ///   A6: 序号           B6: ItemId          C6: 名称            D6: 数据类型      E6: 描述          F6: UA完整地址
    ///   A7+: [序号]         [ItemId]            [名称/TagKey]       [DataType]      [描述/UaNodeId]   [ns=N;s=...]
    /// </summary>
    public static class CsvTagExporter
    {
        /// <summary>
        /// 将当前配置导出为 CSV 文件。
        /// </summary>
        /// <param name="tags">标签列表</param>
        /// <param name="progId">OPC DA 服务器 ProgId</param>
        /// <param name="filePath">输出文件路径</param>
        /// <param name="namespaceIndex">UA 命名空间索引（默认 2）</param>
        /// <param name="log">日志记录器（可选）</param>
        /// <returns>导出成功返回 true</returns>
        public static bool ExportToFile(List<TagConfig> tags, string progId, string filePath, ushort namespaceIndex = 2, LogManager log = null)
        {
            if (tags == null || tags.Count == 0)
            {
                log?.Append("[CSV导出] 标签列表为空，跳过导出");
                return false;
            }

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(MakeRow("#不用改", "#服务器:", progId ?? "", "", ""));
                sb.AppendLine(MakeRow("#不用改", "#导出时间:", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "", ""));
                sb.AppendLine(MakeRow("#修改C3", "#已选点位:", tags.Count.ToString(), "", ""));
                sb.AppendLine(",,,,");
                sb.AppendLine(",,,,");
                sb.AppendLine(MakeRow("序号", "ItemId", "名称", "数据类型", "描述", "UA完整地址"));

                for (int i = 0; i < tags.Count; i++)
                {
                    var tag = tags[i];
                    string itemId = tag.ItemId ?? "";
                    string name = string.IsNullOrEmpty(tag.TagKey) ? itemId : tag.TagKey;
                    string dataType = string.IsNullOrEmpty(tag.DataType) ? AppConstants.UnknownDataType : tag.DataType;
                    string description = string.IsNullOrEmpty(tag.UaNodeId) ? itemId : tag.UaNodeId;
                    string uaAddress = string.IsNullOrEmpty(tag.UaNodeId)
                        ? $"ns={namespaceIndex};s=DaTag_{tag.TagKey ?? itemId}"
                        : $"ns={namespaceIndex};s={tag.UaNodeId}";
                    sb.AppendLine(MakeRow((i + 1).ToString(), itemId, name, dataType, description, uaAddress));
                }

                File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
                log?.Append($"[CSV导出] 已导出 {tags.Count} 个标签到 {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                log?.Append($"[CSV导出] 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从 CSV 文件导入标签配置。
        /// </summary>
        public static List<TagConfig> ImportFromFile(string filePath, LogManager log)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                log.Append("[CSV导入] 文件路径为空");
                return null;
            }
            if (!File.Exists(filePath))
            {
                log.Append($"[CSV导入] 文件不存在: {filePath}");
                return null;
            }

            try
            {
                string[] lines = File.ReadAllLines(filePath, DetectEncoding(filePath));
                if (lines.Length < 7)
                {
                    log.Append($"[CSV导入] 文件行数不足（期望≥7，实际{lines.Length}）");
                    return null;
                }

                string serverProgId = ParseHeaderLine(lines[0], "#服务器:");
                string exportTime = ParseHeaderLine(lines[1], "#导出时间:");
                int expectedCount = ParseHeaderCount(lines[2]);

                if (string.IsNullOrEmpty(serverProgId))
                {
                    log.Append("[CSV导入] 第 1 行缺少 '#服务器:' 标签");
                    return null;
                }
                if (string.IsNullOrEmpty(exportTime))
                {
                    log.Append("[CSV导入] 第 2 行缺少 '#导出时间:' 标签");
                    return null;
                }

                var tags = new List<TagConfig>();
                int dataRowCount = 0;

                for (int i = 6; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("#") && !line.Contains(",")) continue;
                    if (line.StartsWith("序号") && line.Contains("ItemId")) continue;

                    string[] cells = ParseCsvLine(line);
                    if (cells.Length < 4)
                    {
                        log.Append($"[CSV导入] 第 {i + 1} 行列数不足（期望≥4，实际{cells.Length}），已跳过");
                        continue;
                    }

                    string itemId = cells[1].Trim();
                    if (string.IsNullOrEmpty(itemId))
                    {
                        log.Append($"[CSV导入] 第 {i + 1} 行 ItemId 为空，已跳过");
                        continue;
                    }

                    string name = cells[2].Trim();
                    string dataType = cells[3].Trim();
                    string description = cells.Length >= 5 ? cells[4].Trim() : "";

                    tags.Add(new TagConfig
                    {
                        ItemId = itemId,
                        TagKey = string.IsNullOrEmpty(name) ? itemId : name,
                        DataType = string.IsNullOrEmpty(dataType) ? AppConstants.UnknownDataType : dataType,
                        UaNodeId = string.IsNullOrEmpty(description) ? null : description
                    });
                    dataRowCount++;
                }

                if (expectedCount > 0 && Math.Abs(expectedCount - dataRowCount) > 1)
                {
                    log.Append($"[CSV导入] 数量不匹配：C3 声明 {expectedCount} 个，实际数据行 {dataRowCount} 个（容许±1）");
                }

                if (tags.Count == 0)
                {
                    log.Append("[CSV导入] 未解析到有效数据行");
                    return null;
                }

                log.Append($"[CSV导入] 成功导入 {tags.Count} 个标签（来源: {filePath}）");
                return tags;
            }
            catch (Exception ex)
            {
                log.Append($"[CSV导入] 失败: {ex.Message}");
                return null;
            }
        }

        private static string MakeRow(params string[] columns)
        {
            return string.Join(",", columns.Select(c => EscapeCsvField(c)));
        }

        private static string EscapeCsvField(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        private static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == ',')
                    {
                        fields.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
            }
            fields.Add(current.ToString().TrimEnd('\r'));
            return fields.ToArray();
        }

        private static string ParseHeaderLine(string line, string label)
        {
            int idx = line.IndexOf(label, StringComparison.Ordinal);
            if (idx < 0) return "";
            return line.Substring(idx + label.Length).Trim();
        }

        private static int ParseHeaderCount(string line)
        {
            string value = ParseHeaderLine(line, "#已选点位:");
            if (int.TryParse(value, out int count))
                return count;
            return 0;
        }

        private static Encoding DetectEncoding(string filePath)
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(true);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode;
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode;
            if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                return Encoding.UTF32;
            return Encoding.Default;
        }
    }
}
