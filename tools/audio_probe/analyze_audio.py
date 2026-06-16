import json, struct, sys, os, subprocess, collections

BASE = sys.argv[1]
FF = r"C:\Users\isc07\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin\ffmpeg.exe"

meta = json.load(open(BASE + ".meta.json"))
key = bytes.fromhex(meta["aesKeyHex"])
eiv = bytes.fromhex(meta["eivHex"])
sr = meta["sampleRate"]; ch = meta["channels"]; spf = meta["samplesPerFrame"]
print("key", key.hex(), "eiv", eiv.hex(), "sr", sr, "ch", ch, "spf", spf)

# --- parse .rtp (repeated [u32 LE len][packet]) ---
data = open(BASE + ".rtp", "rb").read()
off = 0; packets = []
while off + 4 <= len(data):
    (plen,) = struct.unpack_from("<I", data, off); off += 4
    if off + plen > len(data): break
    packets.append(data[off:off+plen]); off += plen
print("packets", len(packets))

# RTP payload = skip 12-byte header. dedupe by seq, keep first, only payload>=16
seen = set(); frames = []
sizes = collections.Counter()
for p in packets:
    if len(p) < 12: continue
    seq = struct.unpack_from(">H", p, 2)[0]
    payload = p[12:]
    sizes[len(payload)] += 1
    if len(payload) < 16: continue
    if seq in seen: continue
    seen.add(seq); frames.append(payload)
print("payload size histogram (top):", sizes.most_common(8))
print("unique audio frames (>=16B):", len(frames))

from Crypto.Cipher import AES

def cbc_remainder(pl):
    n = (len(pl)//16)*16
    if n == 0: return pl
    dec = AES.new(key, AES.MODE_CBC, eiv).decrypt(pl[:n])
    return dec + pl[n:]

def cbc_whole(pl):
    n = (len(pl)//16)*16
    return AES.new(key, AES.MODE_CBC, eiv).decrypt(pl[:n])  # drops remainder

def ctr_whole(pl):
    from Crypto.Util import Counter
    ctr = Counter.new(128, initial_value=int.from_bytes(eiv, "big"))
    return AES.new(key, AES.MODE_CTR, counter=ctr).decrypt(pl)

CANDS = {"cbc_remainder": cbc_remainder, "cbc_whole": cbc_whole, "ctr_whole": ctr_whole}

# --- build ELD AudioSpecificConfig ---
class BW:
    def __init__(s): s.bits=[]
    def w(s,val,n):
        for i in range(n-1,-1,-1): s.bits.append((val>>i)&1)
    def bytes(s):
        b=s.bits[:]
        while len(b)%8: b.append(0)
        out=bytearray()
        for i in range(0,len(b),8):
            v=0
            for j in range(8): v=(v<<1)|b[i+j]
            out.append(v)
        return bytes(out)

FREQ_IDX={96000:0,88200:1,64000:2,48000:3,44100:4,32000:5,24000:6,22050:7,16000:8,12000:9,11025:10,8000:11}
def eld_asc(sr,ch,frame480):
    bw=BW()
    # AOT 39 (ER AAC ELD) via escape
    bw.w(31,5); bw.w(39-32,6)
    bw.w(FREQ_IDX[sr],4)
    bw.w(ch,4)
    # ELDSpecificConfig
    bw.w(1 if frame480 else 0,1)  # frameLengthFlag: 1=480
    bw.w(0,1)  # aacSectionDataResilienceFlag
    bw.w(0,1)  # aacScalefactorDataResilienceFlag
    bw.w(0,1)  # aacSpectralDataResilienceFlag
    bw.w(0,1)  # ldSbrPresentFlag
    bw.w(0,4)  # eldExtType = ELDEXT_TERM
    return bw.bytes()

# --- minimal MP4 (m4a) muxer with esds(ASC) ---
def box(typ, payload): return struct.pack(">I",8+len(payload))+typ+payload
def fbox(typ,ver,flags,payload): return box(typ, struct.pack(">B",ver)+struct.pack(">I",flags)[1:]+payload)

def esds(asc):
    # DecoderSpecificInfo (tag5)
    def desc(tag,p): return bytes([tag])+bytes([0x80,0x80,0x80])+bytes([len(p)])+p
    dsi=desc(0x05,asc)
    # DecoderConfigDescriptor (tag4): objType 0x40 (AAC), streamType audio
    dcd_payload=bytes([0x40,0x15])+b"\x00\x00\x00"+struct.pack(">I",0)+struct.pack(">I",0)+dsi
    dcd=desc(0x04,dcd_payload)
    slc=desc(0x06,bytes([0x02]))
    es_payload=struct.pack(">H",0)+bytes([0x00])+dcd+slc
    es=desc(0x03,es_payload)
    return fbox(b"esds",0,0,es)

def mp4a(asc,sr,ch):
    payload=b"\x00"*6+struct.pack(">H",1)  # reserved + data_ref_index
    payload+=b"\x00"*8  # version/rev/vendor
    payload+=struct.pack(">H",ch)+struct.pack(">H",16)
    payload+=b"\x00"*4
    payload+=struct.pack(">H",sr)+struct.pack(">H",0)  # samplerate 16.16 (hi)
    payload+=esds(asc)
    return box(b"mp4a",payload)

def build_mp4(frames,asc,sr,ch,spf):
    stsd=fbox(b"stsd",0,0,struct.pack(">I",1)+mp4a(asc,sr,ch))
    stts=fbox(b"stts",0,0,struct.pack(">I",1)+struct.pack(">II",len(frames),spf))
    stsc=fbox(b"stsc",0,0,struct.pack(">I",1)+struct.pack(">III",1,len(frames),1))
    stsz=fbox(b"stsz",0,0,struct.pack(">I",0)+struct.pack(">I",len(frames))+b"".join(struct.pack(">I",len(f)) for f in frames))
    # data
    mdat_payload=b"".join(frames)
    # we will place mdat after moov; compute offset later
    stco=fbox(b"stco",0,0,struct.pack(">I",1)+struct.pack(">I",0))  # patch later
    stbl=box(b"stbl",stsd+stts+stsc+stsz+stco)
    smhd=fbox(b"smhd",0,0,struct.pack(">H",0)+struct.pack(">H",0))
    dref=fbox(b"dref",0,0,struct.pack(">I",1)+fbox(b"url ",0,1,b""))
    dinf=box(b"dinf",dref)
    hdlr=fbox(b"hdlr",0,0,b"\x00"*4+b"soun"+b"\x00"*12+b"a\x00")
    minf=box(b"minf",smhd+dinf+stbl)
    dur=len(frames)*spf
    mdhd=fbox(b"mdhd",0,0,struct.pack(">IIII",0,0,sr,dur)+struct.pack(">HH",0x55c4,0))
    mdia=box(b"mdia",mdhd+hdlr+minf)
    tkhd=fbox(b"tkhd",0,7,struct.pack(">IIIII",0,0,1,0,dur)+b"\x00"*8+struct.pack(">HHHH",0,0,0,0)+
              bytes.fromhex("00010000000000000000000000000000000100000000000000000000000000004000000000000000"))
    trak=box(b"trak",tkhd+mdia)
    mvhd=fbox(b"mvhd",0,0,struct.pack(">IIII",0,0,sr,dur)+struct.fromhex("00010000") if False else
              struct.pack(">IIII",0,0,sr,dur)+struct.pack(">I",0x00010000)+struct.pack(">H",0)+b"\x00"*10+
              bytes.fromhex("00010000000000000000000000000000000100000000000000000000000000004000000000000000")+b"\x00"*24+struct.pack(">I",2))
    moov=box(b"moov",mvhd+trak)
    ftyp=box(b"ftyp",b"M4A "+struct.pack(">I",0)+b"M4A mp42isom")
    # offset of mdat payload = len(ftyp)+len(moov)+8
    base=len(ftyp)+len(moov)+8
    # patch stco -> need rebuild with correct offset
    stco=fbox(b"stco",0,0,struct.pack(">I",1)+struct.pack(">I",base))
    stbl=box(b"stbl",stsd+stts+stsc+stsz+stco)
    minf=box(b"minf",smhd+dinf+stbl)
    mdia=box(b"mdia",mdhd+hdlr+minf)
    trak=box(b"trak",tkhd+mdia)
    moov=box(b"moov",mvhd+trak)
    base=len(ftyp)+len(moov)+8
    stco=fbox(b"stco",0,0,struct.pack(">I",1)+struct.pack(">I",base))
    stbl=box(b"stbl",stsd+stts+stsc+stsz+stco)
    minf=box(b"minf",smhd+dinf+stbl); mdia=box(b"mdia",mdhd+hdlr+minf); trak=box(b"trak",tkhd+mdia); moov=box(b"moov",mvhd+trak)
    mdat=box(b"mdat",mdat_payload)
    return ftyp+moov+mdat

asc=eld_asc(sr,ch,spf==480)
print("ELD ASC:", asc.hex())

testframes = frames[10:110]  # skip first few, take 100
for name,fn in CANDS.items():
    dec=[fn(f) for f in testframes]
    mp4=build_mp4(dec,asc,sr,ch,spf)
    path=BASE+f".{name}.m4a"
    open(path,"wb").write(mp4)
    wav=BASE+f".{name}.wav"
    r=subprocess.run([FF,"-v","error","-y","-i",path,"-f","wav",wav],capture_output=True,text=True)
    errlen=len(r.stderr.strip())
    wsz=os.path.getsize(wav) if os.path.exists(wav) else 0
    print(f"\n=== {name}: ffmpeg stderr_bytes={errlen}, wav_size={wsz}")
    if r.stderr.strip(): print(r.stderr.strip()[:600])
