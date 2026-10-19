using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CpcMultiplayer;

internal static class Lua
{
	public static void Write(StringBuilder sb, object v)
	{
		if (v == null)
		{
			sb.Append("nil");
		}
		else if (v is bool)
		{
			sb.Append(((bool)v) ? "true" : "false");
		}
		else if (v is int || v is long)
		{
			sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture));
		}
		else if (v is double || v is decimal || v is float)
		{
			sb.Append(Convert.ToDouble(v).ToString("R", CultureInfo.InvariantCulture));
		}
		else if (v is string s)
		{
			// Printable ASCII goes through as-is; quotes and backslashes are escaped, every other
			// byte (control chars, UTF-8 continuation bytes) becomes a Lua decimal escape \ddd.
			sb.Append('"');
			foreach (byte b in Encoding.UTF8.GetBytes(s))
			{
				if (b == '"' || b == '\\')
				{
					sb.Append('\\').Append((char)b);
				}
				else if (b >= 32 && b <= 126)
				{
					sb.Append((char)b);
				}
				else
				{
					sb.Append('\\').Append(((int)b).ToString("000"));
				}
			}
			sb.Append('"');
		}
		else if (v is IDictionary<string, object> dictionary)
		{
			sb.Append('{');
			foreach (KeyValuePair<string, object> item in dictionary)
			{
				sb.Append('[');
				Write(sb, item.Key);
				sb.Append("]=");
				Write(sb, item.Value);
				sb.Append(',');
			}
			sb.Append('}');
		}
		else if (v is IEnumerable enumerable)
		{
			sb.Append('{');
			foreach (object item2 in enumerable)
			{
				Write(sb, item2);
				sb.Append(',');
			}
			sb.Append('}');
		}
		else
		{
			Write(sb, v.ToString());
		}
	}
}
