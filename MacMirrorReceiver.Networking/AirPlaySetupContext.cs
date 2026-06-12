using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace MacMirrorReceiver.Networking;

internal sealed class AirPlaySetupContext
{
	private const int RaopEventPort = 5000;
	private const int DataPort = 7100;
	private const int TimingPort = 7102;

	private readonly AirPlayTimingClient _timingClient;

	public AirPlaySetupContext(AirPlayTimingClient timingClient)
	{
		_timingClient = timingClient;
	}

	public byte[] BuildSetupResponse(byte[] requestBody, IPAddress? remoteAddress)
	{
		Dictionary<string, object?>? request = AirPlayBinaryPlist.Read(requestBody) as Dictionary<string, object?>;
		if (request == null)
		{
			AppLog.Write("AirPlay SETUP request was not a readable binary plist; using fallback response.");
			return BuildFallbackResponse();
		}

		List<int> streamTypes = ReadStreamTypes(request);
		int timingPort = ReadInt(request, "timingPort") ?? 0;
		AppLog.Write($"AirPlay SETUP parsed keys={string.Join(",", request.Keys)}, timingPort={timingPort}, streamTypes={string.Join(",", streamTypes)}.");
		if (remoteAddress != null && timingPort > 0)
		{
			_timingClient.Start(remoteAddress, timingPort);
		}

		Dictionary<string, object> response = new Dictionary<string, object>
		{
			["eventPort"] = RaopEventPort,
			["timingPort"] = TimingPort
		};

		if (streamTypes.Count > 0)
		{
			List<object> streams = new List<object>();
			foreach (int streamType in streamTypes)
			{
				if (streamType == 110)
				{
					streams.Add(new Dictionary<string, object>
					{
						["dataPort"] = DataPort,
						["type"] = 110
					});
				}
				else if (streamType == 96)
				{
					streams.Add(new Dictionary<string, object>
					{
						["controlPort"] = RaopEventPort,
						["dataPort"] = DataPort,
						["type"] = 96
					});
				}
				else
				{
					AppLog.Write($"AirPlay SETUP ignored unsupported stream type={streamType}.");
				}
			}

			if (streams.Count > 0)
			{
				response["streams"] = streams;
			}
		}

		return AirPlayBinaryPlist.Write(response);
	}

	private static byte[] BuildFallbackResponse()
	{
		return AirPlayBinaryPlist.Write(new Dictionary<string, object>
		{
			["eventPort"] = RaopEventPort,
			["timingPort"] = TimingPort,
			["streams"] = new List<object>
			{
				new Dictionary<string, object>
				{
					["dataPort"] = DataPort,
					["type"] = 110
				}
			}
		});
	}

	private static List<int> ReadStreamTypes(Dictionary<string, object?> request)
	{
		if (!request.TryGetValue("streams", out object? streamsObject) || streamsObject is not List<object?> streams)
		{
			return new List<int>();
		}

		return streams
			.OfType<Dictionary<string, object?>>()
			.Select(stream => ReadInt(stream, "type"))
			.Where(type => type.HasValue)
			.Select(type => type!.Value)
			.ToList();
	}

	private static int? ReadInt(Dictionary<string, object?> dictionary, string key)
	{
		if (!dictionary.TryGetValue(key, out object? value))
		{
			return null;
		}

		return value switch
		{
			byte number => number,
			short number => number,
			int number => number,
			long number => checked((int)number),
			ulong number => checked((int)number),
			_ => null
		};
	}
}
