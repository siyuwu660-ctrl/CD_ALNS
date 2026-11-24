// 轻量 CSV 读取器使用逐字符状态机识别引号包围区域和字段分隔逗号，将表头映射为字段字典，供不同数据表复用统一解析规则。
// 该组件只负责语法拆分，业务类型转换与校验留给数据装载器处理，从而保持读取逻辑与领域规则分离。
namespace GurobiTimetablingResearch.IO;

/// <summary>
/// 面向标准化小规模数据表的内存 CSV 读取器。
/// 解析器通过引号状态区分字段分隔符与带引号字段内部的逗号，再按表头生成只读字段字典。
/// </summary>
public sealed class SimpleCsvReader
{
    public IReadOnlyList<IReadOnlyDictionary<string, string>> ReadRows(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("CSV file was not found.", filePath);
        }

        string[] lines = File.ReadAllLines(filePath);
        if (lines.Length == 0)
        {
            return Array.Empty<IReadOnlyDictionary<string, string>>();
        }

        string[] header = ParseLine(lines[0]).ToArray();
        List<IReadOnlyDictionary<string, string>> rows = new(lines.Length - 1);

        for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                continue;
            }

            string[] values = ParseLine(lines[lineIndex]).ToArray();
            Dictionary<string, string> row = new(StringComparer.OrdinalIgnoreCase);

            for (int columnIndex = 0; columnIndex < header.Length; columnIndex++)
            {
                string key = header[columnIndex];
                string value = columnIndex < values.Length ? values[columnIndex] : string.Empty;
                row[key] = value;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static IEnumerable<string> ParseLine(string line)
    {
        List<string> fields = new();
        char[] buffer = line.ToCharArray();
        bool insideQuotes = false;
        int fieldStart = 0;

        for (int index = 0; index < buffer.Length; index++)
        {
            char current = buffer[index];

            if (current == '"')
            {
                insideQuotes = !insideQuotes;
                continue;
            }

            if (current == ',' && !insideQuotes)
            {
                fields.Add(CleanField(line[fieldStart..index]));
                fieldStart = index + 1;
            }
        }

        fields.Add(CleanField(line[fieldStart..]));
        return fields;
    }

    private static string CleanField(string rawField)
    {
        return rawField.Trim().Trim('"').Trim();
    }
}
