import json, struct, sys, os, subprocess, hashlib
BASE=sys.argv[1]
FF=r"C:\Users\isc07\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin\ffmpeg.exe"
from Crypto.Cipher import AES
from Crypto.Util import Counter
sha512=lambda b: hashlib.sha512(b).digest()

m=json.load(open(BASE+".meta.json"))
aeskey=bytes.fromhex(m["aesKeyHex"]); eiv=bytes.fromhex(m["eivHex"])
secret=bytes.fromhex(m["sharedSecretHex"]); sr=m["sampleRate"]; ch=m["channels"]; spf=m["samplesPerFrame"]
sid=int(m["rtspTargetSessionId"]); usid=str(sid); ssid=str(sid - (1<<64) if sid>=(1<<63) else sid)

data=open(BASE+".rtp","rb").read(); off=0; pk=[]
while off+4<=len(data):
    (l,)=struct.unpack_from("<I",data,off); off+=4
    if off+l>len(data): break
    pk.append(data[off:off+l]); off+=l
seen=set(); frames=[]
for p in pk:
    if len(p)<12: continue
    seq=struct.unpack_from(">H",p,2)[0]; pl=p[12:]
    if len(pl)<16 or seq in seen: continue
    seen.add(seq); frames.append(pl)
print("frames",len(frames))
testframes=frames[20:80]

def kv(k):
    yield "raw",k
    yield "rev",k[::-1]

# build (name, key, iv) candidates
def stream(mixed16, idb, label):
    kk=sha512(b"AirPlayStreamKey"+idb+mixed16)[:16]
    ii=sha512(b"AirPlayStreamIV"+idb+mixed16)[:16]
    return kk,ii

CANDS=[]  # (name, key, iv)
CANDS.append(("raw|eiv", aeskey, eiv))
CANDS.append(("rawrev|eiv", aeskey[::-1], eiv))
CANDS.append(("sha(key+sec)|eiv", sha512(aeskey+secret)[:16], eiv))
CANDS.append(("sha(sec+key)|eiv", sha512(secret+aeskey)[:16], eiv))
CANDS.append(("sec16|eiv", secret[:16], eiv))
CANDS.append(("sha(sec)|eiv", sha512(secret)[:16], eiv))
CANDS.append(("sha(key)|eiv", sha512(aeskey)[:16], eiv))
for mn,mixed in [("Mstd",sha512(aeskey+secret)[:16]),("Mdirect",aeskey)]:
    for idn,idb in [("uid",usid.encode()),("sid",ssid.encode()),("empty",b"")]:
        kk,ii=stream(mixed,idb,mn+idn)
        CANDS.append((f"stream:{mn}:{idn}|derIV", kk, ii))
        CANDS.append((f"stream:{mn}:{idn}|eiv", kk, eiv))

def cbc_rem(k,iv,pl):
    n=(len(pl)//16)*16
    if n==0: return pl
    return AES.new(k,AES.MODE_CBC,iv).decrypt(pl[:n])+pl[n:]
def ctr_be(k,iv,pl):
    c=Counter.new(128,initial_value=int.from_bytes(iv,"big"))
    return AES.new(k,AES.MODE_CTR,counter=c).decrypt(pl)
MODES={"cbcrem":cbc_rem,"ctrbe":ctr_be}

# ---- MP4 (validated AAC ELD) ----
def box(t,p): return struct.pack(">I",8+len(p))+t+p
def fbox(t,v,f,p): return box(t,struct.pack(">B",v)+struct.pack(">I",f)[1:]+p)
FREQ={44100:4,48000:3}
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
def asc(f480):
    bw=BW(); bw.w(31,5); bw.w(7,6); bw.w(FREQ[sr],4); bw.w(ch,4)
    bw.w(1 if f480 else 0,1); bw.w(0,4); bw.w(0,4); return bw.bytes()
def esds(a):
    d=lambda t,p: bytes([t,0x80,0x80,0x80,len(p)])+p
    dsi=d(0x05,a); dcd=d(0x04,bytes([0x40,0x15,0,0,0,0,0,0,0,0,0,0,0])+dsi); slc=d(0x06,bytes([2]))
    return fbox(b"esds",0,0,d(0x03,struct.pack(">H",0)+bytes([0])+dcd+slc))
def mp4a(a):
    p=b"\x00"*6+struct.pack(">H",1)+b"\x00"*8+struct.pack(">H",ch)+struct.pack(">H",16)+b"\x00"*4+struct.pack(">H",sr)+struct.pack(">H",0)+esds(a)
    return box(b"mp4a",p)
def build(frames,a):
    stsd=fbox(b"stsd",0,0,struct.pack(">I",1)+mp4a(a)); stts=fbox(b"stts",0,0,struct.pack(">I",1)+struct.pack(">II",len(frames),spf))
    stsc=fbox(b"stsc",0,0,struct.pack(">I",1)+struct.pack(">III",1,len(frames),1))
    stsz=fbox(b"stsz",0,0,struct.pack(">I",0)+struct.pack(">I",len(frames))+b"".join(struct.pack(">I",len(f)) for f in frames))
    dur=len(frames)*spf; mat=bytes.fromhex("00010000000000000000000000000000000100000000000000000000000000004000000000000000")
    smhd=fbox(b"smhd",0,0,b"\x00\x00\x00\x00"); dref=fbox(b"dref",0,0,struct.pack(">I",1)+fbox(b"url ",0,1,b"")); dinf=box(b"dinf",dref)
    hdlr=fbox(b"hdlr",0,0,b"\x00"*4+b"soun"+b"\x00"*12+b"a\x00"); mdhd=fbox(b"mdhd",0,0,struct.pack(">IIII",0,0,sr,dur)+struct.pack(">HH",0x55c4,0))
    tkhd=fbox(b"tkhd",0,7,struct.pack(">IIIII",0,0,1,0,dur)+b"\x00"*8+struct.pack(">HHHH",0,0,0,0)+mat)
    mvhd=fbox(b"mvhd",0,0,struct.pack(">IIII",0,0,sr,dur)+struct.pack(">I",0x00010000)+struct.pack(">H",0)+b"\x00"*10+mat+b"\x00"*24+struct.pack(">I",2))
    def asm(base):
        stco=fbox(b"stco",0,0,struct.pack(">I",1)+struct.pack(">I",base)); stbl=box(b"stbl",stsd+stts+stsc+stsz+stco)
        return box(b"moov",mvhd+box(b"trak",tkhd+box(b"mdia",mdhd+hdlr+box(b"minf",smhd+dinf+stbl))))
    ftyp=box(b"ftyp",b"M4A "+struct.pack(">I",0)+b"M4A mp42isom"); moov=asm(0); base=len(ftyp)+len(moov)+8; moov=asm(base)
    return ftyp+moov+box(b"mdat",b"".join(frames))

A480=asc(True)
res=[]
for cn,k,iv in CANDS:
    for mn,fn in MODES.items():
        try:
            dec=[fn(k,iv,f) for f in testframes]
        except Exception as e:
            continue
        mp4=build(dec,A480); open(BASE+".bf.m4a","wb").write(mp4)
        r=subprocess.run([FF,"-v","error","-i",BASE+".bf.m4a","-f","null","-"],capture_output=True,text=True)
        errs=r.stderr.count("Error submitting packet"); ok=len(testframes)-errs
        res.append((ok,cn,mn,r.stderr.count(chr(10))))
res.sort(reverse=True)
print(f"{'OK':>3} {'cand':32} {'mode':7} stderrlines")
for ok,cn,mn,sl in res[:16]:
    print(f"{ok:3d} {cn:32} {mn:7} {sl}")
