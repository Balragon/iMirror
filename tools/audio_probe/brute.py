import json, struct, sys, os, subprocess, collections
sys.argv  # base
BASE = sys.argv[1]
FF = r"C:\Users\isc07\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin\ffmpeg.exe"
from Crypto.Cipher import AES
from Crypto.Util import Counter

meta = json.load(open(BASE + ".meta.json"))
key0 = bytes.fromhex(meta["aesKeyHex"]); eiv = bytes.fromhex(meta["eivHex"])
sr=meta["sampleRate"]; ch=meta["channels"]; spf=meta["samplesPerFrame"]

data=open(BASE+".rtp","rb").read(); off=0; packets=[]
while off+4<=len(data):
    (plen,)=struct.unpack_from("<I",data,off); off+=4
    if off+plen>len(data): break
    packets.append(data[off:off+plen]); off+=plen
seen=set(); frames=[]
for p in packets:
    if len(p)<12: continue
    seq=struct.unpack_from(">H",p,2)[0]; pl=p[12:]
    if len(pl)<16 or seq in seen: continue
    seen.add(seq); frames.append(pl)
print("frames",len(frames))
testframes=frames[20:80]

def variants(k):
    yield "raw",k
    yield "rev",k[::-1]
    wb=bytearray(k)
    for o in range(0,16,4): wb[o:o+4]=wb[o:o+4][::-1]
    yield "wbyte",bytes(wb)
    wo=bytearray(16)
    for o in range(0,16,4): wo[12-o:16-o]=k[o:o+4]
    yield "wordrev",bytes(wo)

def cbc_rem(k,pl,off):
    pl=pl[off:]; n=(len(pl)//16)*16
    if n==0: return pl
    return AES.new(k,AES.MODE_CBC,eiv).decrypt(pl[:n])+pl[n:]
def ctr_be(k,pl,off):
    pl=pl[off:]; c=Counter.new(128,initial_value=int.from_bytes(eiv,"big"))
    return AES.new(k,AES.MODE_CTR,counter=c).decrypt(pl)
def ctr_le(k,pl,off):
    pl=pl[off:]; c=Counter.new(128,initial_value=int.from_bytes(eiv,"little"),little_endian=True)
    return AES.new(k,AES.MODE_CTR,counter=c).decrypt(pl)

MODES={"cbcrem":cbc_rem,"ctrbe":ctr_be,"ctrle":ctr_le}

# ---- MP4 builder (validated: ffprobe sees AAC ELD) ----
def box(t,p): return struct.pack(">I",8+len(p))+t+p
def fbox(t,v,f,p): return box(t,struct.pack(">B",v)+struct.pack(">I",f)[1:]+p)
FREQ={44100:4,48000:3,32000:5,24000:6,22050:7,16000:8}
class BW:
    def __init__(s): s.b=[]
    def w(s,v,n):
        for i in range(n-1,-1,-1): s.b.append((v>>i)&1)
    def bytes(s):
        b=s.b[:]
        while len(b)%8: b.append(0)
        o=bytearray()
        for i in range(0,len(b),8):
            x=0
            for j in range(8): x=(x<<1)|b[i+j]
            o.append(x)
        return bytes(o)
def asc(frame480):
    bw=BW(); bw.w(31,5); bw.w(7,6); bw.w(FREQ[sr],4); bw.w(ch,4)
    bw.w(1 if frame480 else 0,1); bw.w(0,1); bw.w(0,1); bw.w(0,1); bw.w(0,1); bw.w(0,4)
    return bw.bytes()
def esds(a):
    def d(t,p): return bytes([t,0x80,0x80,0x80,len(p)])+p
    dsi=d(0x05,a); dcd=d(0x04,bytes([0x40,0x15,0,0,0,0,0,0,0,0,0,0,0])+dsi); slc=d(0x06,bytes([2]))
    es=d(0x03,struct.pack(">H",0)+bytes([0])+dcd+slc); return fbox(b"esds",0,0,es)
def mp4a(a):
    p=b"\x00"*6+struct.pack(">H",1)+b"\x00"*8+struct.pack(">H",ch)+struct.pack(">H",16)+b"\x00"*4+struct.pack(">H",sr)+struct.pack(">H",0)+esds(a)
    return box(b"mp4a",p)
def build(frames,a):
    stsd=fbox(b"stsd",0,0,struct.pack(">I",1)+mp4a(a))
    stts=fbox(b"stts",0,0,struct.pack(">I",1)+struct.pack(">II",len(frames),spf))
    stsc=fbox(b"stsc",0,0,struct.pack(">I",1)+struct.pack(">III",1,len(frames),1))
    stsz=fbox(b"stsz",0,0,struct.pack(">I",0)+struct.pack(">I",len(frames))+b"".join(struct.pack(">I",len(f)) for f in frames))
    dur=len(frames)*spf
    mat=bytes.fromhex("00010000000000000000000000000000000100000000000000000000000000004000000000000000")
    smhd=fbox(b"smhd",0,0,b"\x00\x00\x00\x00"); dref=fbox(b"dref",0,0,struct.pack(">I",1)+fbox(b"url ",0,1,b""))
    dinf=box(b"dinf",dref); hdlr=fbox(b"hdlr",0,0,b"\x00"*4+b"soun"+b"\x00"*12+b"a\x00")
    mdhd=fbox(b"mdhd",0,0,struct.pack(">IIII",0,0,sr,dur)+struct.pack(">HH",0x55c4,0))
    tkhd=fbox(b"tkhd",0,7,struct.pack(">IIIII",0,0,1,0,dur)+b"\x00"*8+struct.pack(">HHHH",0,0,0,0)+mat)
    mvhd=fbox(b"mvhd",0,0,struct.pack(">IIII",0,0,sr,dur)+struct.pack(">I",0x00010000)+struct.pack(">H",0)+b"\x00"*10+mat+b"\x00"*24+struct.pack(">I",2))
    def assemble(base):
        stco=fbox(b"stco",0,0,struct.pack(">I",1)+struct.pack(">I",base))
        stbl=box(b"stbl",stsd+stts+stsc+stsz+stco); minf=box(b"minf",smhd+dinf+stbl)
        mdia=box(b"mdia",mdhd+hdlr+minf); trak=box(b"trak",tkhd+mdia); return box(b"moov",mvhd+trak)
    ftyp=box(b"ftyp",b"M4A "+struct.pack(">I",0)+b"M4A mp42isom")
    moov=assemble(0); base=len(ftyp)+len(moov)+8; moov=assemble(base)
    return ftyp+moov+box(b"mdat",b"".join(frames))

A480=asc(True); A512=asc(False)
results=[]
import tempfile
for kn,k in variants(key0):
    for mn,fn in MODES.items():
        for offv in (0,):
            dec=[fn(k,f,offv) for f in testframes]
            for an,a in (("eld480",A480),("eld512",A512)):
                mp4=build(dec,a); path=BASE+f".bf.m4a"; open(path,"wb").write(mp4)
                r=subprocess.run([FF,"-v","error","-i",path,"-f","null","-"],capture_output=True,text=True)
                errs=r.stderr.count("Error submitting packet")
                ok=len(testframes)-errs
                results.append((ok,kn,mn,offv,an,r.stderr.count("\n")))
results.sort(reverse=True)
print("OK   key     mode   off  asc      stderrlines")
for ok,kn,mn,offv,an,sl in results[:12]:
    print(f"{ok:3d}  {kn:7s} {mn:6s} {offv:3d}  {an:7s} {sl}")
