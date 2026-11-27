using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentProxyHub.Core.Apps;

/// <summary>
/// 词法感知 JSONC（带 //、/* */ 注释与尾随逗号）原位补丁器：
/// - 仅改写目标属性对应的值或增删目标行，100% 保留原有注释、空行与排版格式
/// - 每次修补后均执行 JsonNode.Parse 结构校验，杜绝写入损坏配置文件
/// </summary>
public static class JsoncPatcher
{
    private static readonly JsonDocumentOptions ValidationOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class PropSpan
    {
        public required string Key { get; init; }
        public required int PropertyStart { get; init; }
        public required int ValueStart { get; init; }
        public required int ValueEnd { get; init; }
        public int TrailingCommaIdx { get; set; } = -1;
    }

    private sealed class ObjSpan
    {
        public required int OpenBraceIdx { get; init; }
        public required int CloseBraceIdx { get; init; }
        public required List<PropSpan> Properties { get; init; }
    }

    public static bool IsEmptyJsonObject(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return true;
        try
        {
            var node = JsonNode.Parse(rawText, null, ValidationOpts) as JsonObject;
            return node is { Count: 0 } && !ContainsAnyComment(rawText);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryPatchObjectInPlace(
        string rawText,
        IReadOnlyList<string> nestedObjectPath,
        IReadOnlyDictionary<string, JsonNode?> updates,
        out string result)
    {
        result = string.IsNullOrWhiteSpace(rawText) ? "{\n}" : rawText;
        try
        {
            // 先用宽松 JSONC 解析校验输入确实为 JSON 对象
            if (JsonNode.Parse(result, null, ValidationOpts) is not JsonObject)
                return false;

            string current = result;
            foreach (var kv in updates)
            {
                string rawValueJson = kv.Value == null ? "null" : kv.Value.ToJsonString();
                if (!TrySetPropertyAtPath(ref current, nestedObjectPath, kv.Key, rawValueJson))
                    return false;
            }

            if (JsonNode.Parse(current, null, ValidationOpts) is not JsonObject)
                return false;

            result = current;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryRestorePropertiesInPlace(
        string rawText,
        IReadOnlyList<string> nestedObjectPath,
        IReadOnlyDictionary<string, AppProxyTakeoverService.BackupEntry> entries,
        out string result)
    {
        result = rawText;
        try
        {
            if (JsonNode.Parse(result, null, ValidationOpts) is not JsonObject)
                return false;

            string current = result;
            foreach (var kv in entries)
            {
                if (kv.Value.Existed)
                {
                    string rawVal = string.IsNullOrWhiteSpace(kv.Value.RawJson) ? "null" : kv.Value.RawJson!;
                    if (!TrySetPropertyAtPath(ref current, nestedObjectPath, kv.Key, rawVal))
                        return false;
                }
                else
                {
                    if (!TryRemovePropertyAtPath(ref current, nestedObjectPath, kv.Key))
                        return false;
                }
            }

            if (JsonNode.Parse(current, null, ValidationOpts) is not JsonObject)
                return false;

            result = current;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetPropertyAtPath(
        ref string text,
        IReadOnlyList<string> nestedPath,
        string key,
        string rawValueJson)
    {
        // 逐层确保路径上的嵌套对象存在
        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        for (int depth = 0; depth < nestedPath.Count; depth++)
        {
            var objAtDepth = LocateObjectAtPath(text, nestedPath.Take(depth).ToArray());
            if (objAtDepth == null) return false;

            string seg = nestedPath[depth];
            var existingSeg = objAtDepth.Properties.FirstOrDefault(p => p.Key == seg);
            if (existingSeg == null)
            {
                text = InsertPropertyIntoObject(text, objAtDepth, seg, "{}", isEmptyNestedObj: true);
            }
        }

        var targetObj = LocateObjectAtPath(text, nestedPath);
        if (targetObj == null) return false;

        var existing = targetObj.Properties.FirstOrDefault(p => p.Key == key);
        if (existing != null)
        {
            text = string.Concat(text.AsSpan(0, existing.ValueStart), rawValueJson, text.AsSpan(existing.ValueEnd));
            return true;
        }

        text = InsertPropertyIntoObject(text, targetObj, key, rawValueJson, isEmptyNestedObj: false);
        return true;
    }

    private static bool TryRemovePropertyAtPath(
        ref string text,
        IReadOnlyList<string> nestedPath,
        string key)
    {
        var targetObj = LocateObjectAtPath(text, nestedPath);
        if (targetObj == null) return true; // 父路径不存在则无需移除

        int idx = targetObj.Properties.FindIndex(p => p.Key == key);
        if (idx < 0) return true;

        var props = targetObj.Properties;
        var target = props[idx];
        bool isLastProp = idx == props.Count - 1;
        bool hasPrev = idx > 0;

        // 确定本次删除的范围：从属性开头到值末尾（或尾逗号之后）
        int delEnd = target.TrailingCommaIdx >= 0 ? target.TrailingCommaIdx + 1 : target.ValueEnd;
        int delStart = target.PropertyStart;

        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // 如果是最后一项且没有尾逗号，但前一项有尾逗号，先移除前一项尾逗号再移除本项，避免产生无效尾逗号
        if (isLastProp && target.TrailingCommaIdx < 0 && hasPrev && props[idx - 1].TrailingCommaIdx >= 0)
        {
            int prevComma = props[idx - 1].TrailingCommaIdx;
            text = RemoveSpanPreservingInlineComment(text, delStart, delEnd, nl);
            text = string.Concat(text.AsSpan(0, prevComma), text.AsSpan(prevComma + 1));
            return true;
        }

        text = RemoveSpanPreservingInlineComment(text, delStart, delEnd, nl);
        return true;
    }

    private static string RemoveSpanPreservingInlineComment(string text, int delStart, int delEnd, string nl)
    {
        // 判断删除该属性后，所在整行是否除了缩进和换行外没有其他有效内容（包括行尾注释）
        int lineStart = text.LastIndexOf('\n', Math.Max(0, delStart - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        bool onlySpacesBefore = string.IsNullOrWhiteSpace(text[lineStart..delStart]);

        int lineEnd = text.IndexOf('\n', delEnd);
        int contentAfterEnd = lineEnd < 0 ? text.Length : lineEnd;
        string afterOnSameLine = text[delEnd..contentAfterEnd].TrimEnd('\r');
        bool onlySpacesAfter = string.IsNullOrWhiteSpace(afterOnSameLine);

        if (onlySpacesBefore && onlySpacesAfter)
        {
            int fullLineEnd = lineEnd >= 0 ? lineEnd + 1 : text.Length;
            return string.Concat(text.AsSpan(0, lineStart), text.AsSpan(fullLineEnd));
        }

        // 同行还有其它内容（例如行尾注释或同一行的另一个属性），只删除属性本身区间
        int trimWsAfter = delEnd;
        while (trimWsAfter < text.Length && text[trimWsAfter] == ' ')
        {
            // 若紧跟的是另一成员而非注释或右括号，顺便去掉一个前导空格；否则保留与注释间的空格
            if (trimWsAfter + 1 < text.Length && text[trimWsAfter + 1] == '/')
                break;
            trimWsAfter++;
        }
        return string.Concat(text.AsSpan(0, delStart), text.AsSpan(trimWsAfter));
    }

    private static string InsertPropertyIntoObject(
        string text,
        ObjSpan obj,
        string key,
        string rawValueJson,
        bool isEmptyNestedObj)
    {
        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string escapedKey = JsonEncodedText.Encode(key).ToString();
        string actualVal = isEmptyNestedObj ? "{}" : rawValueJson;
        string memberIndent = GetChildIndent(text, obj);

        int closeLineStart = GetLineStart(text, obj.CloseBraceIdx);
        bool closeOnOwnLine = closeLineStart > obj.OpenBraceIdx && string.IsNullOrWhiteSpace(text[closeLineStart..obj.CloseBraceIdx]);
        string closeIndent = closeOnOwnLine ? text[closeLineStart..obj.CloseBraceIdx] : InferObjectCloseIndent(text, obj.OpenBraceIdx);

        if (obj.Properties.Count == 0)
        {
            string entryText = $"{memberIndent}\"{escapedKey}\": {actualVal}";
            if (closeOnOwnLine)
            {
                return string.Concat(text.AsSpan(0, closeLineStart), $"{entryText}{nl}", text.AsSpan(closeLineStart));
            }
            string between = text[(obj.OpenBraceIdx + 1)..obj.CloseBraceIdx];
            string formattedBlock = $"{nl}{entryText}{nl}{closeIndent}";
            if (string.IsNullOrWhiteSpace(between))
            {
                return string.Concat(text.AsSpan(0, obj.OpenBraceIdx + 1), formattedBlock, text.AsSpan(obj.CloseBraceIdx));
            }
            return string.Concat(text.AsSpan(0, obj.CloseBraceIdx), formattedBlock, text.AsSpan(obj.CloseBraceIdx));
        }

        var last = obj.Properties[^1];
        string prefixUpdated = text;
        int insertAnchorOffset = 0;

        if (last.TrailingCommaIdx < 0)
        {
            prefixUpdated = string.Concat(text.AsSpan(0, last.ValueEnd), ",", text.AsSpan(last.ValueEnd));
            insertAnchorOffset = 1;
        }

        string propLine = $"{memberIndent}\"{escapedKey}\": {actualVal}";
        if (closeOnOwnLine)
        {
            int adjCloseLineStart = closeLineStart + insertAnchorOffset;
            return string.Concat(prefixUpdated.AsSpan(0, adjCloseLineStart), $"{propLine}{nl}", prefixUpdated.AsSpan(adjCloseLineStart));
        }

        int adjCloseIdx = obj.CloseBraceIdx + insertAnchorOffset;
        bool openSameLineAsClose = GetLineStart(text, obj.OpenBraceIdx) == closeLineStart;
        if (openSameLineAsClose)
        {
            return string.Concat(prefixUpdated.AsSpan(0, adjCloseIdx), $" \"{escapedKey}\": {actualVal}", prefixUpdated.AsSpan(adjCloseIdx));
        }

        return string.Concat(prefixUpdated.AsSpan(0, adjCloseIdx), $"{nl}{propLine}{nl}{closeIndent}", prefixUpdated.AsSpan(adjCloseIdx));
    }

    private static string GetChildIndent(string text, ObjSpan obj)
    {
        if (obj.Properties.Count > 0)
        {
            int firstStart = obj.Properties[^1].PropertyStart;
            int ls = GetLineStart(text, firstStart);
            string leading = text[ls..firstStart];
            if (leading.All(c => c is ' ' or '\t'))
                return leading;
        }
        return InferObjectCloseIndent(text, obj.OpenBraceIdx) + "  ";
    }

    private static string InferObjectCloseIndent(string text, int openBraceIdx)
    {
        int ls = GetLineStart(text, openBraceIdx);
        var sb = new StringBuilder();
        for (int i = ls; i < text.Length && text[i] is ' ' or '\t'; i++)
            sb.Append(text[i]);
        return sb.ToString();
    }

    private static int GetLineStart(string text, int pos)
    {
        if (pos <= 0) return 0;
        int idx = text.LastIndexOf('\n', Math.Min(pos - 1, text.Length - 1));
        return idx < 0 ? 0 : idx + 1;
    }

    private static ObjSpan? LocateObjectAtPath(string text, IReadOnlyList<string> path)
    {
        int pos = SkipTrivia(text, 0);
        if (pos >= text.Length || text[pos] != '{') return null;

        ObjSpan? current = ParseObjectSpan(text, pos);
        if (current == null) return null;

        foreach (string seg in path)
        {
            var prop = current.Properties.FirstOrDefault(p => p.Key == seg);
            if (prop == null) return null;
            if (prop.ValueStart >= text.Length || text[prop.ValueStart] != '{') return null;
            current = ParseObjectSpan(text, prop.ValueStart);
            if (current == null) return null;
        }

        return current;
    }

    private static ObjSpan? ParseObjectSpan(string text, int openBraceIdx)
    {
        if (openBraceIdx >= text.Length || text[openBraceIdx] != '{') return null;
        var props = new List<PropSpan>();
        int i = SkipTrivia(text, openBraceIdx + 1);

        while (i < text.Length)
        {
            if (text[i] == '}')
            {
                return new ObjSpan
                {
                    OpenBraceIdx = openBraceIdx,
                    CloseBraceIdx = i,
                    Properties = props
                };
            }

            if (text[i] == ',')
            {
                i = SkipTrivia(text, i + 1);
                continue;
            }

            if (text[i] != '"') return null;
            int keyStart = i;
            if (!TryReadJsonString(text, ref i, out string key)) return null;

            i = SkipTrivia(text, i);
            if (i >= text.Length || text[i] != ':') return null;
            i = SkipTrivia(text, i + 1);
            if (i >= text.Length) return null;

            int valStart = i;
            if (!TrySkipJsonValue(text, ref i)) return null;
            int valEnd = i;

            int afterVal = SkipTrivia(text, i);
            int commaIdx = -1;
            if (afterVal < text.Length && text[afterVal] == ',')
            {
                commaIdx = afterVal;
                i = SkipTrivia(text, afterVal + 1);
            }
            else
            {
                i = afterVal;
            }

            props.Add(new PropSpan
            {
                Key = key,
                PropertyStart = keyStart,
                ValueStart = valStart,
                ValueEnd = valEnd,
                TrailingCommaIdx = commaIdx
            });
        }

        return null;
    }

    private static bool TrySkipJsonValue(string text, ref int pos)
    {
        if (pos >= text.Length) return false;
        char c = text[pos];
        if (c == '"')
            return TryReadJsonString(text, ref pos, out _);

        if (c == '{' || c == '[')
        {
            char closeChar = c == '{' ? '}' : ']';
            int depth = 1;
            pos++;
            while (pos < text.Length && depth > 0)
            {
                pos = SkipTrivia(text, pos);
                if (pos >= text.Length) return false;
                char ch = text[pos];
                if (ch == '"')
                {
                    if (!TryReadJsonString(text, ref pos, out _)) return false;
                    continue;
                }
                if (ch == '{' || ch == '[') depth++;
                else if (ch == '}' || ch == ']') depth--;
                pos++;
            }
            return depth == 0;
        }

        // number / true / false / null
        int start = pos;
        while (pos < text.Length)
        {
            char ch = text[pos];
            if (char.IsWhiteSpace(ch) || ch is ',' or '}' or ']' or '/')
                break;
            pos++;
        }
        return pos > start;
    }

    private static bool TryReadJsonString(string text, ref int pos, out string unescaped)
    {
        unescaped = string.Empty;
        if (pos >= text.Length || text[pos] != '"') return false;
        int start = pos;
        pos++;
        while (pos < text.Length)
        {
            char c = text[pos++];
            if (c == '\\')
            {
                if (pos >= text.Length) return false;
                pos++;
                continue;
            }
            if (c == '"')
            {
                try
                {
                    unescaped = JsonSerializer.Deserialize<string>(text[start..pos]) ?? string.Empty;
                }
                catch
                {
                    unescaped = text[(start + 1)..(pos - 1)];
                }
                return true;
            }
        }
        return false;
    }

    private static int SkipTrivia(string text, int pos)
    {
        while (pos < text.Length)
        {
            char c = text[pos];
            if (char.IsWhiteSpace(c))
            {
                pos++;
                continue;
            }
            if (c == '/' && pos + 1 < text.Length)
            {
                if (text[pos + 1] == '/')
                {
                    pos += 2;
                    while (pos < text.Length && text[pos] != '\n') pos++;
                    continue;
                }
                if (text[pos + 1] == '*')
                {
                    pos += 2;
                    while (pos + 1 < text.Length && !(text[pos] == '*' && text[pos + 1] == '/')) pos++;
                    pos = Math.Min(text.Length, pos + 2);
                    continue;
                }
            }
            break;
        }
        return pos;
    }

    private static bool ContainsAnyComment(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                if (!TryReadJsonString(text, ref i, out _)) break;
                continue;
            }
            if (text[i] == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
                return true;
            i++;
        }
        return false;
    }
}
