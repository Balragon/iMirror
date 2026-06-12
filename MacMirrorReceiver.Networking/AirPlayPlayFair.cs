using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace MacMirrorReceiver.Networking;

internal static class AirPlayPlayFair
{
	private static readonly Lazy<Tables> LazyTables = new Lazy<Tables>(LoadTables, LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly int[] Md5Shifts =
	{
		7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
		5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
		4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
		6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21
	};

	public static byte[] DecryptAesKey(byte[] keyMessage, byte[] encryptedKey)
	{
		if (keyMessage.Length < 144)
		{
			throw new InvalidOperationException("FairPlay key message is missing.");
		}
		if (encryptedKey.Length < 72)
		{
			throw new InvalidOperationException("FairPlay encrypted key is too short.");
		}

		Tables tables = LazyTables.Value;
		byte[] sessionKey = GenerateSessionKey(tables, tables.DefaultSap, keyMessage);
		uint[,] keySchedule = GenerateKeySchedule(tables, sessionKey);
		byte[] block = new byte[16];
		for (int i = 0; i < block.Length; i++)
		{
			block[i] = (byte)(encryptedKey[56 + i] ^ tables.ZKey[i]);
		}

		Cycle(tables, block, keySchedule);
		byte[] keyOut = new byte[16];
		for (int i = 0; i < keyOut.Length; i++)
		{
			keyOut[i] = (byte)(block[i] ^ encryptedKey[16 + i] ^ tables.XKey[i] ^ tables.ZKey[i]);
		}

		return keyOut;
	}

	private static byte[] GenerateSessionKey(Tables tables, byte[] oldSap, byte[] messageIn)
	{
		byte[] decryptedMessage = DecryptMessage(tables, messageIn);
		byte[] newSap = new byte[320];
		Buffer.BlockCopy(tables.StaticSource1, 0, newSap, 0, 0x11);
		Buffer.BlockCopy(decryptedMessage, 0, newSap, 0x11, 0x80);
		Buffer.BlockCopy(oldSap, 0x80, newSap, 0x91, 0x80);
		Buffer.BlockCopy(tables.StaticSource2, 0, newSap, 0x111, 0x2f);

		byte[] sessionKey = CopyOf(tables.InitialSessionKey);
		for (int round = 0; round < 5; round++)
		{
			byte[] block = new byte[64];
			Buffer.BlockCopy(newSap, round * 64, block, 0, block.Length);
			byte[] md5 = ModifiedMd5(block, sessionKey);
			sessionKey = SapHash(tables, block);
			for (int i = 0; i < 4; i++)
			{
				uint value = ReadUInt32(sessionKey, i * 4) + ReadUInt32(md5, i * 4);
				WriteUInt32(sessionKey, i * 4, value);
			}
		}

		for (int i = 0; i < 16; i += 4)
		{
			Swap(sessionKey, i, i + 3);
			Swap(sessionKey, i + 1, i + 2);
		}
		for (int i = 0; i < sessionKey.Length; i++)
		{
			sessionKey[i] ^= 121;
		}

		return sessionKey;
	}

	private static byte[] DecryptMessage(Tables tables, byte[] messageIn)
	{
		byte[] decryptedMessage = new byte[128];
		byte[] buffer = new byte[16];
		int mode = messageIn[12];
		if (mode < 0 || mode >= 4)
		{
			throw new InvalidOperationException("Unsupported FairPlay mode.");
		}

		for (int i = 0; i < 8; i++)
		{
			int sourceOffset = mode == 3 ? 0x80 - 0x10 * i : 0x10 * (i + 1);
			Buffer.BlockCopy(messageIn, sourceOffset, buffer, 0, buffer.Length);

			for (int j = 0; j < 9; j++)
			{
				int baseIndex = 0x80 - 0x10 * j;
				buffer[0x0] = (byte)(MessageTable(tables, baseIndex + 0x0, buffer[0x0]) ^ tables.MessageKey[mode, baseIndex + 0x0]);
				buffer[0x4] = (byte)(MessageTable(tables, baseIndex + 0x4, buffer[0x4]) ^ tables.MessageKey[mode, baseIndex + 0x4]);
				buffer[0x8] = (byte)(MessageTable(tables, baseIndex + 0x8, buffer[0x8]) ^ tables.MessageKey[mode, baseIndex + 0x8]);
				buffer[0xc] = (byte)(MessageTable(tables, baseIndex + 0xc, buffer[0xc]) ^ tables.MessageKey[mode, baseIndex + 0xc]);

				byte tmp = buffer[0x0d];
				buffer[0xd] = (byte)(MessageTable(tables, baseIndex + 0xd, buffer[0x9]) ^ tables.MessageKey[mode, baseIndex + 0xd]);
				buffer[0x9] = (byte)(MessageTable(tables, baseIndex + 0x9, buffer[0x5]) ^ tables.MessageKey[mode, baseIndex + 0x9]);
				buffer[0x5] = (byte)(MessageTable(tables, baseIndex + 0x5, buffer[0x1]) ^ tables.MessageKey[mode, baseIndex + 0x5]);
				buffer[0x1] = (byte)(MessageTable(tables, baseIndex + 0x1, tmp) ^ tables.MessageKey[mode, baseIndex + 0x1]);

				tmp = buffer[0x02];
				buffer[0x2] = (byte)(MessageTable(tables, baseIndex + 0x2, buffer[0xa]) ^ tables.MessageKey[mode, baseIndex + 0x2]);
				buffer[0xa] = (byte)(MessageTable(tables, baseIndex + 0xa, tmp) ^ tables.MessageKey[mode, baseIndex + 0xa]);
				tmp = buffer[0x06];
				buffer[0x6] = (byte)(MessageTable(tables, baseIndex + 0x6, buffer[0xe]) ^ tables.MessageKey[mode, baseIndex + 0x6]);
				buffer[0xe] = (byte)(MessageTable(tables, baseIndex + 0xe, tmp) ^ tables.MessageKey[mode, baseIndex + 0xe]);

				tmp = buffer[0x3];
				buffer[0x3] = (byte)(MessageTable(tables, baseIndex + 0x3, buffer[0x7]) ^ tables.MessageKey[mode, baseIndex + 0x3]);
				buffer[0x7] = (byte)(MessageTable(tables, baseIndex + 0x7, buffer[0xb]) ^ tables.MessageKey[mode, baseIndex + 0x7]);
				buffer[0xb] = (byte)(MessageTable(tables, baseIndex + 0xb, buffer[0xf]) ^ tables.MessageKey[mode, baseIndex + 0xb]);
				buffer[0xf] = (byte)(MessageTable(tables, baseIndex + 0xf, tmp) ^ tables.MessageKey[mode, baseIndex + 0xf]);

				uint word0 = tables.TableS9[0x000 + buffer[0x0]] ^ tables.TableS9[0x100 + buffer[0x1]] ^ tables.TableS9[0x200 + buffer[0x2]] ^ tables.TableS9[0x300 + buffer[0x3]];
				uint word1 = tables.TableS9[0x000 + buffer[0x4]] ^ tables.TableS9[0x100 + buffer[0x5]] ^ tables.TableS9[0x200 + buffer[0x6]] ^ tables.TableS9[0x300 + buffer[0x7]];
				uint word2 = tables.TableS9[0x000 + buffer[0x8]] ^ tables.TableS9[0x100 + buffer[0x9]] ^ tables.TableS9[0x200 + buffer[0xa]] ^ tables.TableS9[0x300 + buffer[0xb]];
				uint word3 = tables.TableS9[0x000 + buffer[0xc]] ^ tables.TableS9[0x100 + buffer[0xd]] ^ tables.TableS9[0x200 + buffer[0xe]] ^ tables.TableS9[0x300 + buffer[0xf]];
				WriteUInt32(buffer, 0, word0);
				WriteUInt32(buffer, 4, word1);
				WriteUInt32(buffer, 8, word2);
				WriteUInt32(buffer, 12, word3);
			}

			PermuteMessageTail(tables, buffer);
			if (mode == 2 || mode == 1 || mode == 0)
			{
				int outputOffset = 0x10 * i;
				byte[] xorSource = new byte[16];
				if (i > 0)
				{
					Buffer.BlockCopy(messageIn, 0x10 * i, xorSource, 0, xorSource.Length);
				}
				else
				{
					for (int k = 0; k < xorSource.Length; k++)
					{
						xorSource[k] = tables.MessageIv[mode, k];
					}
				}
				XorBlocks(buffer, xorSource, decryptedMessage, outputOffset);
			}
			else
			{
				int outputOffset = 0x70 - 0x10 * i;
				byte[] xorSource = new byte[16];
				if (i < 7)
				{
					Buffer.BlockCopy(messageIn, 0x70 - 0x10 * i, xorSource, 0, xorSource.Length);
				}
				else
				{
					for (int k = 0; k < xorSource.Length; k++)
					{
						xorSource[k] = tables.MessageIv[mode, k];
					}
				}
				XorBlocks(buffer, xorSource, decryptedMessage, outputOffset);
			}
		}

		return decryptedMessage;
	}

	private static void PermuteMessageTail(Tables tables, byte[] buffer)
	{
		buffer[0x0] = tables.TableS10[(0x0 << 8) + buffer[0x0]];
		buffer[0x4] = tables.TableS10[(0x4 << 8) + buffer[0x4]];
		buffer[0x8] = tables.TableS10[(0x8 << 8) + buffer[0x8]];
		buffer[0xc] = tables.TableS10[(0xc << 8) + buffer[0xc]];

		byte tmp = buffer[0x0d];
		buffer[0xd] = tables.TableS10[(0xd << 8) + buffer[0x9]];
		buffer[0x9] = tables.TableS10[(0x9 << 8) + buffer[0x5]];
		buffer[0x5] = tables.TableS10[(0x5 << 8) + buffer[0x1]];
		buffer[0x1] = tables.TableS10[(0x1 << 8) + tmp];

		tmp = buffer[0x02];
		buffer[0x2] = tables.TableS10[(0x2 << 8) + buffer[0xa]];
		buffer[0xa] = tables.TableS10[(0xa << 8) + tmp];
		tmp = buffer[0x06];
		buffer[0x6] = tables.TableS10[(0x6 << 8) + buffer[0xe]];
		buffer[0xe] = tables.TableS10[(0xe << 8) + tmp];

		tmp = buffer[0x3];
		buffer[0x3] = tables.TableS10[(0x3 << 8) + buffer[0x7]];
		buffer[0x7] = tables.TableS10[(0x7 << 8) + buffer[0xb]];
		buffer[0xb] = tables.TableS10[(0xb << 8) + buffer[0xf]];
		buffer[0xf] = tables.TableS10[(0xf << 8) + tmp];
	}

	private static byte[] SapHash(Tables tables, byte[] blockIn)
	{
		byte[] buffer0 = CopyOf(tables.SapBuffer0);
		byte[] buffer1 = new byte[210];
		byte[] buffer2 = CopyOf(tables.SapBuffer2);
		byte[] buffer3 = new byte[132];
		byte[] buffer4 = CopyOf(tables.SapBuffer4);

		for (int i = 0; i < buffer1.Length; i++)
		{
			uint word = ReadUInt32(blockIn, ((i % 64) >> 2) * 4);
			buffer1[i] = (byte)((word >> ((3 - (i % 4)) << 3)) & 0xff);
		}
		for (int i = 0; i < 840; i++)
		{
			byte x = buffer1[Index32(i - 155, 210)];
			byte y = buffer1[Index32(i - 57, 210)];
			byte z = buffer1[Index32(i - 13, 210)];
			byte w = buffer1[Index32(i, 210)];
			buffer1[i % 210] = U8((uint)Rol8(y, 5) + (uint)(Rol8(z, 3) ^ w) - Rol8(x, 7));
		}

		Garble(buffer0, buffer1, buffer2, buffer3, buffer4);

		byte[] keyOut = new byte[16];
		for (int i = 0; i < keyOut.Length; i++)
		{
			keyOut[i] = 0xe1;
		}
		for (int i = 0; i < 11; i++)
		{
			keyOut[i] = i == 3 ? (byte)0x3d : U8((uint)keyOut[i] + buffer3[tables.SapIndexMangle[i] * 4]);
		}
		for (int i = 0; i < buffer0.Length; i++)
		{
			keyOut[i % 16] ^= buffer0[i];
		}
		for (int i = 0; i < buffer2.Length; i++)
		{
			keyOut[i % 16] ^= buffer2[i];
		}
		for (int i = 0; i < buffer1.Length; i++)
		{
			keyOut[i % 16] ^= buffer1[i];
		}

		for (int j = 0; j < 16; j++)
		{
			for (int i = 0; i < 16; i++)
			{
				byte x = keyOut[Index32(i - 7, 16)];
				byte y = keyOut[i % 16];
				byte z = keyOut[Index32(i - 37, 16)];
				byte w = keyOut[Index32(i - 177, 16)];
				keyOut[i] = (byte)(Rol8(x, 1) ^ y ^ Rol8(z, 6) ^ Rol8(w, 5));
			}
		}

		return keyOut;
	}

	private static byte[] ModifiedMd5(byte[] originalBlockIn, byte[] keyIn)
	{
		byte[] blockIn = CopyOf(originalBlockIn);
		uint a = ReadUInt32(keyIn, 0);
		uint b = ReadUInt32(keyIn, 4);
		uint c = ReadUInt32(keyIn, 8);
		uint d = ReadUInt32(keyIn, 12);

		for (int i = 0; i < 64; i++)
		{
			int j = i < 16 ? i : i < 32 ? (5 * i + 1) % 16 : i < 48 ? (3 * i + 5) % 16 : (7 * i) % 16;
			uint input = ((uint)blockIn[4 * j] << 24) | ((uint)blockIn[4 * j + 1] << 16) | ((uint)blockIn[4 * j + 2] << 8) | blockIn[4 * j + 3];
			uint z = a + input + Md5Constant(i);
			z = i < 16
				? RotateLeft(z + Md5F(b, c, d), Md5Shifts[i])
				: i < 32
					? RotateLeft(z + Md5G(b, c, d), Md5Shifts[i])
					: i < 48
						? RotateLeft(z + Md5H(b, c, d), Md5Shifts[i])
						: RotateLeft(z + Md5I(b, c, d), Md5Shifts[i]);
			z += b;
			uint tmp = d;
			d = c;
			c = b;
			b = z;
			a = tmp;
			if (i == 31)
			{
				SwapWords(blockIn, (int)(a & 15), (int)(b & 15));
				SwapWords(blockIn, (int)(c & 15), (int)(d & 15));
				SwapWords(blockIn, (int)((a & (15 << 4)) >> 4), (int)((b & (15 << 4)) >> 4));
				SwapWords(blockIn, (int)((a & (15 << 8)) >> 8), (int)((b & (15 << 8)) >> 8));
				SwapWords(blockIn, (int)((a & (15 << 12)) >> 12), (int)((b & (15 << 12)) >> 12));
			}
		}

		byte[] output = new byte[16];
		WriteUInt32(output, 0, ReadUInt32(keyIn, 0) + a);
		WriteUInt32(output, 4, ReadUInt32(keyIn, 4) + b);
		WriteUInt32(output, 8, ReadUInt32(keyIn, 8) + c);
		WriteUInt32(output, 12, ReadUInt32(keyIn, 12) + d);
		return output;
	}

	private static uint[,] GenerateKeySchedule(Tables tables, byte[] keyMaterial)
	{
		uint[,] keySchedule = new uint[11, 4];
		byte[] buffer = new byte[16];
		for (int i = 0; i < buffer.Length; i++)
		{
			buffer[i] = (byte)(keyMaterial[i] ^ tables.TKey[i]);
		}

		int ti = 0;
		for (int round = 0; round < 11; round++)
		{
			keySchedule[round, 0] = ReadUInt32(buffer, 0);
			int table1 = TableIndex(ti);
			int table2 = TableIndex(ti + 1);
			int table3 = TableIndex(ti + 2);
			int table4 = TableIndex(ti + 3);
			ti += 4;
			buffer[0] = U8((uint)(buffer[0] ^ tables.TableS1[table1 + buffer[0x0d]] ^ tables.IndexMangle[round]));
			buffer[1] = U8((uint)(buffer[1] ^ tables.TableS1[table2 + buffer[0x0e]]));
			buffer[2] = U8((uint)(buffer[2] ^ tables.TableS1[table3 + buffer[0x0f]]));
			buffer[3] = U8((uint)(buffer[3] ^ tables.TableS1[table4 + buffer[0x0c]]));

			keySchedule[round, 1] = ReadUInt32(buffer, 4);
			WriteUInt32(buffer, 4, ReadUInt32(buffer, 4) ^ ReadUInt32(buffer, 0));
			keySchedule[round, 2] = ReadUInt32(buffer, 8);
			WriteUInt32(buffer, 8, ReadUInt32(buffer, 8) ^ ReadUInt32(buffer, 4));
			keySchedule[round, 3] = ReadUInt32(buffer, 12);
			WriteUInt32(buffer, 12, ReadUInt32(buffer, 12) ^ ReadUInt32(buffer, 8));
		}

		return keySchedule;
	}

	private static void Cycle(Tables tables, byte[] block, uint[,] keySchedule)
	{
		for (int i = 0; i < 4; i++)
		{
			WriteUInt32(block, i * 4, ReadUInt32(block, i * 4) ^ keySchedule[10, i]);
		}

		PermuteBlock1(tables, block);
		for (int round = 0; round < 9; round++)
		{
			int scheduleRound = 9 - round;
			byte[] key0 = WordBytes(keySchedule[scheduleRound, 0]);
			uint word0 = tables.TableS5[block[3] ^ key0[3]] ^
				tables.TableS6[block[2] ^ key0[2]] ^
				tables.TableS8[block[0] ^ key0[0]] ^
				tables.TableS7[block[1] ^ key0[1]];
			WriteUInt32(block, 0, word0);

			byte[] key1 = WordBytes(keySchedule[scheduleRound, 1]);
			uint word1 = tables.TableS6[block[6] ^ key1[2]] ^
				tables.TableS5[block[7] ^ key1[3]] ^
				tables.TableS7[block[5] ^ key1[1]] ^
				tables.TableS8[block[4] ^ key1[0]];
			WriteUInt32(block, 4, word1);

			byte[] key2 = WordBytes(keySchedule[scheduleRound, 2]);
			byte[] key3 = WordBytes(keySchedule[scheduleRound, 3]);
			WriteUInt32(block, 8,
				tables.TableS5[block[11] ^ key2[3]] ^
				tables.TableS6[block[10] ^ key2[2]] ^
				tables.TableS7[block[9] ^ key2[1]] ^
				tables.TableS8[block[8] ^ key2[0]]);
			WriteUInt32(block, 12,
				tables.TableS5[block[15] ^ key3[3]] ^
				tables.TableS6[block[14] ^ key3[2]] ^
				tables.TableS7[block[13] ^ key3[1]] ^
				tables.TableS8[block[12] ^ key3[0]]);

			PermuteBlock2(tables, block, 8 - round);
		}

		for (int i = 0; i < 4; i++)
		{
			WriteUInt32(block, i * 4, ReadUInt32(block, i * 4) ^ keySchedule[0, i]);
		}
	}

	private static void PermuteBlock1(Tables tables, byte[] block)
	{
		block[0] = tables.TableS3[block[0]];
		block[4] = tables.TableS3[0x400 + block[4]];
		block[8] = tables.TableS3[0x800 + block[8]];
		block[12] = tables.TableS3[0xc00 + block[12]];

		byte tmp = block[13];
		block[13] = tables.TableS3[0x100 + block[9]];
		block[9] = tables.TableS3[0xd00 + block[5]];
		block[5] = tables.TableS3[0x900 + block[1]];
		block[1] = tables.TableS3[0x500 + tmp];

		tmp = block[2];
		block[2] = tables.TableS3[0xa00 + block[10]];
		block[10] = tables.TableS3[0x200 + tmp];
		tmp = block[6];
		block[6] = tables.TableS3[0xe00 + block[14]];
		block[14] = tables.TableS3[0x600 + tmp];

		tmp = block[3];
		block[3] = tables.TableS3[0xf00 + block[7]];
		block[7] = tables.TableS3[0x300 + block[11]];
		block[11] = tables.TableS3[0x700 + block[15]];
		block[15] = tables.TableS3[0xb00 + tmp];
	}

	private static void PermuteBlock2(Tables tables, byte[] block, int round)
	{
		block[0] = PermuteTable2(tables, round * 16 + 0, block[0]);
		block[4] = PermuteTable2(tables, round * 16 + 4, block[4]);
		block[8] = PermuteTable2(tables, round * 16 + 8, block[8]);
		block[12] = PermuteTable2(tables, round * 16 + 12, block[12]);

		byte tmp = block[13];
		block[13] = PermuteTable2(tables, round * 16 + 13, block[9]);
		block[9] = PermuteTable2(tables, round * 16 + 9, block[5]);
		block[5] = PermuteTable2(tables, round * 16 + 5, block[1]);
		block[1] = PermuteTable2(tables, round * 16 + 1, tmp);

		tmp = block[2];
		block[2] = PermuteTable2(tables, round * 16 + 2, block[10]);
		block[10] = PermuteTable2(tables, round * 16 + 10, tmp);
		tmp = block[6];
		block[6] = PermuteTable2(tables, round * 16 + 6, block[14]);
		block[14] = PermuteTable2(tables, round * 16 + 14, tmp);

		tmp = block[3];
		block[3] = PermuteTable2(tables, round * 16 + 3, block[7]);
		block[7] = PermuteTable2(tables, round * 16 + 7, block[11]);
		block[11] = PermuteTable2(tables, round * 16 + 11, block[15]);
		block[15] = PermuteTable2(tables, round * 16 + 15, tmp);
	}

	private static void Garble(byte[] buffer0, byte[] buffer1, byte[] buffer2, byte[] buffer3, byte[] buffer4)
	{
		uint tmp;
		uint tmp2;
		uint tmp3;
		uint a;
		uint b;
		uint c;
		uint d;
		uint e;
		uint f;
		uint g;
		uint h;
		uint j;
		uint k;
		uint m;
		uint r;
		uint s;
		uint t;
		uint u;
		uint v;
		uint w;
		uint x;
		uint y;
		uint z;

		Set(buffer2, 12, 0x14u + (((uint)(buffer1[64] & 92) | ((uint)(buffer1[99] / 3) & 35)) & buffer4[Idx(Rol8x(buffer4[buffer1[206] % 21], 4), 21)]));
		Set(buffer1, 4, (uint)(buffer1[99] / 5) * (uint)(buffer1[99] / 5) * 2);
		buffer2[34] = 0xb8;
		XorAssign(buffer1, 153, (uint)buffer2[buffer1[203] % 35] * buffer2[buffer1[203] % 35] * buffer1[190]);
		SubAssign(buffer0, 3, ((uint)(buffer4[buffer1[205] % 21] >> 1) & 80) | 0xe6440u);
		buffer0[16] = 0x93;
		buffer0[13] = 0x62;
		SubAssign(buffer1, 33, (uint)(buffer4[buffer1[36] % 21] & 0xf6));
		tmp2 = buffer2[buffer1[67] % 35];
		buffer2[12] = 0x07;
		tmp = buffer0[buffer1[181] % 20];
		SubAssign(buffer1, 2, 3136);
		buffer0[19] = buffer4[buffer1[58] % 21];
		Set(buffer3, 0, 92u - buffer2[buffer1[32] % 35]);
		Set(buffer3, 4, (uint)buffer2[buffer1[15] % 35] + 0x9e);
		AddAssign(buffer1, 34, (uint)buffer4[buffer3[4] % 21] / 5);
		AddAssign(buffer0, 19, 0xfffffee6u - (((uint)buffer0[buffer3[4] % 20] >> 1) & 102));
		Set(buffer1, 15, (3u * ((((uint)buffer1[72] >> (buffer4[buffer1[190] % 21] & 7)) ^ ((uint)buffer1[72] << ((7 - (buffer4[buffer1[190] % 21] - 1)) & 7))) - (3u * buffer4[buffer1[126] % 21]))) ^ buffer1[15]);
		XorAssign(buffer0, 15, (uint)buffer2[buffer1[181] % 35] * buffer2[buffer1[181] % 35] * buffer2[buffer1[181] % 35]);
		XorAssign(buffer2, 4, (uint)buffer1[202] / 3);
		a = 92u - buffer0[buffer3[0] % 20];
		e = (a & 0xc6) | (~(uint)buffer1[105] & 0xc6) | (a & ~(uint)buffer1[105]);
		AddAssign(buffer2, 1, e * e * e);
		XorAssign(buffer0, 19, ((224u | ((uint)buffer4[buffer1[92] % 21] & 27)) * buffer2[buffer1[41] % 35]) / 3);
		AddAssign(buffer1, 140, WeirdRor8(92, buffer1[5] & 7));
		AddAssign(buffer2, 12, ((((~(uint)buffer1[4]) ^ buffer2[buffer1[12] % 35]) | buffer1[182]) & 192) | ((((~(uint)buffer1[4]) ^ buffer2[buffer1[12] % 35]) & buffer1[182])));
		AddAssign(buffer1, 36, 125);
		Set(buffer1, 124, Rol8x(U8((((74u & buffer1[138]) | ((74u | buffer1[138]) & buffer0[15])) & buffer0[buffer1[43] % 20]) | (((74u & buffer1[138]) | ((74u | buffer1[138]) & buffer0[15]) | buffer0[buffer1[43] % 20]) & 95)), 4));
		Set(buffer3, 8, ((((uint)buffer0[buffer3[4] % 20] & 95) & (((uint)buffer4[buffer1[68] % 21] & 46) << 1)) | 16) ^ 92);
		a = (uint)buffer1[177] + buffer4[buffer1[79] % 21];
		d = (((a >> 1) | ((3u * buffer1[148]) / 5)) & buffer2[1]) | ((a >> 1) & ((3u * buffer1[148]) / 5));
		Set(buffer3, 12, unchecked(0u - 34u) - d);
		a = 8u - (uint)(buffer2[22] & 7);
		b = (uint)buffer1[33] >> (int)(a & 7);
		c = (uint)buffer1[33] << (buffer2[22] & 7);
		AddAssign(buffer2, 16, (((uint)buffer2[buffer3[0] % 35] & 159) | buffer0[buffer3[4] % 20] | 8) - ((b ^ c) | 128));
		XorAssign(buffer0, 14, buffer2[buffer3[12] % 35]);
		a = WeirdRol8(buffer4[buffer0[buffer1[201] % 20] % 21], (buffer2[buffer1[112] % 35] << 1) & 7);
		d = ((uint)buffer0[buffer1[208] % 20] & 131) | ((uint)buffer0[buffer1[164] % 20] & 124);
		AddAssign(buffer1, 19, (a & (d / 5)) | ((a | (d / 5)) & 37));
		Set(buffer2, 8, WeirdRor8(140, ((buffer4[buffer1[45] % 21] + 92u) * (buffer4[buffer1[45] % 21] + 92u)) & 7));
		buffer1[190] = 56;
		XorAssign(buffer2, 8, buffer3[0]);
		Set(buffer1, 53, ~((uint)(buffer0[buffer1[83] % 20] | 204) / 5));
		AddAssign(buffer0, 13, buffer0[buffer1[41] % 20]);
		Set(buffer0, 10, (((uint)buffer2[buffer3[0] % 35] & buffer1[2]) | (((uint)buffer2[buffer3[0] % 35] | buffer1[2]) & buffer3[12])) / 15);
		a = (((56u | ((uint)buffer4[buffer1[2] % 21] & 68)) | buffer2[buffer3[8] % 35]) & 42) | ((((uint)buffer4[buffer1[2] % 21] & 68) | 56) & buffer2[buffer3[8] % 35]);
		Set(buffer3, 16, (a * a) + 110);
		Set(buffer3, 20, 202u - buffer3[16]);
		buffer3[24] = buffer1[151];
		XorAssign(buffer2, 13, buffer4[buffer3[0] % 21]);
		b = ((uint)(buffer2[buffer1[179] % 35] - 38) & 177) | ((uint)buffer3[12] & 177);
		c = (uint)(buffer2[buffer1[179] % 35] - 38) & buffer3[12];
		Set(buffer3, 28, 30u + ((b | c) * (b | c)));
		Set(buffer3, 32, (uint)buffer3[28] + 62);
		a = (((uint)buffer3[20] + (buffer3[0] & 74u)) | ~((uint)buffer4[buffer3[0] % 21])) & 121;
		b = ((uint)buffer3[20] + (buffer3[0] & 74u)) & ~((uint)buffer4[buffer3[0] % 21]);
		tmp3 = a | b;
		c = ((((a | b) ^ 0xffffffa6u) | buffer3[0]) & 4) | (((a | b) ^ 0xffffffa6u) & buffer3[0]);
		Set(buffer1, 47, ((uint)buffer2[buffer1[89] % 35] + c) ^ buffer1[47]);
		Set(buffer3, 36, ((uint)(Rol8(U8((tmp & 179) + 68), 2) & buffer0[3]) | (tmp2 & ~((uint)buffer0[3]))) - 15u);
		XorAssign(buffer1, 123, 221);
		a = ((uint)buffer4[buffer3[0] % 21] / 3) - buffer2[buffer3[4] % 35];
		c = (((uint)(buffer3[0] & 163) + 92) & 246) | ((uint)buffer3[0] & 92);
		e = ((c | buffer3[24]) & 54) | (c & buffer3[24]);
		Set(buffer3, 40, a - e);
		Set(buffer3, 44, tmp3 ^ 81 ^ ((((uint)buffer3[0] >> 1) & 101) + 26));
		Set(buffer3, 48, (uint)buffer2[buffer3[4] % 35] & 27);
		buffer3[52] = 27;
		buffer3[56] = 199;
		Set(buffer3, 64, (uint)buffer3[4] + (((((((((uint)buffer3[40] | buffer3[24]) & 177) | ((uint)buffer3[40] & buffer3[24])) & ((((uint)buffer4[buffer3[0] % 20] & 177) | 176) | ((uint)buffer4[buffer3[0] % 21] & ~3u))) | (((((uint)buffer3[40] & buffer3[24]) | (((uint)buffer3[40] | buffer3[24]) & 177)) & 199) | ((((uint)buffer4[buffer3[0] % 21] & 1) + 176) | ((uint)buffer4[buffer3[0] % 21] & ~3u)) & buffer3[56]))) & ~(uint)buffer3[52]) | buffer3[48]));
		XorAssign(buffer2, 33, buffer1[26]);
		XorAssign(buffer1, 106, (uint)buffer3[20] ^ 133);
		Set(buffer2, 30, (((uint)buffer3[64] / 3) - (275u | ((uint)buffer3[0] & 247))) ^ buffer0[buffer1[122] % 20]);
		Set(buffer1, 22, ((uint)buffer2[buffer1[90] % 35] & 95) | 68);
		a = ((uint)buffer4[buffer3[36] % 21] & 184) | ((uint)buffer2[buffer3[44] % 35] & ~184u);
		AddAssign(buffer2, 18, (a * a * a) >> 1);
		SubAssign(buffer2, 5, buffer4[buffer1[92] % 21]);
		a = ((((uint)buffer1[41] & ~24u) | ((uint)buffer2[buffer1[183] % 35] & 24)) & ((uint)buffer3[16] + 53)) | ((uint)buffer3[20] & buffer2[buffer3[20] % 35]);
		b = ((uint)buffer1[17] & ~((uint)buffer3[44])) | ((uint)buffer0[buffer1[59] % 20] & buffer3[44]);
		XorAssign(buffer2, 18, a * b);
		a = WeirdRor8(buffer1[11], buffer2[buffer1[28] % 35] & 7) & 7;
		b = ((((uint)buffer0[buffer1[93] % 20] & ~((uint)buffer0[14])) | ((uint)buffer0[14] & 150)) & ~28u) | ((uint)buffer1[7] & 28);
		Set(buffer2, 22, (((((b | WeirdRol8(buffer2[buffer3[0] % 35], a)) & buffer2[33]) | (b & WeirdRol8(buffer2[buffer3[0] % 35], a))) + 74) & 0xff));
		a = buffer4[(buffer0[buffer1[39] % 20] ^ 217) % 21];
		SubAssign(buffer0, 15, (((((uint)buffer3[20] | buffer3[0]) & 214) | ((uint)buffer3[20] & buffer3[0])) & a) | (((((uint)buffer3[20] | buffer3[0]) & 214) | ((uint)buffer3[20] & buffer3[0]) | a) & buffer3[32]));
		b = ((((uint)buffer2[buffer1[57] % 35] & buffer0[buffer3[64] % 20]) | (((uint)buffer0[buffer3[64] % 20] | buffer2[buffer1[57] % 35]) & 95) | ((uint)buffer3[64] & 45) | 82) & 32);
		c = (((uint)buffer2[buffer1[57] % 35] & buffer0[buffer3[64] % 20]) | (((uint)buffer2[buffer1[57] % 35] | buffer0[buffer3[64] % 20]) & 95)) & (((uint)buffer3[64] & 45) | 82);
		d = (((uint)buffer3[0] / 3) - ((uint)buffer3[64] | buffer1[22])) ^ ((uint)buffer3[28] + 62) ^ (b | c);
		t = buffer0[(d & 0xff) % 20];
		Set(buffer3, 68, (uint)buffer0[buffer1[99] % 20] * buffer0[buffer1[99] % 20] * buffer0[buffer1[99] % 20] * buffer0[buffer1[99] % 20] | buffer2[buffer3[64] % 35]);
		u = buffer0[buffer1[50] % 20];
		w = buffer2[buffer1[138] % 35];
		x = buffer4[buffer1[39] % 21];
		y = buffer0[buffer1[4] % 20];
		z = buffer4[buffer1[202] % 21];
		v = buffer0[buffer1[151] % 20];
		s = buffer2[buffer1[14] % 35];
		r = buffer0[buffer1[145] % 20];
		a = ((uint)buffer2[buffer3[68] % 35] & buffer0[buffer1[209] % 20]) | (((uint)buffer2[buffer3[68] % 35] | buffer0[buffer1[209] % 20]) & 24);
		b = WeirdRol8(buffer4[buffer1[127] % 21], buffer2[buffer3[68] % 35] & 7);
		c = (a & buffer0[10]) | (b & ~((uint)buffer0[10]));
		d = 7u ^ ((uint)buffer4[buffer2[buffer3[36] % 35] % 21] << 1);
		Set(buffer3, 72, (c & 71) | (d & ~71u));
		AddAssign(buffer2, 2, ((((uint)buffer0[buffer3[20] % 20] << 1) & 159) | ((uint)buffer4[buffer1[190] % 21] & ~159u)) & (((((uint)buffer4[buffer3[64] % 21] & 110) | ((uint)buffer0[buffer1[25] % 20] & ~110u)) & ~150u) | ((uint)buffer1[25] & 150)));
		SubAssign(buffer2, 14, (((uint)buffer2[buffer3[20] % 35] & ((uint)buffer3[72] ^ buffer2[buffer1[100] % 35])) & ~34u) | ((uint)buffer1[97] & 34));
		buffer0[17] = 115;
		XorAssign(buffer1, 23, (((((((uint)buffer4[buffer1[17] % 21] | buffer0[buffer3[20] % 20]) & buffer3[72]) | ((uint)buffer4[buffer1[17] % 21] & buffer0[buffer3[20] % 20])) & ((uint)buffer1[50] / 3)) | (((((uint)buffer4[buffer1[17] % 21] | buffer0[buffer3[20] % 20]) & buffer3[72]) | ((uint)buffer4[buffer1[17] % 21] & buffer0[buffer3[20] % 20]) | ((uint)buffer1[50] / 3)) & 246)) << 1));
		Set(buffer0, 13, ((((((uint)buffer0[buffer3[40] % 20] | buffer1[10]) & 82) | ((uint)buffer0[buffer3[40] % 20] & buffer1[10])) & 209) | (((uint)buffer0[buffer1[39] % 20] << 1) & 46)) >> 1);
		SubAssign(buffer2, 33, (uint)buffer1[113] & 9);
		SubAssign(buffer2, 28, ((((2u | ((uint)buffer1[110] & 222)) >> 1) & ~223u) | ((uint)buffer3[20] & 223)));
		j = WeirdRol8(U8(v | z), u & 7);
		a = ((uint)buffer2[16] & t) | (w & ~((uint)buffer2[16]));
		b = ((uint)buffer1[33] & 17) | (x & ~17u);
		e = (y | ((a + b) / 5)) & 147 | (y & ((a + b) / 5));
		m = ((uint)buffer3[40] & buffer4[((buffer3[8] + j + e) & 0xff) % 21]) | (((uint)buffer3[40] | buffer4[((buffer3[8] + j + e) & 0xff) % 21]) & buffer2[23]);
		Set(buffer0, 15, (((((uint)buffer4[buffer3[20] % 21] - 48) & ~((uint)buffer1[184])) | (((uint)buffer4[buffer3[20] % 21] - 48) & 189) | (189u & ~((uint)buffer1[184]))) & (m * m * m)));
		AddAssign(buffer2, 22, buffer1[183]);
		Set(buffer3, 76, (3u * buffer4[buffer1[1] % 21]) ^ buffer3[0]);
		a = buffer2[((buffer3[8] + (j + e)) & 0xff) % 35];
		f = (((uint)buffer4[buffer1[178] % 21] & a) | (((uint)buffer4[buffer1[178] % 21] | a) & 209)) * buffer0[buffer1[13] % 20] * ((uint)buffer4[buffer1[26] % 21] >> 1);
		g = (f + 0x733ffff9u) * 198u - (((f + 0x733ffff9u) * 396u + 212u) & 212u) + 85u;
		Set(buffer3, 80, (uint)buffer3[36] + (g ^ 148) + ((g ^ 107) << 1) - 127);
		Set(buffer3, 84, ((uint)buffer2[buffer3[64] % 35] & 245) | ((uint)buffer2[buffer3[20] % 35] & 10));
		a = (uint)buffer0[buffer3[68] % 20] | 81;
		SubAssign(buffer2, 18, ((a * a * a) & ~((uint)buffer0[15])) | (((uint)buffer3[80] / 15) & buffer0[15]));
		Set(buffer3, 88, (uint)buffer3[8] + j + e - buffer0[buffer1[160] % 20] + ((uint)buffer4[buffer0[((buffer3[8] + j + e) & 255) % 20] % 21] / 3));
		b = ((r ^ buffer3[72]) & ~198u) | ((s * s) & 198);
		f = ((uint)buffer4[buffer1[69] % 21] & buffer1[172]) | (((uint)buffer4[buffer1[69] % 21] | buffer1[172]) & (((uint)buffer3[12] - b) + 77));
		Set(buffer0, 16, 147u - (((uint)buffer3[72] & ((f & 251) | 1)) | (((f & 250) | buffer3[72]) & 198)));
		c = ((uint)buffer4[buffer1[168] % 21] & buffer0[buffer1[29] % 20] & 7) | (((uint)buffer4[buffer1[168] % 21] | buffer0[buffer1[29] % 20]) & 6);
		f = ((uint)buffer4[buffer1[155] % 21] & buffer1[105]) | (((uint)buffer4[buffer1[155] % 21] | buffer1[105]) & 141);
		SubAssign(buffer0, 3, buffer4[WeirdRol32(U8(f), c) % 21]);
		Set(buffer1, 5, WeirdRor8(buffer0[12], ((uint)buffer0[buffer1[61] % 20] / 5) & 7) ^ (((~(uint)buffer2[buffer3[84] % 35]) & 0xffffffffu) / 5));
		AddAssign(buffer1, 198, buffer1[3]);
		a = 162u | buffer2[buffer3[64] % 35];
		AddAssign(buffer1, 164, (a * a) / 5);
		g = WeirdRor8(139, buffer3[80] & 7);
		c = ((uint)buffer4[buffer3[64] % 21] * buffer4[buffer3[64] % 21] * buffer4[buffer3[64] % 21] & 95) | ((uint)buffer0[buffer3[40] % 20] & ~95u);
		Set(buffer3, 92, (g & 12) | ((uint)buffer0[buffer3[20] % 20] & 12) | (g & buffer0[buffer3[20] % 20]) | c);
		AddAssign(buffer2, 12, (((uint)buffer1[103] & 32) | ((uint)buffer3[92] & ((uint)buffer1[103] | 60)) | 16) / 3);
		buffer3[96] = buffer1[143];
		buffer3[100] = 27;
		Set(buffer3, 104, ((((uint)buffer3[40] & ~((uint)buffer2[8])) | ((uint)buffer1[35] & buffer2[8])) & buffer3[64]) ^ 119);
		Set(buffer3, 108, 238u & (((((uint)buffer3[40] & ~((uint)buffer2[8])) | ((uint)buffer1[35] & buffer2[8])) & buffer3[64]) << 1));
		Set(buffer3, 112, (~((uint)buffer3[64]) & ((uint)buffer3[84] / 3)) ^ 49);
		Set(buffer3, 116, 98u & ((~((uint)buffer3[64]) & ((uint)buffer3[84] / 3)) << 1));
		a = ((uint)buffer1[35] & buffer2[8]) | ((uint)buffer3[40] & ~((uint)buffer2[8]));
		b = (a & buffer3[64]) | ((((uint)buffer3[84] / 3) & ~((uint)buffer3[64])));
		Set(buffer1, 143, (uint)buffer3[96] - ((b & (86 + (((uint)buffer1[172] & 64) >> 1))) | ((((((uint)buffer1[172] & 65) >> 1) ^ 86) | ((~((uint)buffer3[64]) & ((uint)buffer3[84] / 3)) | ((((uint)buffer3[40] & ~((uint)buffer2[8])) | ((uint)buffer1[35] & buffer2[8])) & buffer3[64]))) & buffer3[100])));
		buffer2[29] = 162;
		a = ((((uint)buffer4[buffer3[88] % 21] & 160) | ((uint)buffer0[buffer1[125] % 20] & 95)) >> 1);
		b = (uint)buffer2[buffer1[149] % 35] ^ ((uint)buffer1[43] * buffer1[43]);
		AddAssign(buffer0, 15, (b & a) | ((a | b) & 115));
		Set(buffer3, 120, (uint)buffer3[64] - buffer0[buffer3[40] % 20]);
		buffer1[95] = buffer4[buffer3[20] % 21];
		a = WeirdRor8(buffer2[buffer3[80] % 35], (buffer2[buffer1[17] % 35] * buffer2[buffer1[17] % 35] * buffer2[buffer1[17] % 35]) & 7);
		SubAssign(buffer0, 7, a * a);
		Set(buffer2, 8, (uint)buffer2[8] - buffer1[184] + (uint)buffer4[buffer1[202] % 21] * buffer4[buffer1[202] % 21] * buffer4[buffer1[202] % 21]);
		Set(buffer0, 16, ((uint)buffer2[buffer1[102] % 35] << 1) & 132);
		Set(buffer3, 124, ((uint)buffer4[buffer3[40] % 21] >> 1) ^ buffer3[68]);
		SubAssign(buffer0, 7, (uint)buffer0[buffer1[191] % 20] - ((((uint)buffer4[buffer1[80] % 21] << 1) & ~177u) | ((uint)buffer4[buffer4[buffer3[88] % 21] % 21] & 177)));
		buffer0[6] = buffer0[buffer1[119] % 20];
		a = ((uint)buffer4[buffer1[190] % 21] & ~209u) | ((uint)buffer1[118] & 209);
		b = (uint)buffer0[buffer3[120] % 20] * buffer0[buffer3[120] % 20];
		Set(buffer0, 12, ((uint)buffer0[buffer3[84] % 20] ^ ((uint)buffer2[buffer1[71] % 35] + buffer2[buffer1[15] % 35])) & ((a & b) | ((a | b) & 27)));
		b = ((uint)buffer1[32] & buffer2[buffer3[88] % 35]) | (((uint)buffer1[32] | buffer2[buffer3[88] % 35]) & 23);
		d = (((uint)buffer4[buffer1[57] % 21] * 231) & 169) | (b & 86);
		f = ((((uint)buffer0[buffer1[82] % 20] & ~29u) | ((uint)buffer4[buffer3[124] % 21] & 29)) & 190) | ((uint)buffer4[(d / 5) % 21] & ~190u);
		h = (uint)buffer0[buffer3[40] % 20] * buffer0[buffer3[40] % 20] * buffer0[buffer3[40] % 20];
		k = (h & buffer1[82]) | (h & 92) | ((uint)buffer1[82] & 92);
		Set(buffer3, 128, ((f & k) | ((f | k) & 192)) ^ (d / 5));
		XorAssign(buffer2, 25, (((uint)buffer0[buffer3[120] % 20] << 1) * buffer1[5]) - (WeirdRol8(buffer3[76], buffer4[buffer3[124] % 21] & 7) & ((uint)buffer3[20] + 110)));
	}

	private static Tables LoadTables()
	{
		string header = ReadThirdPartyFile("omg_hax.h");
		string omgHax = ReadThirdPartyFile("omg_hax.c");
		string sapHash = ReadThirdPartyFile("sap_hash.c");
		return new Tables(
			To2D(ReadBytes(header, "message_key"), 4, 144),
			To2D(ReadBytes(header, "message_iv"), 4, 16),
			ReadBytes(header, "z_key"),
			ReadBytes(header, "x_key"),
			ReadBytes(header, "t_key"),
			ReadUInts(header, "table_s5"),
			ReadUInts(header, "table_s6"),
			ReadUInts(header, "table_s7"),
			ReadUInts(header, "table_s8"),
			ReadBytes(header, "table_s1"),
			ReadBytes(header, "table_s2"),
			ReadBytes(header, "table_s3"),
			ReadBytes(header, "table_s4"),
			ReadUInts(header, "table_s9"),
			ReadBytes(header, "table_s10"),
			ReadBytes(omgHax, "sap_iv"),
			ReadBytes(omgHax, "sap_key_material"),
			ReadBytes(omgHax, "index_mangle"),
			ReadBytes(omgHax, "initial_session_key"),
			ReadBytes(omgHax, "static_source_1"),
			ReadBytes(omgHax, "static_source_2"),
			ReadBytes(omgHax, "default_sap"),
			ReadBytes(sapHash, "buffer0"),
			ReadBytes(sapHash, "buffer2"),
			ReadBytes(sapHash, "buffer4"),
			ReadInts(sapHash, "i0_index"));
	}

	private static string ReadThirdPartyFile(string fileName)
	{
		string baseDirectory = Path.GetDirectoryName(typeof(AirPlayPlayFair).Assembly.Location) ?? AppContext.BaseDirectory;
		string path = Path.Combine(baseDirectory, "ThirdParty", "playfair", fileName);
		if (File.Exists(path))
		{
			return File.ReadAllText(path);
		}

		string fallback = Path.Combine(baseDirectory, "..", "..", "..", "ThirdParty", "playfair", fileName);
		if (File.Exists(fallback))
		{
			return File.ReadAllText(fallback);
		}

		throw new FileNotFoundException("Missing AirPlay FairPlay table file.", path);
	}

	private static byte[] ReadBytes(string content, string name)
	{
		uint[] values = ReadNumbers(content, name);
		byte[] bytes = new byte[values.Length];
		for (int i = 0; i < values.Length; i++)
		{
			bytes[i] = U8(values[i]);
		}
		return bytes;
	}

	private static uint[] ReadUInts(string content, string name)
	{
		return ReadNumbers(content, name);
	}

	private static int[] ReadInts(string content, string name)
	{
		uint[] values = ReadNumbers(content, name);
		int[] ints = new int[values.Length];
		for (int i = 0; i < values.Length; i++)
		{
			ints[i] = unchecked((int)values[i]);
		}
		return ints;
	}

	private static uint[] ReadNumbers(string content, string name)
	{
		Match match = Regex.Match(content, @"(?s)\b" + Regex.Escape(name) + @"(?:\s*\[[^\]]*\])*\s*=\s*\{(?<body>.*?)\};");
		if (!match.Success)
		{
			throw new InvalidOperationException("Missing FairPlay table: " + name);
		}

		MatchCollection matches = Regex.Matches(match.Groups["body"].Value, @"0x[0-9a-fA-F]+|\b\d+\b");
		uint[] values = new uint[matches.Count];
		for (int i = 0; i < matches.Count; i++)
		{
			string value = matches[i].Value;
			values[i] = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? uint.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
				: uint.Parse(value, CultureInfo.InvariantCulture);
		}

		return values;
	}

	private static byte[,] To2D(byte[] values, int rows, int columns)
	{
		byte[,] result = new byte[rows, columns];
		for (int row = 0; row < rows; row++)
		{
			for (int column = 0; column < columns; column++)
			{
				result[row, column] = values[row * columns + column];
			}
		}
		return result;
	}

	private static int TableIndex(int value)
	{
		return ((31 * value) % 0x28) << 8;
	}

	private static byte MessageTable(Tables tables, int index, byte value)
	{
		return tables.TableS2[((97 * index % 144) << 8) + value];
	}

	private static byte PermuteTable2(Tables tables, int index, byte value)
	{
		return tables.TableS4[((71 * index % 144) << 8) + value];
	}

	private static void XorBlocks(byte[] first, byte[] second, byte[] output, int outputOffset)
	{
		for (int i = 0; i < 16; i++)
		{
			output[outputOffset + i] = (byte)(first[i] ^ second[i]);
		}
	}

	private static uint ReadUInt32(byte[] source, int offset)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, 4));
	}

	private static void WriteUInt32(byte[] destination, int offset, uint value)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset, 4), value);
	}

	private static byte[] WordBytes(uint value)
	{
		byte[] bytes = new byte[4];
		WriteUInt32(bytes, 0, value);
		return bytes;
	}

	private static void Swap(byte[] bytes, int first, int second)
	{
		(bytes[first], bytes[second]) = (bytes[second], bytes[first]);
	}

	private static void SwapWords(byte[] bytes, int firstWord, int secondWord)
	{
		uint first = ReadUInt32(bytes, firstWord * 4);
		uint second = ReadUInt32(bytes, secondWord * 4);
		WriteUInt32(bytes, firstWord * 4, second);
		WriteUInt32(bytes, secondWord * 4, first);
	}

	private static byte Rol8(byte input, int count)
	{
		count &= 7;
		return count == 0 ? input : U8(((uint)input << count) | ((uint)input >> (8 - count)));
	}

	private static uint Rol8x(byte input, int count)
	{
		count &= 7;
		return count == 0 ? input : (((uint)input << count) | ((uint)input >> (8 - count)));
	}

	private static uint WeirdRor8(uint input, uint count)
	{
		count &= 7;
		return count == 0 ? 0 : (((input >> (int)count) & 0xff) | ((input & 0xff) << (int)(8 - count)));
	}

	private static uint WeirdRor8(uint input, int count)
	{
		return WeirdRor8(input, unchecked((uint)count));
	}

	private static uint WeirdRol8(byte input, uint count)
	{
		count &= 7;
		return count == 0 ? 0 : ((((uint)input << (int)count) & 0xff) | ((uint)input >> (int)(8 - count)));
	}

	private static uint WeirdRol8(byte input, int count)
	{
		return WeirdRol8(input, unchecked((uint)count));
	}

	private static uint WeirdRol32(byte input, uint count)
	{
		count &= 7;
		return count == 0 ? 0 : (((uint)input << (int)count) ^ ((uint)input >> (int)(8 - count)));
	}

	private static uint WeirdRol32(byte input, int count)
	{
		return WeirdRol32(input, unchecked((uint)count));
	}

	private static uint RotateLeft(uint input, int count)
	{
		return (input << count) | (input >> (32 - count));
	}

	private static uint Md5F(uint b, uint c, uint d)
	{
		return (b & c) | (~b & d);
	}

	private static uint Md5G(uint b, uint c, uint d)
	{
		return (b & d) | (c & ~d);
	}

	private static uint Md5H(uint b, uint c, uint d)
	{
		return b ^ c ^ d;
	}

	private static uint Md5I(uint b, uint c, uint d)
	{
		return c ^ (b | ~d);
	}

	private static uint Md5Constant(int index)
	{
		return unchecked((uint)(ulong)(Math.Abs(Math.Sin(index + 1)) * 4294967296.0));
	}

	private static int Index32(int value, int modulo)
	{
		return (int)(unchecked((uint)value) % (uint)modulo);
	}

	private static int Idx(uint value, int modulo)
	{
		return (int)(value % (uint)modulo);
	}

	private static byte U8(uint value)
	{
		return unchecked((byte)value);
	}

	private static void Set(byte[] bytes, int index, uint value)
	{
		bytes[index] = U8(value);
	}

	private static void AddAssign(byte[] bytes, int index, uint value)
	{
		bytes[index] = U8((uint)bytes[index] + value);
	}

	private static void SubAssign(byte[] bytes, int index, uint value)
	{
		bytes[index] = U8((uint)bytes[index] - value);
	}

	private static void XorAssign(byte[] bytes, int index, uint value)
	{
		bytes[index] = U8((uint)bytes[index] ^ value);
	}

	private static byte[] CopyOf(byte[] source)
	{
		byte[] copy = new byte[source.Length];
		Buffer.BlockCopy(source, 0, copy, 0, source.Length);
		return copy;
	}

	private sealed record Tables(
		byte[,] MessageKey,
		byte[,] MessageIv,
		byte[] ZKey,
		byte[] XKey,
		byte[] TKey,
		uint[] TableS5,
		uint[] TableS6,
		uint[] TableS7,
		uint[] TableS8,
		byte[] TableS1,
		byte[] TableS2,
		byte[] TableS3,
		byte[] TableS4,
		uint[] TableS9,
		byte[] TableS10,
		byte[] SapIv,
		byte[] SapKeyMaterial,
		byte[] IndexMangle,
		byte[] InitialSessionKey,
		byte[] StaticSource1,
		byte[] StaticSource2,
		byte[] DefaultSap,
		byte[] SapBuffer0,
		byte[] SapBuffer2,
		byte[] SapBuffer4,
		int[] SapIndexMangle);
}
