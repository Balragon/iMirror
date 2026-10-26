using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MacMirrorReceiver.Networking;

internal static class AirPlayBinaryPlist
{
	public static byte[] Write(Dictionary<string, object> root)
	{
		List<PlistObject> objects = new List<PlistObject>();
		int topObject = AddObject(root, objects);
		int objectRefSize = GetIntegerSize(objects.Count);

		using MemoryStream objectStream = new MemoryStream();
		List<long> offsets = new List<long>();
		foreach (PlistObject value in objects)
		{
			offsets.Add(objectStream.Length);
			WriteObject(objectStream, value, objectRefSize);
		}

		int offsetSize = GetIntegerSize((int)objectStream.Length);
		using MemoryStream output = new MemoryStream();
		output.Write(Encoding.ASCII.GetBytes("bplist00"));
		long objectTableOffset = output.Length;
		output.Write(objectStream.ToArray());
		long offsetTableOffset = output.Length;
		foreach (long offset in offsets)
		{
			WriteSizedInteger(output, objectTableOffset + offset, offsetSize);
		}

		output.Write(new byte[6]);
		output.WriteByte((byte)offsetSize);
		output.WriteByte((byte)objectRefSize);
		WriteUInt64(output, (ulong)objects.Count);
		WriteUInt64(output, (ulong)topObject);
		WriteUInt64(output, (ulong)offsetTableOffset);
		return output.ToArray();
	}

	// Guards for sender-supplied plists: a SETUP body comes from any LAN host, so a malformed
	// object table must fail the parse (return null) rather than crash the receiver.
	private const int MaxObjectCount = 4096;
	private const int MaxNestingDepth = 32;
	private static readonly object ParsingSentinel = new object();

	/// <summary>
	/// Parses a binary plist. Returns null when the data is not a well-formed plist (bad header,
	/// truncated tables, invalid sizes, object-reference cycles, or nesting deeper than
	/// <see cref="MaxNestingDepth"/>). Never throws on malformed input.
	/// </summary>
	public static object? Read(byte[] data)
	{
		if (data == null || data.Length < 40 || Encoding.ASCII.GetString(data, 0, 8) != "bplist00")
		{
			return null;
		}

		try
		{
			return ReadCore(data);
		}
		catch (Exception ex) when (ex is InvalidDataException
			or IndexOutOfRangeException
			or ArgumentException
			or ArithmeticException)
		{
			return null;
		}
	}

	private static object? ReadCore(byte[] data)
	{
		int trailer = data.Length - 32;
		int offsetSize = data[trailer + 6];
		int objectRefSize = data[trailer + 7];
		ulong objectCount = ReadUInt64(data, trailer + 8);
		ulong topObject = ReadUInt64(data, trailer + 16);
		ulong offsetTableOffset = ReadUInt64(data, trailer + 24);
		if (!IsValidIntegerSize(offsetSize) || !IsValidIntegerSize(objectRefSize))
		{
			throw new InvalidDataException("Invalid binary plist integer size.");
		}
		if (objectCount == 0 || objectCount > MaxObjectCount || topObject >= objectCount)
		{
			throw new InvalidDataException("Invalid binary plist object count.");
		}
		if (offsetTableOffset < 8 || offsetTableOffset > (ulong)trailer ||
			(ulong)trailer - offsetTableOffset < objectCount * (ulong)offsetSize)
		{
			throw new InvalidDataException("Invalid binary plist offset table.");
		}

		long[] offsets = new long[objectCount];
		for (int i = 0; i < offsets.Length; i++)
		{
			long offset = ReadSizedInteger(data, checked((int)offsetTableOffset + (i * offsetSize)), offsetSize);
			if (offset < 8 || offset >= trailer)
			{
				throw new InvalidDataException("Binary plist object offset is out of range.");
			}
			offsets[i] = offset;
		}

		Dictionary<int, object?> cache = new Dictionary<int, object?>();
		return ReadObject((int)topObject, data, offsets, objectRefSize, cache, 0);
	}

	private static bool IsValidIntegerSize(int size) => size is 1 or 2 or 4 or 8;

	private static int AddObject(object value, List<PlistObject> objects)
	{
		switch (value)
		{
			case string text:
				objects.Add(new PlistString(text));
				return objects.Count - 1;
			case bool flag:
				objects.Add(new PlistBool(flag));
				return objects.Count - 1;
			case byte[] data:
				objects.Add(new PlistData(data));
				return objects.Count - 1;
			case double number:
				objects.Add(new PlistReal(number));
				return objects.Count - 1;
			case int number:
				objects.Add(new PlistInteger(number));
				return objects.Count - 1;
			case uint number:
				objects.Add(new PlistInteger(number));
				return objects.Count - 1;
			case long number:
				objects.Add(new PlistInteger(number));
				return objects.Count - 1;
			case ulong number:
				objects.Add(new PlistInteger(checked((long)number)));
				return objects.Count - 1;
			case Dictionary<string, object> dictionary:
			{
				List<int> keyRefs = new List<int>();
				List<int> valueRefs = new List<int>();
				foreach (KeyValuePair<string, object> item in dictionary.OrderBy(item => item.Key, StringComparer.Ordinal))
				{
					keyRefs.Add(AddObject(item.Key, objects));
					valueRefs.Add(AddObject(item.Value, objects));
				}
				objects.Add(new PlistDictionary(keyRefs, valueRefs));
				return objects.Count - 1;
			}
			case IEnumerable<object> list:
			{
				List<int> itemRefs = list.Select(item => AddObject(item, objects)).ToList();
				objects.Add(new PlistArray(itemRefs));
				return objects.Count - 1;
			}
			default:
				throw new InvalidOperationException("Unsupported binary plist value: " + value.GetType().Name);
		}
	}

	private static object? ReadObject(int index, byte[] data, long[] offsets, int objectRefSize, Dictionary<int, object?> cache, int depth)
	{
		if (index < 0 || index >= offsets.Length)
		{
			throw new InvalidDataException("Binary plist object reference is out of range.");
		}
		if (depth > MaxNestingDepth)
		{
			throw new InvalidDataException("Binary plist nesting is too deep.");
		}
		if (cache.TryGetValue(index, out object? cached))
		{
			if (ReferenceEquals(cached, ParsingSentinel))
			{
				// A container that (directly or indirectly) references itself would otherwise
				// recurse without bound and take the process down with a StackOverflowException.
				throw new InvalidDataException("Binary plist contains an object reference cycle.");
			}
			return cached;
		}

		int offset = checked((int)offsets[index]);
		byte marker = data[offset];
		int kind = marker & 0xF0;
		int info = marker & 0x0F;

		// Mark the object as in progress before descending so cycles are detected.
		cache[index] = ParsingSentinel;
		object? value = kind switch
		{
			0x00 => marker == 0x09,
			0x10 => ReadSizedInteger(data, offset + 1, 1 << info),
			0x20 => ReadReal(data, offset + 1, 1 << info),
			0x40 => ReadData(data, offset, info),
			0x50 => ReadAsciiString(data, offset, info),
			0x60 => ReadUtf16String(data, offset, info),
			0xA0 => ReadArray(data, offsets, objectRefSize, cache, offset, info, depth + 1),
			0xD0 => ReadDictionary(data, offsets, objectRefSize, cache, offset, info, depth + 1),
			_ => null
		};
		cache[index] = value;
		return value;
	}

	private static byte[] ReadData(byte[] data, int offset, int info)
	{
		(int count, int start) = ReadCount(data, offset, info);
		return data.AsSpan(start, count).ToArray();
	}

	private static string ReadAsciiString(byte[] data, int offset, int info)
	{
		(int count, int start) = ReadCount(data, offset, info);
		return Encoding.ASCII.GetString(data, start, count);
	}

	private static string ReadUtf16String(byte[] data, int offset, int info)
	{
		(int count, int start) = ReadCount(data, offset, info);
		byte[] bytes = data.AsSpan(start, count * 2).ToArray();
		return Encoding.BigEndianUnicode.GetString(bytes);
	}

	private static List<object?> ReadArray(byte[] data, long[] offsets, int objectRefSize, Dictionary<int, object?> cache, int offset, int info, int depth)
	{
		(int count, int start) = ReadCount(data, offset, info);
		List<object?> values = new List<object?>();
		for (int i = 0; i < count; i++)
		{
			int childIndex = (int)ReadSizedInteger(data, start + (i * objectRefSize), objectRefSize);
			values.Add(ReadObject(childIndex, data, offsets, objectRefSize, cache, depth));
		}
		return values;
	}

	private static Dictionary<string, object?> ReadDictionary(byte[] data, long[] offsets, int objectRefSize, Dictionary<int, object?> cache, int offset, int info, int depth)
	{
		(int count, int start) = ReadCount(data, offset, info);
		Dictionary<string, object?> values = new Dictionary<string, object?>(StringComparer.Ordinal);
		for (int i = 0; i < count; i++)
		{
			int keyIndex = (int)ReadSizedInteger(data, start + (i * objectRefSize), objectRefSize);
			int valueIndex = (int)ReadSizedInteger(data, start + ((count + i) * objectRefSize), objectRefSize);
			string? key = ReadObject(keyIndex, data, offsets, objectRefSize, cache, depth) as string;
			if (key != null)
			{
				values[key] = ReadObject(valueIndex, data, offsets, objectRefSize, cache, depth);
			}
		}
		return values;
	}

	private static (int Count, int Start) ReadCount(byte[] data, int offset, int info)
	{
		if (info < 15)
		{
			return (info, offset + 1);
		}

		byte marker = data[offset + 1];
		if ((marker & 0xF0) != 0x10)
		{
			throw new InvalidDataException("Invalid binary plist count marker.");
		}
		int lengthBytes = 1 << (marker & 0x0F);
		if (lengthBytes > 8)
		{
			throw new InvalidDataException("Invalid binary plist count size.");
		}
		long count = ReadSizedInteger(data, offset + 2, lengthBytes);
		if (count < 0 || count > data.Length)
		{
			throw new InvalidDataException("Binary plist count exceeds the data length.");
		}
		return ((int)count, offset + 2 + lengthBytes);
	}

	private static void WriteObject(Stream stream, PlistObject value, int objectRefSize)
	{
		switch (value)
		{
			case PlistBool flag:
				stream.WriteByte((byte)(flag.Value ? 0x09 : 0x08));
				break;
			case PlistData data:
				WriteCountMarker(stream, 0x40, data.Value.Length);
				stream.Write(data.Value);
				break;
			case PlistString text:
				byte[] bytes = Encoding.ASCII.GetBytes(text.Value);
				WriteCountMarker(stream, 0x50, bytes.Length);
				stream.Write(bytes);
				break;
			case PlistReal number:
			{
				byte[] realBytes = BitConverter.GetBytes(number.Value);
				if (BitConverter.IsLittleEndian)
				{
					Array.Reverse(realBytes);
				}
				stream.WriteByte(0x23);
				stream.Write(realBytes);
				break;
			}
			case PlistInteger number:
				int size = GetIntegerSize(number.Value);
				stream.WriteByte((byte)(0x10 | Log2(size)));
				WriteSizedInteger(stream, number.Value, size);
				break;
			case PlistArray array:
				WriteCountMarker(stream, 0xA0, array.ItemRefs.Count);
				foreach (int itemRef in array.ItemRefs)
				{
					WriteSizedInteger(stream, itemRef, objectRefSize);
				}
				break;
			case PlistDictionary dictionary:
				WriteCountMarker(stream, 0xD0, dictionary.KeyRefs.Count);
				foreach (int keyRef in dictionary.KeyRefs)
				{
					WriteSizedInteger(stream, keyRef, objectRefSize);
				}
				foreach (int valueRef in dictionary.ValueRefs)
				{
					WriteSizedInteger(stream, valueRef, objectRefSize);
				}
				break;
		}
	}

	private static void WriteCountMarker(Stream stream, int marker, int count)
	{
		if (count < 15)
		{
			stream.WriteByte((byte)(marker | count));
			return;
		}

		stream.WriteByte((byte)(marker | 0x0F));
		int size = GetIntegerSize(count);
		stream.WriteByte((byte)(0x10 | Log2(size)));
		WriteSizedInteger(stream, count, size);
	}

	private static double ReadReal(byte[] data, int offset, int size)
	{
		if (size == 4)
		{
			byte[] bytes = data.AsSpan(offset, size).ToArray();
			if (BitConverter.IsLittleEndian)
			{
				Array.Reverse(bytes);
			}
			return BitConverter.ToSingle(bytes, 0);
		}

		if (size == 8)
		{
			byte[] bytes = data.AsSpan(offset, size).ToArray();
			if (BitConverter.IsLittleEndian)
			{
				Array.Reverse(bytes);
			}
			return BitConverter.ToDouble(bytes, 0);
		}

		return 0;
	}

	private static int GetIntegerSize(long value)
	{
		if (value <= byte.MaxValue)
		{
			return 1;
		}
		if (value <= ushort.MaxValue)
		{
			return 2;
		}
		if (value <= uint.MaxValue)
		{
			return 4;
		}
		return 8;
	}

	private static int Log2(int value) => value switch
	{
		1 => 0,
		2 => 1,
		4 => 2,
		8 => 3,
		_ => throw new InvalidOperationException("Invalid integer size.")
	};

	private static long ReadSizedInteger(byte[] data, int offset, int size)
	{
		if (offset < 0 || size < 0 || size > data.Length - offset)
		{
			throw new InvalidDataException("Binary plist integer is truncated.");
		}
		long value = 0;
		for (int i = 0; i < size; i++)
		{
			value = (value << 8) | data[offset + i];
		}
		return value;
	}

	private static ulong ReadUInt64(byte[] data, int offset)
	{
		ulong value = 0;
		for (int i = 0; i < 8; i++)
		{
			value = (value << 8) | data[offset + i];
		}
		return value;
	}

	private static void WriteSizedInteger(Stream stream, long value, int size)
	{
		for (int i = size - 1; i >= 0; i--)
		{
			stream.WriteByte((byte)(value >> (i * 8)));
		}
	}

	private static void WriteUInt64(Stream stream, ulong value)
	{
		for (int i = 7; i >= 0; i--)
		{
			stream.WriteByte((byte)(value >> (i * 8)));
		}
	}

	private abstract record PlistObject;

	private sealed record PlistBool(bool Value) : PlistObject;

	private sealed record PlistData(byte[] Value) : PlistObject;

	private sealed record PlistString(string Value) : PlistObject;

	private sealed record PlistReal(double Value) : PlistObject;

	private sealed record PlistInteger(long Value) : PlistObject;

	private sealed record PlistArray(List<int> ItemRefs) : PlistObject;

	private sealed record PlistDictionary(List<int> KeyRefs, List<int> ValueRefs) : PlistObject;
}
